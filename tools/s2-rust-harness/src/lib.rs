//! Shared pieces of the s2-rust drivers: JSON-line events on stdout, JSON-line commands from
//! stdin, and the message pump that runs an S2 session (s2energy-messaging's `S2Connection`)
//! on any `S2Transport`, confirming every received message and forwarding the commands of
//! the test as messages.

use std::io::{self, BufRead, Write};

use serde_json::Value;
use tokio::sync::mpsc::{UnboundedReceiver, UnboundedSender, unbounded_channel};

pub mod pump;

/// Write one JSON event line to stdout.
pub fn emit(value: Value) {
    let stdout = io::stdout();
    let mut out = stdout.lock();
    writeln!(out, "{}", value).ok();
    out.flush().ok();
}

/// Read stdin line by line on a thread and hand every non-empty line to the receiver.
pub fn stdin_lines() -> UnboundedReceiver<String> {
    let (sender, receiver): (UnboundedSender<String>, UnboundedReceiver<String>) = unbounded_channel();
    std::thread::spawn(move || {
        let stdin = io::stdin();
        for line in stdin.lock().lines() {
            match line {
                Ok(line) => {
                    let line = line.trim().to_string();
                    if !line.is_empty() && sender.send(line).is_err() {
                        break;
                    }
                }
                Err(_) => break,
            }
        }
    });
    receiver
}

/// Parse a command line; unparsable lines are reported and skipped.
pub fn parse_command(line: &str) -> Option<Value> {
    match serde_json::from_str::<Value>(line) {
        Ok(value @ Value::Object(_)) => Some(value),
        Ok(_) => {
            emit(serde_json::json!({ "event": "command_error", "error": "the command is not a JSON object", "line": line }));
            None
        }
        Err(error) => {
            emit(serde_json::json!({ "event": "command_error", "error": format!("invalid JSON: {error}"), "line": line }));
            None
        }
    }
}

/// Initialise `tracing` to stderr, honouring RUST_LOG.
pub fn init_tracing() {
    use tracing_subscriber::{EnvFilter, fmt, prelude::*};
    tracing_subscriber::registry()
        .with(fmt::layer().with_writer(io::stderr))
        .with(EnvFilter::from_default_env())
        .init();
}
