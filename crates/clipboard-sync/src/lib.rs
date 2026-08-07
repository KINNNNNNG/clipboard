#![forbid(unsafe_code)]

//! Sync protocol primitives that do not perform network transport.

mod diagnostics;
mod error;
mod protocol;
mod recovery_code;
mod transport;

pub use diagnostics::{
    NoopSyncDiagnostics, RecordingSyncDiagnostics, SyncDiagnostic, SyncDiagnosticErrorCategory,
    SyncDiagnosticOutcome, SyncDiagnosticPhase, SyncDiagnostics,
};
pub use error::SyncError;
pub use protocol::{SegmentHeader, SyncEvent, open_segment, seal_segment};
pub use recovery_code::{RecoveryMaterial, decode_recovery_code, encode_recovery_code};
pub use transport::{DirectoryTransport, SyncTransport};

pub const SYNC_PROTOCOL_VERSION: u8 = 1;
