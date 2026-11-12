use serde_json::json;
use std::{fs, panic, path::Path};

fn main() {
    let arguments: Vec<String> = std::env::args().collect();
    assert_eq!(arguments.len(), 3, "expected event UUID and local output path");
    let event_id = arguments[1].clone();
    assert!(event_id.len() == 36 && event_id.chars().all(|c| c.is_ascii_hexdigit() || c == '-'));
    let output = arguments[2].clone();
    // The synthetic process never opens a socket. Its parent sends this bounded record after exit.
    panic::set_hook(Box::new(move |info| {
        let location = info.location().expect("synthetic panic has source location");
        let file = Path::new(location.file()).file_name().unwrap().to_string_lossy();
        let body = json!({
            "eventId": event_id,
            "errorType": "RustPanic",
            "errorMessage": "Synthetic Rust panic",
            "stackTrace": format!("at panic ({}) line {}:{}", file, location.line(), location.column()),
            "kind": "unhandled", "severity": "fatal", "platform": "windows",
            "osName": "Windows", "sdkVersion": "synthetic-rust-native-json", "release": "rust-panic-probe"
        });
        let encoded = serde_json::to_vec(&body).unwrap();
        assert!(encoded.len() < 4096);
        fs::write(&output, encoded).expect("write disposable synthetic queue record");
    }));
    panic!("Synthetic Rust panic");
}
