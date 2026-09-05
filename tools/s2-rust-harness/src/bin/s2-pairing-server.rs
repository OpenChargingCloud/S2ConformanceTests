//! S2 Connect pairing server driver (s2energy-connection::pairing::Server behind an
//! axum-server TLS listener). Waits for exactly one pairing attempt (or, with --repeated,
//! any number) against the hosted node and reports the outcome as JSON-line events:
//!   {"event":"listening","port":…}
//!   {"event":"paired","remote_node":{…},"remote_endpoint":{…},"role":"CommunicationClient"|"CommunicationServer",
//!    "access_token":"…","initiate_url":…,"root_hash":…}
//!   {"event":"pairing_failed","kind":"…","error":"…"}
//! The pairing token is taken as the ASCII bytes of its text (the S2 Connect 1.0.0 rule) unless
//! --token-encoding base64 asks for s2-rust's own PairingToken::from_str decoding.

use std::{net::SocketAddr, path::PathBuf, str::FromStr, sync::Arc};

use axum_server::tls_rustls::RustlsConfig;
use clap::Parser;
use rustls::pki_types::{CertificateDer, pem::PemObject};
use s2energy_connection::{
    Deployment, EndpointDescription, MessageVersion, NodeDescription, NodeId, Role,
    pairing::{NodeConfig, NodeIdAlias, PairingRole, PairingToken, Server, ServerConfig},
};
use s2_rust_harness::{emit, init_tracing, parse_command, stdin_lines};
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

    /// The deployment of this pairing server: LAN or WAN.
    #[arg(long, default_value = "LAN")]
    deployment: String,

    /// The S2 role of the hosted node: CEM or RM.
    #[arg(long, default_value = "CEM")]
    role: String,

    /// The node identification (UUID).
    #[arg(long)]
    node_id: String,

    /// The node identification alias the client may target.
    #[arg(long)]
    alias: Option<String>,

    /// The pairing token text.
    #[arg(long)]
    token: String,

    /// How the token text becomes bytes: "ascii" (S2 Connect) or "base64" (s2-rust's PairingToken::from_str).
    #[arg(long, default_value = "ascii")]
    token_encoding: String,

    /// The session initiation URL of the hosted node (needed for a CEM).
    #[arg(long)]
    initiate_url: Option<String>,

    /// The supported S2 message versions, comma separated.
    #[arg(long, default_value = "v1.0.0,0.0.2-beta")]
    versions: String,

    /// Allow any number of pairings with the same token.
    #[arg(long)]
    repeated: bool,
}

fn token_bytes(text: &str, encoding: &str) -> Vec<u8> {
    match encoding {
        "ascii"  => text.as_bytes().to_vec(),
        "base64" => PairingToken::from_str(text).expect("s2-rust could not decode the pairing token").as_slice().to_vec(),
        other    => panic!("unknown token encoding '{other}'"),
    }
}

fn pairing_event(pairing: &s2energy_connection::pairing::Pairing) -> serde_json::Value {
    let (role, initiate_url, root_hash) = match &pairing.role {
        PairingRole::CommunicationClient { initiate_url, root_hash } => (
            "CommunicationClient",
            Some(initiate_url.clone()),
            root_hash.as_ref().map(|hash| hex::encode(hash as &[u8])),
        ),
        PairingRole::CommunicationServer => ("CommunicationServer", None, None),
    };
    json!({
        "event":           "paired",
        "remote_node":     pairing.remote_node_description,
        "remote_endpoint": pairing.remote_endpoint_description,
        "role":            role,
        "access_token":    pairing.token.0,
        "initiate_url":    initiate_url,
        "root_hash":       root_hash
    })
}

#[tokio::main(flavor = "current_thread")]
async fn main() {
    init_tracing();
    let args = Args::parse();

    let deployment = match args.deployment.as_str() {
        "LAN" => Deployment::Lan,
        "WAN" => Deployment::Wan,
        other => panic!("unknown deployment '{other}'"),
    };

    let role = match args.role.as_str() {
        "CEM" => Role::Cem,
        "RM"  => Role::Rm,
        other => panic!("unknown role '{other}'"),
    };

    let certificates = CertificateDer::pem_file_iter(&args.cert)
        .expect("the certificate file could not be read")
        .collect::<Result<Vec<_>, _>>()
        .expect("the certificate file is not valid PEM");

    let leaf = certificates.first().expect("the certificate file is empty").clone().into_owned();

    let description = NodeDescription {
        id:                NodeId::try_from(args.node_id.as_str()).expect("the node id must be a UUID"),
        brand:             "s2-rust interop".into(),
        logo_url:          None,
        type_:             "test node".into(),
        model_name:        "s2-rust-harness".into(),
        user_defined_name: None,
        role,
    };

    let versions = args.versions.split(',').filter(|v| !v.is_empty()).map(|v| MessageVersion(v.into())).collect::<Vec<_>>();

    let mut builder = NodeConfig::builder(description.clone(), versions);
    if let Some(url) = &args.initiate_url {
        builder = builder.with_session_initiate_url(url.clone());
    }
    if deployment == Deployment::Lan {
        // The self-signed leaf is its own root (the D13 rule of WWCP_S2): announced as the
        // certificateFingerprint of the connection details so that the peer can pin it.
        builder = builder.with_root_certificate(leaf.clone());
    }
    let config = Arc::new(builder.build().expect("invalid node configuration"));

    let server = Server::new(ServerConfig {
        leaf_certificate:     if deployment == Deployment::Lan { Some(leaf) } else { None },
        endpoint_description: EndpointDescription {
            name:       Some("s2-rust interop endpoint".into()),
            logo_url:   None,
            deployment: Some(deployment),
        },
        advertised_nodes: vec![description],
    });

    let token = PairingToken(token_bytes(&args.token, &args.token_encoding).into_boxed_slice());
    let alias = args.alias.clone().map(NodeIdAlias);

    if args.repeated {
        server
            .allow_pair_repeated(config, alias, token, |result| async move {
                match result {
                    Ok(pairing) => emit(pairing_event(&pairing)),
                    Err(error)  => emit(json!({ "event": "pairing_failed", "kind": format!("{:?}", error.kind()), "error": error.to_string() })),
                }
                Ok::<_, std::io::Error>(())
            })
            .expect("could not enable pairing");
    } else {
        server
            .allow_pair_once(config, alias, token, async move |result| {
                match result {
                    Ok(pairing) => emit(pairing_event(&pairing)),
                    Err(error)  => emit(json!({ "event": "pairing_failed", "kind": format!("{:?}", error.kind()), "error": error.to_string() })),
                }
                Ok::<_, std::io::Error>(())
            })
            .expect("could not enable pairing");
    }

    let tls    = RustlsConfig::from_pem_file(&args.cert, &args.key).await.expect("the TLS certificate or key could not be loaded");
    let addr   = SocketAddr::new(args.bind.parse().expect("invalid bind address"), args.port);
    let router = server.get_router();
    let handle = axum_server::Handle::new();
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
    while let Some(line) = commands.recv().await {
        if let Some(command) = parse_command(&line) {
            match command.get("cmd").and_then(serde_json::Value::as_str) {
                Some("stop") => break,
                other => emit(json!({ "event": "command_error", "error": format!("unknown command {other:?}") })),
            }
        }
    }

    handle.shutdown();
    emit(json!({ "event": "stopped" }));
}
