//! S2 Connect communication server driver (s2energy-connection::communication::Server: the
//! session initiation API and the WebSocket endpoint behind an axum-server TLS listener),
//! seeded with one pairing. Every established S2 session is run through the message pump
//! (see src/pump.rs), which the test drives with `{"cmd":"send","message":…}` and
//! `{"cmd":"close"}`. Events:
//!   {"event":"listening","port":…}
//!   {"event":"access_token","token":…}          the rotated access token after session initiation
//!   {"event":"connected","client":…,"server":…,"message_version":…,"remote_node":…,"remote_endpoint":…}
//!   {"event":"received"|"reception_status"|"sent"|"send_error"|"closed", …}   from the pump
//!   {"event":"unpaired"}                        the client called /unpair

use std::{
    convert::Infallible,
    net::SocketAddr,
    path::PathBuf,
    sync::{Arc, Mutex},
};

use axum_server::tls_rustls::RustlsConfig;
use clap::Parser;
use s2energy_connection::{
    AccessToken, EndpointDescription, MessageVersion, NodeId,
    communication::{NodeConfig, PairingLookup, PairingLookupResult, Server, ServerConfig, ServerPairing, ServerPairingStore},
};
use s2energy_messaging::connection::S2Connection;
use s2_rust_harness::{emit, init_tracing, pump, stdin_lines};
use serde_json::json;

#[derive(Parser)]
struct Args {
    /// The address to bind to.
    #[arg(long, default_value = "127.0.0.1")]
    bind: String,

    /// The TCP port.
    #[arg(long)]
    port: u16,

    /// The TLS certificate chain (PEM).
    #[arg(long)]
    cert: PathBuf,

    /// The TLS private key (PEM, PKCS#8).
    #[arg(long)]
    key: PathBuf,

    /// The host and port under which clients reach this server (used in the websocketUrl), e.g. localhost:8443
    #[arg(long)]
    base_url: String,

    /// The node identification (UUID) of this server node.
    #[arg(long)]
    server_node_id: String,

    /// The node identification (UUID) of the paired client node.
    #[arg(long)]
    client_node_id: String,

    /// The access token of the pairing.
    #[arg(long)]
    access_token: String,

    /// The supported S2 message versions, comma separated.
    #[arg(long, default_value = "v1.0.0,0.0.2-beta")]
    versions: String,
}

struct StoreInner {
    token:    AccessToken,
    config:   Arc<NodeConfig>,
    server:   NodeId,
    client:   NodeId,
    unpaired: bool,
}

#[derive(Clone)]
struct Store(Arc<Mutex<StoreInner>>);

impl ServerPairingStore for Store {
    type Error = Infallible;
    type Pairing<'a>
        = Store
    where
        Self: 'a;

    async fn lookup(&self, request: PairingLookup) -> Result<PairingLookupResult<Self::Pairing<'_>>, Self::Error> {
        let inner = self.0.lock().unwrap();
        if inner.client == request.client && inner.server == request.server {
            if inner.unpaired {
                Ok(PairingLookupResult::Unpaired)
            } else {
                Ok(PairingLookupResult::Pairing(self.clone()))
            }
        } else {
            emit(json!({ "event": "lookup_failed", "client": request.client.to_string(), "server": request.server.to_string() }));
            Ok(PairingLookupResult::NeverPaired)
        }
    }
}

impl ServerPairing for Store {
    type Error = Infallible;

    fn access_token(&self) -> impl AsRef<AccessToken> {
        self.0.lock().unwrap().token.clone()
    }

    fn config(&self) -> impl AsRef<NodeConfig> {
        self.0.lock().unwrap().config.clone()
    }

    async fn set_access_token(&mut self, token: AccessToken) -> Result<(), Self::Error> {
        emit(json!({ "event": "access_token", "token": token.0 }));
        self.0.lock().unwrap().token = token;
        Ok(())
    }

    async fn unpair(self) -> Result<(), Self::Error> {
        emit(json!({ "event": "unpaired" }));
        self.0.lock().unwrap().unpaired = true;
        Ok(())
    }
}

#[tokio::main(flavor = "current_thread")]
async fn main() {
    init_tracing();
    let args = Args::parse();

    let versions = args.versions.split(',').filter(|v| !v.is_empty()).map(|v| MessageVersion(v.into())).collect::<Vec<_>>();

    let store = Store(Arc::new(Mutex::new(StoreInner {
        token:    AccessToken(args.access_token.clone()),
        config:   Arc::new(NodeConfig::builder(versions).build()),
        server:   NodeId::try_from(args.server_node_id.as_str()).expect("the server node id must be a UUID"),
        client:   NodeId::try_from(args.client_node_id.as_str()).expect("the client node id must be a UUID"),
        unpaired: false,
    })));

    let server = Server::new(
        ServerConfig {
            base_url:             args.base_url.clone(),
            endpoint_description: Some(EndpointDescription {
                name:       Some("s2-rust interop communication server".into()),
                logo_url:   None,
                deployment: None,
            }),
        },
        store,
    );

    let tls     = RustlsConfig::from_pem_file(&args.cert, &args.key).await.expect("the TLS certificate or key could not be loaded");
    let addr    = SocketAddr::new(args.bind.parse().expect("invalid bind address"), args.port);
    let router  = server.get_router();
    let handle  = axum_server::Handle::new();
    let serving = handle.clone();

    tokio::spawn(async move {
        axum_server::bind_rustls(addr, tls)
            .handle(serving)
            .serve(router.into_make_service())
            .await
            .expect("the HTTPS server failed");
    });

    let bound = handle.listening().await.expect("the HTTPS server did not start");
    emit(json!({ "event": "listening", "port": bound.port(), "address": bound.ip().to_string() }));

    let mut commands = stdin_lines();

    loop {
        let connection = tokio::select! {
            connection = server.next_connection() => connection,
            line = commands.recv() => {
                match line {
                    Some(line) if line.contains("\"stop\"") => break,
                    Some(_) => continue,
                    None => break,
                }
            }
        };

        let (pairing, info) = connection;

        emit(json!({
            "event":           "connected",
            "client":          pairing.client.to_string(),
            "server":          pairing.server.to_string(),
            "message_version": info.message_version.0,
            "remote_node":     info.remote_node_description,
            "remote_endpoint": info.remote_endpoint_description
        }));

        let mut s2 = S2Connection::new(info.transport);

        match pump::run(&mut s2, &mut commands).await {
            pump::Stopped::Closed          => emit(json!({ "event": "closed", "reason": "transport" })),
            pump::Stopped::CloseRequested  => { s2.disconnect().await; emit(json!({ "event": "closed", "reason": "requested" })); }
            pump::Stopped::StdinEnded      => { s2.disconnect().await; break; }
            pump::Stopped::Command(cmd)    => {
                if cmd.get("cmd").and_then(serde_json::Value::as_str) == Some("stop") {
                    s2.disconnect().await;
                    break;
                }
                emit(json!({ "event": "command_error", "error": format!("unknown command {cmd}") }));
            }
        }
    }

    handle.shutdown();
    emit(json!({ "event": "stopped" }));
}
