//! The message pump: runs an `S2Connection` until the transport closes or the test sends
//! `{"cmd": "close"}`. Every received message is answered with a ReceptionStatus OK (the
//! behaviour of a permissive peer) and reported as `{"event": "received", "message": …}`;
//! every ReceptionStatus for an own message is reported as `{"event": "reception_status", …}`;
//! `{"cmd": "send", "message": <S2 JSON>}` parses the message with the s2-rust data model and
//! sends it, reporting `{"event": "sent", …}` or `{"event": "send_error", …}`.
//!
//! Commands the pump does not understand are handed back to the caller through the returned
//! value of [`run`] (one at a time), so that a driver can add its own commands.
//!
//! The received message is taken out of s2-rust's `UnconfirmedMessage` synchronously
//! (`into_inner`) and the OK is sent afterwards, exactly as `UnconfirmedMessage::confirm`
//! does: this keeps the `select!` free of a cancellation window in which the unconfirmed
//! message could be dropped (which s2-rust turns into a panic).

use s2energy_common::S2Transport;
use s2energy_messaging::{
    common::{Message, ReceptionStatus, ReceptionStatusValues},
    connection::S2Connection,
};
use serde_json::{Value, json};
use tokio::sync::mpsc::UnboundedReceiver;

use crate::{emit, parse_command};

/// Why the pump stopped.
pub enum Stopped {
    /// The transport was closed (by the peer or after an error).
    Closed,
    /// The test asked to close the connection.
    CloseRequested,
    /// stdin ended.
    StdinEnded,
    /// A command the pump does not handle itself.
    Command(Value),
}

enum Step {
    Received(Message),
    ReceiveError(String),
    Command(Option<String>),
}

/// Run the pump on the given connection.
pub async fn run<T: S2Transport>(connection: &mut S2Connection<T>, commands: &mut UnboundedReceiver<String>) -> Stopped {
    loop {
        let step = tokio::select! {
            received = connection.receive_message() => match received {
                Ok(unconfirmed) => Step::Received(unconfirmed.into_inner()),
                Err(error)      => Step::ReceiveError(error.to_string()),
            },
            line = commands.recv() => Step::Command(line),
        };

        match step {
            Step::Received(message) => {
                if let Message::ReceptionStatus(status) = &message {
                    emit(json!({
                        "event":              "reception_status",
                        "subject_message_id": status.subject_message_id,
                        "status":             status.status,
                        "diagnostic_label":   status.diagnostic_label
                    }));
                    continue;
                }

                emit(json!({ "event": "received", "message": message }));

                if let Some(subject_message_id) = message.id() {
                    let status = ReceptionStatus {
                        diagnostic_label:   None,
                        status:             ReceptionStatusValues::Ok,
                        subject_message_id,
                    };
                    if let Err(error) = connection.send_message(status).await {
                        emit(json!({ "event": "confirm_error", "error": error.to_string() }));
                        return Stopped::Closed;
                    }
                }
            }

            Step::ReceiveError(error) => {
                emit(json!({ "event": "receive_error", "error": error }));
                return Stopped::Closed;
            }

            Step::Command(None) => return Stopped::StdinEnded,

            Step::Command(Some(line)) => {
                let Some(command) = parse_command(&line) else { continue };
                match command.get("cmd").and_then(Value::as_str) {
                    Some("send") => {
                        let Some(value) = command.get("message") else {
                            emit(json!({ "event": "command_error", "error": "send needs a 'message'" }));
                            continue;
                        };
                        match serde_json::from_value::<Message>(value.clone()) {
                            Ok(message) => match connection.send_message(message.clone()).await {
                                Ok(()) => emit(json!({ "event": "sent", "message": message })),
                                Err(error) => emit(json!({ "event": "send_error", "error": error.to_string() })),
                            },
                            Err(error) => emit(json!({ "event": "send_error", "error": format!("s2-rust cannot parse the message: {error}") })),
                        }
                    }
                    Some("close") => return Stopped::CloseRequested,
                    _ => return Stopped::Command(command),
                }
            }
        }
    }
}
