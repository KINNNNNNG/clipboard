use serde::{Deserialize, Serialize};
use uuid::Uuid;

use crate::{SYNC_PROTOCOL_VERSION, SegmentHeader, SnapshotId, SyncError, VersionVector};

pub const REMOTE_HEADER_VERSION: u8 = 1;
pub const REMOTE_CIPHER_SUITE: &str = "xchacha20-poly1305";
pub const HEADER_NAME: &str = "header.json";

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct RemoteHeader {
    version: u8,
    vault_id: Uuid,
    protocol_version: u8,
    cipher_suite: String,
}

impl RemoteHeader {
    pub fn new(vault_id: Uuid) -> Self {
        Self {
            version: REMOTE_HEADER_VERSION,
            vault_id,
            protocol_version: SYNC_PROTOCOL_VERSION,
            cipher_suite: REMOTE_CIPHER_SUITE.to_owned(),
        }
    }

    pub fn vault_id(&self) -> Uuid {
        self.vault_id
    }

    pub fn validate(&self) -> Result<(), SyncError> {
        if self.version != REMOTE_HEADER_VERSION
            || self.vault_id.is_nil()
            || self.protocol_version != SYNC_PROTOCOL_VERSION
            || self.cipher_suite != REMOTE_CIPHER_SUITE
        {
            return Err(SyncError::UnsupportedProtocolVersion(self.protocol_version));
        }
        Ok(())
    }
}

pub trait RemoteMetadataStore: Send + Sync {
    fn get_header(&self) -> Result<Option<RemoteHeader>, SyncError>;
    fn put_header(&self, header: &RemoteHeader) -> Result<(), SyncError>;
    fn list_device_states(&self) -> Result<Vec<Uuid>, SyncError> {
        Ok(Vec::new())
    }
    fn get_device_state(&self, device_id: Uuid) -> Result<Option<Vec<u8>>, SyncError>;
    fn put_device_state(&self, device_id: Uuid, ciphertext: &[u8]) -> Result<(), SyncError>;
    fn get_snapshot(&self, snapshot_id: SnapshotId) -> Result<Option<Vec<u8>>, SyncError>;
    fn put_snapshot(&self, snapshot_id: SnapshotId, ciphertext: &[u8]) -> Result<(), SyncError>;
    fn list_snapshots(&self) -> Result<Vec<SnapshotId>, SyncError> {
        Ok(Vec::new())
    }
    fn delete_segment(&self, header: &SegmentHeader) -> Result<bool, SyncError>;
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct SnapshotSegment {
    pub header: SegmentHeader,
    pub sequence: u64,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize, Deserialize)]
pub struct SnapshotManifest {
    pub snapshot_id: SnapshotId,
    pub version_vector: VersionVector,
    pub segments: Vec<SnapshotSegment>,
}

pub fn device_state_name(device_id: Uuid) -> String {
    format!("device-{device_id}.state.enc")
}

pub fn parse_device_state_name(name: &str) -> Option<Uuid> {
    let uuid = name.strip_prefix("device-")?.strip_suffix(".state.enc")?;
    Uuid::parse_str(uuid).ok()
}

pub fn snapshot_name(snapshot_id: SnapshotId) -> String {
    format!("snapshot-{}.enc", snapshot_id.0)
}

pub fn parse_snapshot_name(name: &str) -> Option<SnapshotId> {
    let uuid = name.strip_prefix("snapshot-")?.strip_suffix(".enc")?;
    Some(SnapshotId(Uuid::parse_str(uuid).ok()?))
}
