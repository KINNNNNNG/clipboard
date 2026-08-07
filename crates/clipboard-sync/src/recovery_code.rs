use std::fmt;

use base64::{Engine as _, engine::general_purpose::URL_SAFE_NO_PAD};
use uuid::Uuid;
use zeroize::{Zeroize, Zeroizing};

use crate::{SYNC_PROTOCOL_VERSION, SyncError};

const RECOVERY_PAYLOAD_LENGTH: usize = 49;
const RECOVERY_CODE_LENGTH: usize = RECOVERY_PAYLOAD_LENGTH + 4;
const RECOVERY_COMPACT_LENGTH: usize = 71;
const RECOVERY_GROUPED_LENGTH: usize = 85;

#[derive(Clone, PartialEq, Eq)]
pub struct RecoveryMaterial {
    pub vault_id: Uuid,
    pub master_key: [u8; 32],
}

impl Drop for RecoveryMaterial {
    fn drop(&mut self) {
        self.master_key.zeroize();
    }
}

impl RecoveryMaterial {
    pub fn new(vault_id: Uuid, master_key: [u8; 32]) -> Self {
        Self {
            vault_id,
            master_key,
        }
    }
}

impl fmt::Debug for RecoveryMaterial {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("RecoveryMaterial")
            .field("vault_id", &self.vault_id)
            .field("master_key", &"[REDACTED]")
            .finish()
    }
}

pub fn encode_recovery_code(material: &RecoveryMaterial) -> Result<String, SyncError> {
    let mut bytes = Zeroizing::new([0_u8; RECOVERY_CODE_LENGTH]);
    bytes[0] = SYNC_PROTOCOL_VERSION;
    bytes[1..17].copy_from_slice(material.vault_id.as_bytes());
    bytes[17..RECOVERY_PAYLOAD_LENGTH].copy_from_slice(&material.master_key);
    let checksum = blake3::hash(&bytes[..RECOVERY_PAYLOAD_LENGTH]);
    bytes[RECOVERY_PAYLOAD_LENGTH..].copy_from_slice(&checksum.as_bytes()[..4]);

    let encoded = Zeroizing::new(URL_SAFE_NO_PAD.encode(*bytes));
    Ok(encoded
        .as_bytes()
        .chunks(5)
        .map(|chunk| std::str::from_utf8(chunk).expect("base64url output is ASCII"))
        .collect::<Vec<_>>()
        .join("-"))
}

pub fn decode_recovery_code(code: &str) -> Result<RecoveryMaterial, SyncError> {
    if code.len() > RECOVERY_GROUPED_LENGTH {
        return Err(SyncError::InvalidRecoveryCode);
    }

    let compact = Zeroizing::new(code.replace('-', ""));
    if compact.len() != RECOVERY_COMPACT_LENGTH {
        return Err(SyncError::InvalidRecoveryCode);
    }

    let bytes = Zeroizing::new(
        URL_SAFE_NO_PAD
            .decode(compact)
            .map_err(|_| SyncError::InvalidRecoveryCode)?,
    );

    if bytes.len() != RECOVERY_CODE_LENGTH {
        return Err(SyncError::InvalidRecoveryCode);
    }

    if bytes[0] != SYNC_PROTOCOL_VERSION {
        return Err(SyncError::UnsupportedRecoveryCodeVersion(bytes[0]));
    }

    let checksum = blake3::hash(&bytes[..RECOVERY_PAYLOAD_LENGTH]);
    if checksum.as_bytes()[..4] != bytes[RECOVERY_PAYLOAD_LENGTH..] {
        return Err(SyncError::InvalidRecoveryCodeChecksum);
    }

    let vault_id = Uuid::from_slice(&bytes[1..17]).map_err(|_| SyncError::InvalidRecoveryCode)?;
    let mut master_key = [0_u8; 32];
    master_key.copy_from_slice(&bytes[17..RECOVERY_PAYLOAD_LENGTH]);

    Ok(RecoveryMaterial::new(vault_id, master_key))
}
