use clipboard_core::{CoreCommand, CoreService, IngestText, SearchRequest};
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
            captured_ms: 10,
        }))
        .unwrap();
    }

    let mut reopened = CoreService::open(directory.path(), vault_id, &key).unwrap();
    let response = reopened
        .execute(CoreCommand::Search(SearchRequest {
            pattern: "设备".into(),
            mode: SearchMode::Substring,
        }))
        .unwrap();

    let items = response.search_items();
    assert_eq!(items.len(), 1);
    assert_eq!(items[0].preview, "跨设备剪贴板");
    assert_eq!(items[0].source_app, "notepad.exe");
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
    }))
    .unwrap();

    assert!(
        core.execute(CoreCommand::Search(SearchRequest {
            pattern: "[".into(),
            mode: SearchMode::Regex,
        }))
        .is_err()
    );
    assert_eq!(
        core.execute(CoreCommand::Search(SearchRequest {
            pattern: "clipboard".into(),
            mode: SearchMode::Substring,
        }))
        .unwrap()
        .search_items()
        .len(),
        1
    );
}
