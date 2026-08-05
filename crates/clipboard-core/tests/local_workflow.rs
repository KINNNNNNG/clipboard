use clipboard_core::{CoreCommand, CoreService, IngestText, SearchFilters, SearchRequest};
use clipboard_search::SearchMode;
use tempfile::tempdir;
use uuid::Uuid;

#[test]
fn ingest_persist_reopen_and_search_text() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::new_v4();
    let key = [0x31; 32];
    {
        let mut core = CoreService::open(directory.path(), vault_id, &key).unwrap();
        core.execute(CoreCommand::IngestText(IngestText {
            text: "跨设备剪贴板".into(),
            source_app: "notepad.exe".into(),
            source_app_display_name: Some("记事本".into()),
            captured_ms: 10,
        }))
        .unwrap();
    }

    let mut reopened = CoreService::open(directory.path(), vault_id, &key).unwrap();
    let response = reopened
        .execute(CoreCommand::Search(SearchRequest {
            pattern: "设备".into(),
            mode: SearchMode::Substring,
            filters: SearchFilters::default(),
        }))
        .unwrap();

    let items = response.search_items();
    assert_eq!(items.len(), 1);
    assert_eq!(items[0].preview, "跨设备剪贴板");
    assert_eq!(items[0].source_app, "notepad.exe");
    assert_eq!(items[0].source_app_display_name.as_deref(), Some("记事本"));
}

#[test]
fn invalid_regex_is_returned_without_losing_persisted_history() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::new_v4();
    let key = [0x31; 32];
    let mut core = CoreService::open(directory.path(), vault_id, &key).unwrap();
    core.execute(CoreCommand::IngestText(IngestText {
        text: "clipboard".into(),
        source_app: "notepad.exe".into(),
        captured_ms: 10,
        source_app_display_name: None,
    }))
    .unwrap();

    assert!(
        core.execute(CoreCommand::Search(SearchRequest {
            pattern: "[".into(),
            mode: SearchMode::Regex,
            filters: SearchFilters::default(),
        }))
        .is_err()
    );
    assert_eq!(
        core.execute(CoreCommand::Search(SearchRequest {
            pattern: "clipboard".into(),
            mode: SearchMode::Substring,
            filters: SearchFilters::default(),
        }))
        .unwrap()
        .search_items()
        .len(),
        1
    );
}
