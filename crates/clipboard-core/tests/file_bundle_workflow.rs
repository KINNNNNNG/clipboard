use clipboard_core::{
    CoreCommand, CoreResponse, CoreService, IngestFileBundle, ReadFileBundle, SearchFilters,
    SearchRequest,
};
use clipboard_crypto::{KeyPurpose, VaultKey};
use clipboard_domain::{ClipboardContent, ClipboardItem, FileBundle, FileEntry, FileEntryKind};
use clipboard_search::SearchMode;
use clipboard_storage::Database;
use rusqlite::Connection;
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x71; 32];

#[test]
fn normalized_file_bundle_reuses_item_id_stays_local_and_can_be_read() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(201);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();

    let first_id = mutation_id(
        core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: vec![FileEntry::file("C:\\Docs\\a.txt".into(), 42, 100)],
            source_app: "explorer.exe".into(),
            captured_ms: 100,
            source_app_display_name: None,
        }))
        .unwrap(),
    );
    let second_id = mutation_id(
        core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: vec![FileEntry::file("c:/docs/A.txt/".into(), 42, 100)],
            source_app: "explorer.exe".into(),
            captured_ms: 200,
            source_app_display_name: None,
        }))
        .unwrap(),
    );

    assert_eq!(first_id, second_id);
    assert_eq!(
        Database::open(&directory.path().join("history.db"), &KEY)
            .unwrap()
            .outbox()
            .pending_count()
            .unwrap(),
        0
    );

    let response = core
        .execute(CoreCommand::ReadFileBundle(ReadFileBundle {
            item_id: first_id,
        }))
        .unwrap();
    let CoreResponse::FileBundle { item_id, entries } = response else {
        panic!("expected file bundle response");
    };
    assert_eq!(item_id, first_id);
    assert_eq!(entries.len(), 1);
    assert_eq!(entries[0].path, "c:\\docs\\a.txt");
    assert_eq!(entries[0].kind, FileEntryKind::File);
    assert_eq!(entries[0].size, 42);
    assert_eq!(entries[0].modified_ms, 100);
}

#[test]
fn invalid_file_bundles_fail_before_storage() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(202);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();

    for entries in [
        vec![],
        vec![FileEntry::file("relative\\a.txt".into(), 1, 1)],
        vec![FileEntry::file("   ".into(), 1, 1)],
        vec![
            FileEntry::file("C:\\Docs\\a.txt".into(), 1, 1),
            FileEntry::file("c:/docs/A.txt/".into(), 1, 1),
        ],
    ] {
        assert!(
            core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
                entries,
                source_app: "explorer.exe".into(),
                captured_ms: 100,
                source_app_display_name: None,
            }))
            .is_err()
        );
    }

    let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
    assert!(database.items().list().unwrap().is_empty());
    assert_eq!(database.outbox().pending_count().unwrap(), 0);
}

#[test]
fn file_bundle_search_returns_a_summary_without_exposing_full_paths() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(205);
    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();

    core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
        entries: vec![
            FileEntry::file("C:\\Docs\\report.docx".into(), 42, 100),
            FileEntry::directory("C:\\Photos".into(), 100),
        ],
        source_app: "explorer.exe".into(),
        source_app_display_name: Some("文件资源管理器".into()),
        captured_ms: 100,
    }))
    .unwrap();

    let response = core
        .execute(CoreCommand::Search(SearchRequest {
            pattern: "C:\\Docs\\report".into(),
            mode: SearchMode::Substring,
            filters: SearchFilters::default(),
        }))
        .unwrap();
    let item = &response.search_items()[0];

    assert_eq!(item.kind, "file_bundle");
    assert_eq!(item.preview, "report.docx");
    assert_eq!(item.file_count, Some(2));
    assert_eq!(item.representative_name.as_deref(), Some("report.docx"));
    assert_eq!(item.representative_kind.as_deref(), Some("file"));
    assert_eq!(
        item.source_app_display_name.as_deref(),
        Some("文件资源管理器")
    );
    let json = serde_json::to_string(item).unwrap();
    assert!(!json.contains("C:\\Docs\\report.docx"));
}

#[test]
fn legacy_file_bundle_fingerprint_is_migrated_without_duplicate_item() {
    let directory = tempdir().unwrap();
    let database_path = directory.path().join("history.db");
    let vault_id = Uuid::from_u128(203);
    let item_id = Uuid::from_u128(204);
    let legacy_fingerprint = VaultKey::from_bytes(KEY)
        .derive(vault_id, KeyPurpose::Fingerprint)
        .unwrap()
        .keyed_hash(br#"[{"path":"c:\\docs\\a.txt","kind":"File","size":42,"modified_ms":100}]"#);
    {
        let database = Database::open(&database_path, &KEY).unwrap();
        let mut item = ClipboardItem::new(
            item_id,
            vault_id,
            ClipboardContent::FileBundle(
                FileBundle::new(vec![FileEntry::file("C:\\Docs\\a.txt".into(), 42, 100)]).unwrap(),
            ),
            "explorer.exe".into(),
            100,
        );
        item.content_fingerprint = Some(legacy_fingerprint);
        database.items().insert(&item).unwrap();
    }
    let connection = Connection::open(&database_path).unwrap();
    connection
        .pragma_update(None, "key", format!("x'{}'", hex::encode(KEY)))
        .unwrap();
    connection
        .execute(
            "UPDATE clipboard_items SET content_json = ?1 WHERE id = ?2",
            rusqlite::params![
                r#"{"FileBundle":{"entries":[{"path":"C:\\Docs\\a.txt","kind":"File","size":42,"modified_ms":100}]}}"#,
                item_id,
            ],
        )
        .unwrap();

    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let reused_id = mutation_id(
        core.execute(CoreCommand::IngestFileBundle(IngestFileBundle {
            entries: vec![FileEntry::file("c:/docs/a.txt/".into(), 42, 100)],
            source_app: "explorer.exe".into(),
            captured_ms: 200,
            source_app_display_name: None,
        }))
        .unwrap(),
    );

    assert_eq!(reused_id, item_id);
    let expected_fingerprint = VaultKey::from_bytes(KEY)
        .derive(vault_id, KeyPurpose::Fingerprint)
        .unwrap()
        .keyed_hash(br#"[{"path":"c:\\docs\\a.txt","kind":"file","size":42,"modified_ms":100}]"#);
    let stored = Database::open(&database_path, &KEY)
        .unwrap()
        .items()
        .list_all()
        .unwrap();
    assert_eq!(stored.len(), 1);
    assert_eq!(stored[0].content_fingerprint, Some(expected_fingerprint));
}

fn mutation_id(response: CoreResponse) -> Uuid {
    match response {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}
