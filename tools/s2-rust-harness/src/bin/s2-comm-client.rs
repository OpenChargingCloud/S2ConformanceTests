//! S2 Connect communication client driver (s2energy-connection::communication::Client):
//! runs session initiation against a communication server with a seeded pairing, opens the
//! WebSocket and runs the S2 session through the message pump (see src/pump.rs). Events:
//!   {"event":"access_tokens","tokens":[…]}     the rotated tokens the client keeps
//!   {"event":"connected","message_version":…,"remote_node":…,"remote_endpoint":…}
//!   {"event":"connect_failed","kind":"…","error":"…"}
//!   {"event":"received"|"reception_status"|"sent"|"send_error"|"closed", …}   from the pump
//! Commands: the pump's send/close plus {"cmd":"unpair"} (calls /unpair with the kept tokens).

use std::{
    convert::Infallible,
    path::PathBuf,
    sync::{Arc, Mutex},
};

use clap::Parser;
use rustls::pki_types::{CertificateDer, pem::PemObject};
use s2energy_connection::{
    AccessToken, CertificateHash, MessageVersion, NodeId,
    communication::{Client, ClientConfig, ClientPairing, NodeConfig},
};
use s2energy_messaging::connection::S2Connection;
use s2_rust_harness::{emit, init_tracing, pump, stdin_lines};
use serde_json::json;

#[derive(Parser)]
struct Args {
    /// The session initiation URL of the communication server, e.g. https://localhost:8443/connection/
    #[arg(long)]
    url: String,

    /// The node identification (UUID) of this client node.
    #[arg(long)]
    client_node_id: String,

    /// The node identification (UUID) of the paired server node.
    #[arg(long)]
    server_node_id: String,

    /// The access token(s) of the pairing, most recent first, comma separated.
    #[arg(long)]
    access_token: String,

    /// Additional trusted certificates (PEM), e.g. the self-signed certificate of the server.
    #[arg(long)]
    ca: Vec<PathBuf>,

    /// The supported S2 message versions, comma separated.
    #[arg(long, default_value = "v1.0.0,0.0.2-beta")]
    versions: String,
}

struct PairingData {
    client_id:        NodeId,
    server_id:        NodeId,
    communication_url: String,
    access_tokens:    Mutex<Vec<AccessToken>>,
}

#[derive(Clone)]
struct Pairing(Arc<PairingData>);

impl ClientPairing for Pairing {
    type Error = Infallible;

    fn client_id(&self) -> NodeId {
        self.0.client_id
    }

    fn server_id(&self) -> NodeId {
        self.0.server_id
    }

    fn access_tokens(&self) -> impl AsRef<[AccessToken]> {
        self.0.access_tokens.lock().unwrap().clone()
    }

    fn communication_url(&self) -> impl AsRef<str> {
        &self.0.communication_url
    }

    fn certificate_hash(&self) -> Option<CertificateHash> {
        None
    }

    async fn set_access_tokens(&mut self, tokens: Vec<AccessToken>) -> Result<(), Self::Error> {
        emit(json!({ "event": "access_tokens", "tokens": tokens.iter().map(|token| token.0.clone()).collect::<Vec<_>>() }));
        *self.0.access_tokens.lock().unwrap() = tokens;
        Ok(())
    }
}

#[tokio::main(flavor = "current_thread")]
async fn main() {
    init_tracing();
    let args = Args::parse();

    let versions = args.versions.split(',').filter(|v| !v.is_empty()).map(|v| MessageVersion(v.into())).collect::<Vec<_>>();

    let additional_certificates = args
        .ca
        .iter()
        .flat_map(|path| {
            CertificateDer::pem_file_iter(path)
                .expect("a CA file could not be read")
                .collect::<Result<Vec<_>, _>>()
                .expect("a CA file is not valid PEM")
        })
        .map(|certificate| certificate.into_owned())
        .collect::<Vec<_>>();

    let client = Client::new(
        ClientConfig { additional_certificates, endpoint_description: None },
        Arc::new(NodeConfig::builder(versions).build()),
    );

    let pairing = Pairing(Arc::new(PairingData {
        client_id:         NodeId::try_from(args.client_node_id.as_str()).expect("the client node id must be a UUID"),
        server_id:         NodeId::try_from(args.server_node_id.as_str()).expect("the server node id must be a UUID"),
        communication_url: args.url.clone(),
        access_tokens:     Mutex::new(args.access_token.split(',').filter(|t| !t.is_empty()).map(|t| AccessToken(t.into())).collect()),
    }));

    let info = match client.connect(pairing.clone()).await {
        Ok(info) => info,
        Err(error) => {
            emit(json!({ "event": "connect_failed", "kind": format!("{:?}", error.kind()), "error": error.to_string() }));
            std::process::exit(1);
        }
    };

    emit(json!({
        "event":           "connected",
        "message_version": info.message_version.0,
        "remote_node":     info.remote_node_description,
        "remote_endpoint": info.remote_endpoint_description
    }));

    let mut s2       = S2Connection::new(info.transport);
    let mut commands = stdin_lines();

    loop {
        match pump::run(&mut s2, &mut commands).await {
            pump::Stopped::Closed         => { emit(json!({ "event": "closed", "reason": "transport" })); break; }
            pump::Stopped::CloseRequested => { s2.disconnect().await; emit(json!({ "event": "closed", "reason": "requested" })); break; }
            pump::Stopped::StdinEnded     => { s2.disconnect().await; break; }
            pump::Stopped::Command(cmd)   => match cmd.get("cmd").and_then(serde_json::Value::as_str) {
                Some("unpair") => match client.unpair(pairing.clone()).await {
                    Ok(())     => emit(json!({ "event": "unpaired" })),
                    Err(error) => emit(json!({ "event": "unpair_failed", "kind": format!("{:?}", error.kind()), "error": error.to_string() })),
                },
                other => emit(json!({ "event": "command_error", "error": format!("unknown command {other:?}") })),
            },
        }
    }

    emit(json!({ "event": "stopped" }));
}
