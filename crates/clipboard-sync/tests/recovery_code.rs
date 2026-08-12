use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use clipboard_sync::{
    RecoveryMaterial, SYNC_PROTOCOL_VERSION, SyncError, decode_recovery_code, encode_recovery_code,
};
use uuid::Uuid;

fn material() -> RecoveryMaterial {
    RecoveryMaterial::new(
        Uuid::from_u128(0x1234_5678_90ab_cdef_0123_4567_89ab_cdef),
        [0x5a; 32],
    )
}

#[test]
fn recovery_code_round_trips_with_deterministic_grouping() {
    let recovery_code = encode_recovery_code(&material()).unwrap();
    let groups: Vec<_> = recovery_code.split('-').collect();

    assert_eq!(recovery_code, encode_recovery_code(&material()).unwrap());
    assert!(
        groups[..groups.len() - 1]
            .iter()
            .all(|group| group.len() == 5)
    );
    assert_eq!(groups.last().unwrap().len(), 1);
    assert_eq!(decode_recovery_code(&recovery_code).unwrap(), material());
}

#[test]
fn recovery_code_rejects_changed_character_and_truncation() {
    let recovery_code = encode_recovery_code(&material()).unwrap();
    let replacement = if recovery_code.starts_with('A') {
        'B'
    } else {
        'A'
    };
    let changed = format!("{replacement}{}", &recovery_code[1..]);
    let truncated = &recovery_code[..recovery_code.len() - 1];

    assert!(matches!(
        decode_recovery_code(&changed),
        Err(SyncError::InvalidRecoveryCode)
            | Err(SyncError::InvalidRecoveryCodeChecksum)
            | Err(SyncError::UnsupportedRecoveryCodeVersion(_))
    ));
    assert!(matches!(
        decode_recovery_code(truncated),
        Err(SyncError::InvalidRecoveryCode)
    ));
}

#[test]
fn recovery_code_rejects_tampered_protocol_version() {
    let recovery_code = encode_recovery_code(&material()).unwrap();
    let compact = recovery_code.replace('-', "");
    let mut bytes = URL_SAFE_NO_PAD.decode(compact).unwrap();
    bytes[0] = SYNC_PROTOCOL_VERSION + 1;
    let checksum = blake3::hash(&bytes[..49]);
    bytes[49..].copy_from_slice(&checksum.as_bytes()[..4]);
    let tampered = URL_SAFE_NO_PAD.encode(bytes);

    assert!(matches!(
        decode_recovery_code(&tampered),
        Err(SyncError::UnsupportedRecoveryCodeVersion(version))
            if version == SYNC_PROTOCOL_VERSION + 1
    ));
}

#[test]
fn recovery_code_rejects_valid_base64_with_an_invalid_checksum() {
    let recovery_code = encode_recovery_code(&material()).unwrap();
    let compact = recovery_code.replace('-', "");
    let mut bytes = URL_SAFE_NO_PAD.decode(compact).unwrap();
    bytes[52] ^= 0x01;
    let tampered = URL_SAFE_NO_PAD.encode(bytes);

    assert_eq!(
        decode_recovery_code(&tampered),
        Err(SyncError::InvalidRecoveryCodeChecksum)
    );
}

#[test]
fn recovery_code_rejects_oversized_input() {
    assert_eq!(
        decode_recovery_code(&"A".repeat(86)),
        Err(SyncError::InvalidRecoveryCode)
    );
}

#[test]
fn recovery_material_debug_redacts_the_master_key() {
    let debug = format!("{:?}", material());

    assert!(debug.contains("[REDACTED]"));
    assert!(!debug.contains("5a"));
}
