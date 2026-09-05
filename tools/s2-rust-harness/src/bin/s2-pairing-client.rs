//! S2 Connect pairing client driver (s2energy-connection::pairing::Client). Runs one
//! pairing attempt against a remote pairing server and reports the outcome:
//!   {"event":"paired","remote_node":{…},"remote_endpoint":{…},"role":…,"access_token":…,"initiate_url":…,"root_hash":…}
//!   {"event":"pairing_failed","kind":"…","error":"…"}
//! The pairing token is taken as the ASCII bytes of its text (the S2 Connect 1.0.0 rule) unless
//! --token-encoding base64 asks for s2-rust's own PairingToken::from_str decoding.

use std::{path::PathBuf, str::FromStr};

use clap::Parser;
use rustls::pki_types::{CertificateDer, pem::PemObject};
use s2energy_connection::{
    Deployment, EndpointDescription, MessageVersion, NodeDescription, NodeId, Role,
    pairing::{Client, ClientConfig, NodeConfig, NodeIdAlias, PairingRemote, PairingRole, PairingToken, RemoteNodeIdentifier},
};
use s2_rust_harness::{emit, init_tracing};
use serde_json::json;

#[derive(Parser)]
struct Args {
    /// The pairing URL of the remote endpoint, e.g. https://localhost:8443/pairing/
    #[arg(long)]
    url: String,

    /// The deployment of the remote pairing server: LAN or WAN (selects the challenge-response formula).
    #[arg(long, default_value = "LAN")]
    deployment: String,

    /// The deployment of the local endpoint: LAN or WAN.
    #[arg(long, default_value = "LAN")]
    endpoint_deployment: String,

    /// The S2 role of the local node: RM or CEM.
    #[arg(long, default_value = "RM")]
    role: String,

    /// The local node identification (UUID).
    #[arg(long)]
    node_id: String,

    /// The session initiation URL of the local node (needed for a CEM or a WAN RM).
    #[arg(long)]
    initiate_url: Option<String>,

    /// The pairing token text.
    #[arg(long)]
    token: String,

    /// How the token text becomes bytes: "ascii" (S2 Connect) or "base64" (s2-rust's PairingToken::from_str).
    #[arg(long, default_value = "ascii")]
    token_encoding: String,

    /// The node identification alias of the targeted remote node.
    #[arg(long)]
    remote_alias: Option<String>,

    /// The node identification (UUID) of the targeted remote node.
    #[arg(long)]
    remote_id: Option<String>,

    /// Additional trusted certificates (PEM), e.g. the self-signed certificate of the remote server.
    #[arg(long)]
    ca: Vec<PathBuf>,

    /// The supported S2 message versions, comma separated.
    #[arg(long, default_value = "v1.0.0,0.0.2-beta")]
    versions: String,
}

fn deployment(text: &str) -> Deployment {
    match text {
        "LAN" => Deployment::Lan,
        "WAN" => Deployment::Wan,
        other => panic!("unknown deployment '{other}'"),
    }
}

#[tokio::main(flavor = "current_thread")]
async fn main() {
    init_tracing();
    let args = Args::parse();

    let token_bytes: Vec<u8> = match args.token_encoding.as_str() {
        "ascii"  => args.token.as_bytes().to_vec(),
        "base64" => PairingToken::from_str(&args.token).expect("s2-rust could not decode the pairing token").as_slice().to_vec(),
        other    => panic!("unknown token encoding '{other}'"),
    };

    let role = match args.role.as_str() {
        "CEM" => Role::Cem,
        "RM"  => Role::Rm,
        other => panic!("unknown role '{other}'"),
    };

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

    let mut builder = NodeConfig::builder(description, versions);
    if let Some(url) = &args.initiate_url {
        builder = builder.with_session_initiate_url(url.clone());
    }
    let config = builder.build().expect("invalid node configuration");

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

    let client = Client::new(ClientConfig {
        pairing_deployment:   deployment(&args.deployment),
        endpoint_description: EndpointDescription {
            name:       Some("s2-rust interop client endpoint".into()),
            logo_url:   None,
            deployment: Some(deployment(&args.endpoint_deployment)),
        },
        additional_certificates,
    })
    .expect("could not create the pairing client");

    let remote_id = match (&args.remote_alias, &args.remote_id) {
        (Some(alias), _) => RemoteNodeIdentifier::Alias(NodeIdAlias(alias.clone())),
        (None, Some(id)) => RemoteNodeIdentifier::Id(NodeId::try_from(id.as_str()).expect("the remote node id must be a UUID")),
        (None, None)     => RemoteNodeIdentifier::None,
    };

    let result = client
        .pair(
            &config,
            PairingRemote { url: args.url.clone(), id: remote_id },
            &token_bytes,
            async |pairing| {
                let (role, initiate_url, root_hash) = match &pairing.role {
                    PairingRole::CommunicationClient { initiate_url, root_hash } => (
                        "CommunicationClient",
                        Some(initiate_url.clone()),
                        root_hash.as_ref().map(|hash| hex::encode(hash as &[u8])),
                    ),
                    PairingRole::CommunicationServer => ("CommunicationServer", None, None),
                };
                emit(json!({
                    "event":           "paired",
                    "remote_node":     pairing.remote_node_description,
                    "remote_endpoint": pairing.remote_endpoint_description,
                    "role":            role,
                    "access_token":    pairing.token.0,
                    "initiate_url":    initiate_url,
                    "root_hash":       root_hash
                }));
                Ok::<_, std::convert::Infallible>(())
            },
        )
        .await;

    match result {
        Ok(()) => emit(json!({ "event": "finished" })),
        Err(error) => {
            emit(json!({ "event": "pairing_failed", "kind": format!("{:?}", error.kind()), "error": error.to_string() }));
            std::process::exit(1);
        }
    }
}
