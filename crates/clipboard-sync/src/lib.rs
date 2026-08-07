#![forbid(unsafe_code)]

//! Sync protocol primitives that do not perform network transport.

mod diagnostics;
mod error;
mod oss;
mod protocol;
mod recovery_code;
mod remote;
mod transport;
mod webdav;

pub use diagnostics::{
    NoopSyncDiagnostics, RecordingSyncDiagnostics, SyncDiagnostic, SyncDiagnosticErrorCategory,
    SyncDiagnosticOutcome, SyncDiagnosticPhase, SyncDiagnostics,
};
pub use error::SyncError;
pub use oss::{OssStore, parse_oss_error_code};
pub use protocol::{SegmentHeader, SyncEvent, open_segment, seal_segment};
pub use recovery_code::{RecoveryMaterial, decode_recovery_code, encode_recovery_code};
pub use remote::{
    OssConfig, PENDING_OBJECT_SUFFIX, REMOTE_CONFIG_VERSION, RemoteConfig, RemoteSegmentHeader,
    RemoteStore, WebDavConfig, completed_object_name, parse_completed_object_name,
    pending_object_name, validate_remote_segment_header,
};
pub use transport::{DirectoryTransport, SyncTransport};
pub use webdav::WebDavStore;

pub const SYNC_PROTOCOL_VERSION: u8 = 1;
