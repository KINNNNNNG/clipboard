use clipboard_core::{ApiRequest, CoreCommand, CoreError};

#[test]
fn versioned_request_deserializes_flat_command_envelope() {
    let request: ApiRequest = serde_json::from_str(
        r#"{"api_version":1,"type":"ingest_text","payload":{"text":"hello","source_app":"notepad.exe","captured_ms":10}}"#,
    )
    .unwrap();

    match request.validate().unwrap() {
        CoreCommand::IngestText(command) => {
            assert_eq!(command.text, "hello");
            assert_eq!(command.source_app, "notepad.exe");
            assert_eq!(command.captured_ms, 10);
        }
        _ => panic!("expected ingest_text command"),
    }
}

#[test]
fn unsupported_api_version_is_rejected() {
    let request: ApiRequest = serde_json::from_str(
        r#"{"api_version":2,"type":"search","payload":{"pattern":"x","mode":"substring"}}"#,
    )
    .unwrap();

    assert!(matches!(
        request.validate(),
        Err(CoreError::UnsupportedApiVersion(2))
    ));
}

#[test]
fn missing_api_version_is_invalid_json_shape() {
    assert!(
        serde_json::from_str::<ApiRequest>(
            r#"{"type":"search","payload":{"pattern":"x","mode":"substring"}}"#,
        )
        .is_err()
    );
}
