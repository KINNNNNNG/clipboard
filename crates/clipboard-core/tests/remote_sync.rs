use clipboard_core::{ApiRequest, CoreCommand};

#[test]
fn remote_sync_commands_deserialize_versioned_webdav_configuration_without_debug_leaks() {
    let input = r#"{
        "api_version": 1,
        "type": "sync_remote",
        "payload": {
            "device_id": "00000000-0000-0000-0000-000000000001",
            "remote": {
                "provider": "webdav",
                "version": 1,
                "endpoint": "https://sync.example.test/root",
                "username": "alice",
                "password": "secret"
            }
        }
    }"#;

    let command = serde_json::from_str::<ApiRequest>(input)
        .unwrap()
        .validate()
        .unwrap();
    assert!(matches!(command, CoreCommand::SyncRemote(_)));
    let debug = format!("{command:?}");
    assert!(!debug.contains("sync.example.test"));
    assert!(!debug.contains("alice"));
    assert!(!debug.contains("secret"));
}

#[test]
fn probe_remote_deserializes_without_a_device_or_sync_side_effects() {
    let input = r#"{
        "api_version": 1,
        "type": "probe_remote",
        "payload": {
            "remote": {
                "provider": "oss",
                "version": 1,
                "endpoint": "https://oss.example.test",
                "region": "cn-hangzhou",
                "bucket": "bucket",
                "prefix": "encrypted/segments",
                "access_key_id": "AKIDEXAMPLE",
                "access_key_secret": "secret"
            }
        }
    }"#;

    let command = serde_json::from_str::<ApiRequest>(input)
        .unwrap()
        .validate()
        .unwrap();
    assert!(matches!(command, CoreCommand::ProbeRemote(_)));
    let debug = format!("{command:?}");
    assert!(!debug.contains("oss.example.test"));
    assert!(!debug.contains("AKIDEXAMPLE"));
    assert!(!debug.contains("secret"));
}
