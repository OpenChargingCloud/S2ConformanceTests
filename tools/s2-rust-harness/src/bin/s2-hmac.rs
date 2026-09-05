//! Challenge-response driver: computes the pairing challenge response the way s2-rust does
//! (HMAC-SHA256 keyed with the challenge over the token bytes, followed by the SHA-256 leaf
//! certificate fingerprint for LAN servers and nothing for WAN servers). The pairing token
//! text is decoded exactly like `s2energy_connection::pairing::PairingToken::from_str`
//! (standard Base64) unless `--token-bytes ascii` is given.

use std::str::FromStr;

use base64::{Engine as _, engine::general_purpose::STANDARD};
use clap::Parser;
use hmac::{Hmac, Mac};
use s2energy_connection::pairing::PairingToken;
use sha2::Sha256;

#[derive(Parser)]
struct Args {
    /// The challenge (the HMAC key), standard Base64.
    #[arg(long)]
    challenge: String,

    /// The pairing token text.
    #[arg(long)]
    token: String,

    /// How the token text becomes bytes: "base64" (s2-rust) or "ascii".
    #[arg(long, default_value = "base64")]
    token_bytes: String,

    /// The SHA-256 fingerprint of the TLS leaf certificate of a LAN pairing server (hex, with or without colons).
    #[arg(long)]
    fingerprint: Option<String>,
}

fn main() {
    let args = Args::parse();

    let challenge = STANDARD.decode(&args.challenge).expect("the challenge must be standard Base64");

    let token_bytes: Vec<u8> = match args.token_bytes.as_str() {
        "ascii"  => args.token.as_bytes().to_vec(),
        "base64" => PairingToken::from_str(&args.token).expect("s2-rust could not decode the pairing token").as_slice().to_vec(),
        other    => panic!("unknown token byte encoding '{other}'"),
    };

    let mut mac = Hmac::<Sha256>::new_from_slice(&challenge).expect("HMAC accepts any key size");
    mac.update(&token_bytes);

    let formula = match &args.fingerprint {
        Some(fingerprint) => {
            let bytes = hex::decode(fingerprint.replace(':', "")).expect("the fingerprint must be hex");
            mac.update(&bytes);
            "HMAC(C, T || F)"
        }
        None => "HMAC(C, T)",
    };

    let response = mac.finalize().into_bytes();

    println!("{}", serde_json::json!({
        "response":    STANDARD.encode(response),
        "token_bytes": hex::encode(&token_bytes),
        "formula":     formula
    }));
}
