//! JSON round-trip driver: reads one request per line from stdin, `{"id": <any>, "message": <S2 JSON>}`,
//! deserialises the message with the s2-rust `Message` type (s2energy-messaging), re-serialises it
//! and answers with `{"id": <same>, "ok": true, "message": <re-serialised>}` or
//! `{"id": <same>, "ok": false, "error": "<serde error>"}`.

use std::io::{self, BufRead, Write};

use s2energy_messaging::common::Message;
use serde_json::{Value, json};

fn main() {
    let stdin  = io::stdin();
    let stdout = io::stdout();
    let mut out = stdout.lock();

    writeln!(out, "{}", json!({ "event": "ready", "s2_schema_version": s2energy_messaging::s2_schema_version().to_string() })).ok();
    out.flush().ok();

    for line in stdin.lock().lines() {
        let line = match line {
            Ok(line) => line,
            Err(_)   => break,
        };
        let line = line.trim();
        if line.is_empty() {
            continue;
        }
        if line == "__exit__" {
            break;
        }

        let response = match serde_json::from_str::<Value>(line) {
            Ok(Value::Object(request)) => {
                let id = request.get("id").cloned().unwrap_or(Value::Null);
                match request.get("message") {
                    Some(message) => match serde_json::from_value::<Message>(message.clone()) {
                        Ok(message) => json!({ "id": id, "ok": true,  "message": message }),
                        Err(error)  => json!({ "id": id, "ok": false, "error": error.to_string() }),
                    },
                    None => json!({ "id": id, "ok": false, "error": "the request has no 'message'" }),
                }
            }
            Ok(_)      => json!({ "id": Value::Null, "ok": false, "error": "the request is not a JSON object" }),
            Err(error) => json!({ "id": Value::Null, "ok": false, "error": format!("the request is not valid JSON: {error}") }),
        };

        writeln!(out, "{}", response).ok();
        out.flush().ok();
    }
}
