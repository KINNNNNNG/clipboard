use clipboard_sync::{
    RecordingSyncDiagnostics, SyncDiagnostic, SyncDiagnosticPhase, SyncDiagnostics,
};
use uuid::Uuid;

#[test]
fn local_only_rejection_diagnostic_excludes_file_paths_and_content() {
    let diagnostics = RecordingSyncDiagnostics::default();
    let sensitive_path = r"C:\\private\\report.txt";
    let sensitive_content = "do not synchronize this document";

    diagnostics.record(SyncDiagnostic::local_only_rejected(Uuid::nil()));
    let records = diagnostics.records();
    let encoded = serde_json::to_string(&records).unwrap();

    assert_eq!(records.len(), 1);
    assert_eq!(records[0].phase(), SyncDiagnosticPhase::RejectLocalOnly);
    assert!(!encoded.contains(sensitive_path));
    assert!(!encoded.contains("report.txt"));
    assert!(!encoded.contains(sensitive_content));
}

#[test]
fn segment_diagnostics_record_only_hashed_identifiers_and_numeric_metadata() {
    let diagnostics = RecordingSyncDiagnostics::default();

    diagnostics.record(SyncDiagnostic::segment_completed(
        SyncDiagnosticPhase::Upload,
        Uuid::from_u128(1),
        2048,
        3,
    ));
    let encoded = serde_json::to_string(&diagnostics.records()).unwrap();

    assert!(encoded.contains("upload"));
    assert!(!encoded.contains("C:\\remote\\journal"));
    assert!(!encoded.contains("clipboard contents"));
}
