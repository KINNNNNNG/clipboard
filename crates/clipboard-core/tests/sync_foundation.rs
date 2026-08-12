use clipboard_core::{
    CoreCommand, CoreResponse, CoreService, DeleteRequest, IngestText, SearchFilters,
    SearchRequest, SyncDirectory,
};
use clipboard_crypto::{KeyPurpose, VaultKey};
use clipboard_domain::{
    ClipboardContent, ClipboardItem, DeleteState, FavoriteState, FileBundle, FileEntry, Hlc,
};
use clipboard_search::SearchMode;
use clipboard_storage::Database;
use clipboard_sync::{
    DirectoryTransport, RecordingSyncDiagnostics, SYNC_PROTOCOL_VERSION, SegmentHeader, SyncEvent,
    SyncTransport, seal_segment,
};
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x51; 32];
const VAULT_ID: Uuid = Uuid::from_u128(0x41);

#[test]
fn two_cores_converge_after_offline_text_events_are_synced_in_reverse_order() {
    let remote = tempdir().unwrap();
    let first_directory = tempdir().unwrap();
    let second_directory = tempdir().unwrap();
    let mut first = CoreService::open(first_directory.path(), VAULT_ID, &KEY).unwrap();
    let mut second = CoreService::open(second_directory.path(), VAULT_ID, &KEY).unwrap();

    ingest(&mut first, "from first", 100);
    ingest(&mut second, "from second", 200);

    sync(&mut second, remote.path(), 2).unwrap();
    sync(&mut first, remote.path(), 1).unwrap();
    sync(&mut second, remote.path(), 2).unwrap();

    assert_eq!(previews(&mut first), previews(&mut second));
    assert_eq!(previews(&mut first), vec!["from first", "from second"]);
    assert_eq!(sync(&mut second, remote.path(), 2).unwrap().merged, 0);
}

#[test]
fn malformed_file_bundle_outbox_event_is_rejected_without_remote_path_or_diagnostic_leak() {
    let remote = tempdir().unwrap();
    let data = tempdir().unwrap();
    let mut core = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    let item_id = ingest(&mut core, "safe text", 100);
    let file_bundle = ClipboardItem::new(
        item_id,
        VAULT_ID,
        ClipboardContent::FileBundle(
            FileBundle::new(vec![FileEntry::file(
                "C:\\private\\payroll.xlsx".to_owned(),
                1,
                100,
            )])
            .unwrap(),
        ),
        "explorer.exe".to_owned(),
        100,
    );
    let database = Database::open(&data.path().join("history.db"), &KEY).unwrap();
    database
        .outbox()
        .enqueue_event(item_id, &serde_json::to_string(&file_bundle).unwrap(), 101)
        .unwrap();
    drop(database);

    let diagnostics = RecordingSyncDiagnostics::default();
    let response = core
        .sync_directory_with_diagnostics(
            SyncDirectory {
                remote_path: remote.path().display().to_string(),
                device_id: Uuid::from_u128(9),
            },
            &diagnostics,
        )
        .unwrap();

    assert_eq!(response.rejected_local_only, 1);
    assert_eq!(remote.path().read_dir().unwrap().count(), 1);
    assert!(
        !serde_json::to_string(&diagnostics.records())
            .unwrap()
            .contains("payroll.xlsx")
    );
}

#[test]
fn remote_ciphertext_and_diagnostics_do_not_contain_sensitive_content() {
    let remote = tempdir().unwrap();
    let data = tempdir().unwrap();
    let mut core = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    ingest(&mut core, "secret synchronization text", 100);
    let diagnostics = RecordingSyncDiagnostics::default();

    core.sync_directory_with_diagnostics(
        SyncDirectory {
            remote_path: remote.path().display().to_string(),
            device_id: Uuid::from_u128(12),
        },
        &diagnostics,
    )
    .unwrap();

    let remote_bytes = remote
        .path()
        .read_dir()
        .unwrap()
        .flat_map(|entry| std::fs::read(entry.unwrap().path()).unwrap())
        .collect::<Vec<_>>();
    let diagnostics_json = serde_json::to_string(&diagnostics.records()).unwrap();

    assert!(!contains_bytes(
        &remote_bytes,
        b"secret synchronization text"
    ));
    assert!(!diagnostics_json.contains("secret synchronization text"));
    assert!(!diagnostics_json.contains("C:\\private\\file.txt"));
}

