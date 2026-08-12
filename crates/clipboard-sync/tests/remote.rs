use clipboard_sync::{
    OssConfig, RemoteConfig, RemoteSegmentHeader, SegmentHeader, SyncError, WebDavConfig,
    completed_object_name, parse_completed_object_name, pending_object_name,
    validate_remote_segment_header,
};
use uuid::Uuid;

fn header() -> SegmentHeader {
    SegmentHeader {
        protocol_version: 1,
        vault_id: Uuid::from_u128(1),
        device_id: Uuid::from_u128(2),
        segment_id: Uuid::from_u128(3),
    }
}

#[test]
fn remote_config_debug_and_errors_do_not_expose_credentials_or_endpoints() {
    let config = RemoteConfig::webdav("https://sync.example.test/root", "alice", "secret");
    let debug = format!("{config:?}");

    assert!(!debug.contains("secret"));
    assert!(!debug.contains("sync.example.test"));
    assert!(!debug.contains("alice"));

    for error in [
        SyncError::Authentication,
        SyncError::Conflict,
        SyncError::RateLimited,
        SyncError::RemoteUnavailable,
    ] {
        let display = error.to_string();
        assert!(!display.contains("secret"));
        assert!(!display.contains("sync.example.test"));
        assert!(!display.contains("alice"));
    }
}

#[test]
fn provider_configs_debug_do_not_expose_webdav_or_oss_details() {
    let webdav = WebDavConfig::new("https://sync.example.test/root", "alice", "secret");
    let oss = OssConfig::new(
        "https://oss.example.test",
        "cn-hangzhou",
        "private-bucket",
        "encrypted/segments",
        "AKIDEXAMPLE",
        "oss-secret",
    );

    let webdav_debug = format!("{webdav:?}");
    for sensitive in ["sync.example.test", "alice", "secret"] {
        assert!(!webdav_debug.contains(sensitive));
    }

    let oss_debug = format!("{oss:?}");
    for sensitive in [
        "oss.example.test",
        "private-bucket",
        "AKIDEXAMPLE",
        "oss-secret",
    ] {
        assert!(!oss_debug.contains(sensitive));
    }
}

#[test]
fn remote_config_rejects_unknown_versions_without_echoing_configuration() {
    let source = r#"{
        "provider": "webdav",
        "version": 2,
        "endpoint": "https://sync.example.test/root",
        "username": "alice",
        "password": "secret"
    }"#;

    let error = serde_json::from_str::<RemoteConfig>(source).unwrap_err();
    let display = error.to_string();
    assert!(display.contains("unsupported remote configuration version"));
    for sensitive in ["sync.example.test", "alice", "secret"] {
        assert!(!display.contains(sensitive));
    }
}

#[test]
fn oss_config_rejects_unknown_versions_without_echoing_configuration() {
    let source = r#"{
        "provider": "oss",
        "version": 2,
        "endpoint": "https://oss.example.test",
        "region": "cn-hangzhou",
        "bucket": "private-bucket",
        "prefix": "encrypted/segments",
        "access_key_id": "AKIDEXAMPLE",
        "access_key_secret": "oss-secret"
    }"#;

    let error = serde_json::from_str::<RemoteConfig>(source).unwrap_err();
    let display = error.to_string();
    assert!(display.contains("unsupported remote configuration version"));
    for sensitive in [
        "oss.example.test",
        "private-bucket",
        "AKIDEXAMPLE",
        "oss-secret",
    ] {
        assert!(!display.contains(sensitive));
    }
}

#[test]
fn object_names_use_completed_suffix_and_pending_names_are_not_completed() {
    let completed = completed_object_name(&header()).unwrap();
    let pending = pending_object_name(&header()).unwrap();

    assert_eq!(
        completed,
        "01-00000000-0000-0000-0000-000000000001-00000000-0000-0000-0000-000000000002-00000000-0000-0000-0000-000000000003.enc"
    );
    assert_eq!(pending, format!("{completed}.pending"));
    assert_eq!(
        parse_completed_object_name(&completed),
        Some(RemoteSegmentHeader::try_from(header()).unwrap())
    );
    assert_eq!(parse_completed_object_name(&pending), None);
    assert_eq!(
        parse_completed_object_name(&completed.replacen("01-", "1-", 1)),
        None
    );
    assert_eq!(
        parse_completed_object_name(
            "02-00000000-0000-0000-0000-000000000001-00000000-0000-0000-0000-000000000002-00000000-0000-0000-0000-000000000003.enc"
        ),
        None
    );
}

#[test]
fn object_names_reject_unsupported_protocol_versions() {
    let unsupported = SegmentHeader {
        protocol_version: 2,
        ..header()
    };

    assert_eq!(
        completed_object_name(&unsupported),
        Err(SyncError::UnsupportedProtocolVersion(2))
    );
    assert_eq!(
        validate_remote_segment_header(&unsupported),
        Err(SyncError::UnsupportedProtocolVersion(2))
    );
    assert_eq!(
        RemoteSegmentHeader::try_from(unsupported),
        Err(SyncError::UnsupportedProtocolVersion(2))
    );
}
