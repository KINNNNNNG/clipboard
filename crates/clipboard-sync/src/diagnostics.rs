use std::sync::Mutex;

use serde::Serialize;
use uuid::Uuid;

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SyncDiagnosticPhase {
    RejectLocalOnly,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SyncDiagnosticOutcome {
    Rejected,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SyncDiagnosticErrorCategory {
    LocalOnly,
}

#[derive(Clone, Debug, PartialEq, Eq, Serialize)]
pub struct SyncDiagnostic {
    phase: SyncDiagnosticPhase,
    outcome: SyncDiagnosticOutcome,
    error_category: Option<SyncDiagnosticErrorCategory>,
    item_id_hash: Option<String>,
    count: Option<u64>,
    ciphertext_length: Option<u64>,
    duration_ms: Option<u64>,
    retry: Option<u32>,
}

impl SyncDiagnostic {
    pub fn local_only_rejected(item_id: Uuid) -> Self {
        Self {
            phase: SyncDiagnosticPhase::RejectLocalOnly,
            outcome: SyncDiagnosticOutcome::Rejected,
            error_category: Some(SyncDiagnosticErrorCategory::LocalOnly),
            item_id_hash: Some(short_identifier_hash(item_id)),
            count: Some(1),
            ciphertext_length: None,
            duration_ms: None,
            retry: None,
        }
    }

    pub fn phase(&self) -> SyncDiagnosticPhase {
        self.phase
    }
}

pub trait SyncDiagnostics: Send + Sync {
    fn record(&self, diagnostic: SyncDiagnostic);
}

#[derive(Clone, Copy, Debug, Default)]
pub struct NoopSyncDiagnostics;

impl SyncDiagnostics for NoopSyncDiagnostics {
    fn record(&self, _: SyncDiagnostic) {}
}

#[derive(Debug, Default)]
pub struct RecordingSyncDiagnostics {
    records: Mutex<Vec<SyncDiagnostic>>,
}

impl RecordingSyncDiagnostics {
    pub fn records(&self) -> Vec<SyncDiagnostic> {
        self.records
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone()
    }
}

impl SyncDiagnostics for RecordingSyncDiagnostics {
    fn record(&self, diagnostic: SyncDiagnostic) {
        self.records
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .push(diagnostic);
    }
}

fn short_identifier_hash(item_id: Uuid) -> String {
    blake3::hash(item_id.as_bytes()).to_hex()[..12].to_owned()
}