#[test]
fn inbound_state_events_merge_without_creating_a_new_outbox_entry() {
    let remote = tempdir().unwrap();
    let data = tempdir().unwrap();
    let mut core = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    let item_id = ingest(&mut core, "state target", 100);
    sync(&mut core, remote.path(), 9).unwrap();

    let header = SegmentHeader {
        protocol_version: SYNC_PROTOCOL_VERSION,
        vault_id: VAULT_ID,
        device_id: Uuid::from_u128(10),
        segment_id: Uuid::from_u128(11),
    };
    let journal_key = VaultKey::from_bytes(KEY)
        .derive(VAULT_ID, KeyPurpose::Journal)
        .unwrap();
    let events = [
        SyncEvent::Favorite {
            item_id,
            state: FavoriteState {
                value: true,
                updated: Hlc::new(200, 0, Uuid::from_u128(10)),
            },
        },
        SyncEvent::Delete {
            item_id,
            state: DeleteState {
                deleted: true,
                updated: Hlc::new(300, 0, Uuid::from_u128(10)),
            },
        },
    ];
    let transport = DirectoryTransport::open(remote.path()).unwrap();
    transport
        .put_segment(
            &header,
            &seal_segment(&journal_key, &header, &events).unwrap(),
        )
        .unwrap();

    let response = sync(&mut core, remote.path(), 9).unwrap();
    assert_eq!(response.merged, 2);
    let database = Database::open(&data.path().join("history.db"), &KEY).unwrap();
    assert_eq!(database.outbox().pending_count().unwrap(), 0);
    let stored = database.items().list_all().unwrap();
    assert_eq!(stored.len(), 1);
    assert!(stored[0].favorite_state.unwrap().value);
    assert!(stored[0].delete_state.unwrap().deleted);
}

#[test]
fn deleted_item_is_removed_from_a_second_core_after_remote_sync() {
    let remote = tempdir().unwrap();
    let first_data = tempdir().unwrap();
    let second_data = tempdir().unwrap();
    let mut first = CoreService::open(first_data.path(), VAULT_ID, &KEY).unwrap();
    let mut second = CoreService::open(second_data.path(), VAULT_ID, &KEY).unwrap();

    let item_id = ingest(&mut first, "delete across devices", 100);
    sync(&mut first, remote.path(), 1).unwrap();
    sync(&mut second, remote.path(), 2).unwrap();
    assert_eq!(previews(&mut second), vec!["delete across devices"]);

    first
        .execute(CoreCommand::Delete(DeleteRequest {
            item_id,
            updated: Hlc::new(200, 0, Uuid::from_u128(1)),
        }))
        .unwrap();
    sync(&mut first, remote.path(), 1).unwrap();
    sync(&mut second, remote.path(), 2).unwrap();

    assert!(previews(&mut second).is_empty());
}

#[test]
fn delete_event_received_before_upsert_is_preserved() {
    let remote = tempdir().unwrap();
    let data = tempdir().unwrap();
    let mut core = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    let item = ClipboardItem::new(
        Uuid::from_u128(100),
        VAULT_ID,
        ClipboardContent::Text("delete arrives first".to_owned()),
        "editor.exe".to_owned(),
        100,
    );
    let delete_state = DeleteState {
        deleted: true,
        updated: Hlc::new(200, 0, Uuid::from_u128(10)),
    };
    let journal_key = VaultKey::from_bytes(KEY)
        .derive(VAULT_ID, KeyPurpose::Journal)
        .unwrap();
    let transport = DirectoryTransport::open(remote.path()).unwrap();
    let delete_header = SegmentHeader {
        protocol_version: SYNC_PROTOCOL_VERSION,
        vault_id: VAULT_ID,
        device_id: Uuid::from_u128(10),
        segment_id: Uuid::from_u128(1),
    };
    transport
        .put_segment(
            &delete_header,
            &seal_segment(
                &journal_key,
                &delete_header,
                &[SyncEvent::Delete {
                    item_id: item.id,
                    state: delete_state,
                }],
            )
            .unwrap(),
        )
        .unwrap();
    let upsert_header = SegmentHeader {
        segment_id: Uuid::from_u128(2),
        ..delete_header
    };
    transport
        .put_segment(
            &upsert_header,
            &seal_segment(
                &journal_key,
                &upsert_header,
                &[SyncEvent::TextUpsert { item }],
            )
            .unwrap(),
        )
        .unwrap();

    sync(&mut core, remote.path(), 9).unwrap();

    assert!(previews(&mut core).is_empty());
}

fn ingest(core: &mut CoreService, text: &str, captured_ms: i64) -> Uuid {
    match core
        .execute(CoreCommand::IngestText(IngestText {
            text: text.to_owned(),
            source_app: "editor.exe".to_owned(),
            captured_ms,
            source_app_display_name: None,
        }))
        .unwrap()
    {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}

fn sync(
    core: &mut CoreService,
    remote_path: &std::path::Path,
    device_id: u128,
) -> Result<clipboard_core::SyncDirectoryResponse, clipboard_core::CoreError> {
    let response = core.execute(CoreCommand::SyncDirectory(SyncDirectory {
        remote_path: remote_path.display().to_string(),
        device_id: Uuid::from_u128(device_id),
    }))?;
    match response {
        CoreResponse::Sync(response) => Ok(response),
        _ => panic!("expected sync response"),
    }
}

fn previews(core: &mut CoreService) -> Vec<String> {
    let mut previews = core
        .execute(CoreCommand::Search(SearchRequest {
            pattern: String::new(),
            mode: SearchMode::Substring,
            filters: SearchFilters::default(),
        }))
        .unwrap()
        .search_items()
        .iter()
        .map(|item| item.preview.clone())
        .collect::<Vec<_>>();
    previews.sort();
    previews
}

fn contains_bytes(haystack: &[u8], needle: &[u8]) -> bool {
    haystack
        .windows(needle.len())
        .any(|window| window == needle)
}
