#![forbid(unsafe_code)]

//! Sync protocol primitives that do not perform network transport.

mod diagnostics;
mod error;
mod recovery_code;

pub use diagnostics::{
    NoopSyncDiagnostics, RecordingSyncDiagnostics, SyncDiagnostic, SyncDiagnosticErrorCategory,
    SyncDiagnosticOutcome, SyncDiagnosticPhase, SyncDiagnostics,
};
pub use error::SyncError;
pub use recovery_code::{RecoveryMaterial, decode_recovery_code, encode_recovery_code};

pub const SYNC_PROTOCOL_VERSION: u8 = 1;
