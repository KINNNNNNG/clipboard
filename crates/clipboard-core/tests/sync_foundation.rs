use clipboard_core::{
    CoreCommand, CoreResponse, CoreService, DeleteRequest, IngestImage, IngestText, SearchFilters,
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
    let names = remote
        .path()
        .read_dir()
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .collect::<Vec<_>>();
    assert!(names.iter().any(|name| name == "header.json"));
    assert!(names.iter().any(|name| name.ends_with(".state.enc")));
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
fn image_object_is_uploaded_before_metadata_and_can_be_read_on_second_core() {
    let remote = tempdir().unwrap();
    let first_data = tempdir().unwrap();
    let second_data = tempdir().unwrap();
    let mut first = CoreService::open(first_data.path(), VAULT_ID, &KEY).unwrap();
    let mut second = CoreService::open(second_data.path(), VAULT_ID, &KEY).unwrap();
    let png = b"synthetic-png-bytes";
    let item_id = match first
        .ingest_image(
            IngestImage {
                width: 2,
                height: 3,
                source_app: "paint.exe".to_owned(),
                captured_ms: 100,
                source_app_display_name: None,
            },
            png,
        )
        .unwrap()
    {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    };

    sync(&mut first, remote.path(), 1).unwrap();
    let remote_bytes = remote
        .path()
        .read_dir()
        .unwrap()
        .flat_map(|entry| std::fs::read(entry.unwrap().path()).unwrap())
        .collect::<Vec<_>>();
    assert!(!contains_bytes(&remote_bytes, png));
    let names = remote
        .path()
        .read_dir()
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .collect::<Vec<_>>();
    assert!(names.iter().any(|name| name == "header.json"));
    assert!(names.iter().any(|name| name.ends_with(".state.enc")));
    assert!(names.iter().any(|name| name.starts_with("image-")));
    assert!(names.iter().any(|name| name.ends_with(".enc")
        && !name.ends_with(".state.enc")
        && !name.starts_with("image-")));

    sync(&mut second, remote.path(), 2).unwrap();
    assert_eq!(second.read_image(item_id).unwrap(), png);
}

#[test]
fn missing_image_object_does_not_block_text_in_same_remote_segment() {
    let remote = tempdir().unwrap();
    let data = tempdir().unwrap();
    let mut core = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    let image_id = Uuid::from_u128(0x201);
    let image_object_id = Uuid::from_u128(0x202);
    let image = ClipboardItem::new(
        image_id,
        VAULT_ID,
        ClipboardContent::Image {
            object_id: image_object_id,
            width: 2,
            height: 3,
            bytes: 12,
        },
        "paint.exe".to_owned(),
        100,
    );
    let text = ClipboardItem::new(
        Uuid::from_u128(0x203),
        VAULT_ID,
        ClipboardContent::Text("text survives missing image".to_owned()),
        "editor.exe".to_owned(),
        101,
    );
    let header = SegmentHeader {
        protocol_version: SYNC_PROTOCOL_VERSION,
        vault_id: VAULT_ID,
        device_id: Uuid::from_u128(0x204),
        segment_id: Uuid::from_u128(0x205),
    };
    let journal_key = VaultKey::from_bytes(KEY)
        .derive(VAULT_ID, KeyPurpose::Journal)
        .unwrap();
    let transport = DirectoryTransport::open(remote.path()).unwrap();
    transport
        .put_segment(
            &header,
            &seal_segment(
                &journal_key,
                &header,
                &[
                    SyncEvent::ImageUpsert { item: image },
                    SyncEvent::TextUpsert { item: text },
                ],
            )
            .unwrap(),
        )
        .unwrap();

    let response = sync(&mut core, remote.path(), 0x206).unwrap();

    assert_eq!(response.merged, 1);
    assert_eq!(previews(&mut core), vec!["text survives missing image"]);
    assert!(core.read_image(image_id).is_err());
}

#[test]
fn missing_local_image_object_keeps_outbox_entry_pending() {
    let remote = tempdir().unwrap();
    let data = tempdir().unwrap();
    let mut core = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    let item_id = match core
        .ingest_image(
            IngestImage {
                width: 2,
                height: 3,
                source_app: "paint.exe".to_owned(),
                captured_ms: 100,
                source_app_display_name: None,
            },
            b"image bytes",
        )
        .unwrap()
    {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    };
    let database = Database::open(&data.path().join("history.db"), &KEY).unwrap();
    let item = database
        .items()
        .list_all()
        .unwrap()
        .into_iter()
        .find(|item| item.id == item_id)
        .unwrap();
    let ClipboardContent::Image { object_id, .. } = item.content else {
        panic!("expected image item");
    };
    std::fs::remove_file(
        data.path()
            .join("objects")
            .join(format!("{object_id}.clipobj")),
    )
    .unwrap();
    drop(database);

    assert!(sync(&mut core, remote.path(), 0x301).is_err());

    let database = Database::open(&data.path().join("history.db"), &KEY).unwrap();
    assert_eq!(database.outbox().pending_count().unwrap(), 1);
    assert_eq!(remote.path().read_dir().unwrap().count(), 0);
}

#[test]
fn tampered_remote_image_object_is_not_inserted_into_history() {
    let remote = tempdir().unwrap();
    let first_data = tempdir().unwrap();
    let second_data = tempdir().unwrap();
    let mut first = CoreService::open(first_data.path(), VAULT_ID, &KEY).unwrap();
    let mut second = CoreService::open(second_data.path(), VAULT_ID, &KEY).unwrap();
    let item_id = match first
        .ingest_image(
            IngestImage {
                width: 2,
                height: 3,
                source_app: "paint.exe".to_owned(),
                captured_ms: 100,
                source_app_display_name: None,
            },
            b"image bytes",
        )
        .unwrap()
    {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    };
    sync(&mut first, remote.path(), 0x401).unwrap();
    let object_id = Database::open(&first_data.path().join("history.db"), &KEY)
        .unwrap()
        .items()
        .list_all()
        .unwrap()
        .into_iter()
        .find(|item| item.id == item_id)
        .and_then(|item| match item.content {
            ClipboardContent::Image { object_id, .. } => Some(object_id),
            _ => None,
        })
        .unwrap();
    let transport = DirectoryTransport::open(remote.path()).unwrap();
    let object_name = clipboard_sync::completed_image_object_name(
        &clipboard_sync::RemoteImageObject::new(VAULT_ID, object_id),
    );
    let mut encrypted = transport.get_object(&object_name).unwrap();
    let last_index = encrypted.len() - 1;
    encrypted[last_index] ^= 0x80;
    std::fs::write(remote.path().join(object_name), encrypted).unwrap();

    let response = sync(&mut second, remote.path(), 0x402).unwrap();

    assert_eq!(response.merged, 0);
    assert!(previews(&mut second).is_empty());
    assert!(second.read_image(item_id).is_err());
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

#[test]
fn snapshot_compaction_waits_for_other_device_acknowledgement() {
    let remote = tempdir().unwrap();
    let first_data = tempdir().unwrap();
    let second_data = tempdir().unwrap();
    let mut first = CoreService::open(first_data.path(), VAULT_ID, &KEY).unwrap();
    let mut second = CoreService::open(second_data.path(), VAULT_ID, &KEY).unwrap();

    ingest(&mut first, "snapshot gate", 100);
    sync(&mut first, remote.path(), 1).unwrap();
    let before = DirectoryTransport::open(remote.path())
        .unwrap()
        .list_all_segments()
        .unwrap();
    assert!(!before.is_empty());

    sync(&mut second, remote.path(), 2).unwrap();
    let after_second = DirectoryTransport::open(remote.path())
        .unwrap()
        .list_all_segments()
        .unwrap();
    assert!(!after_second.is_empty());
}

#[test]
fn snapshot_compaction_can_remove_old_segments_after_both_devices_sync_again() {
    let remote = tempdir().unwrap();
    let first_data = tempdir().unwrap();
    let second_data = tempdir().unwrap();
    let mut first = CoreService::open(first_data.path(), VAULT_ID, &KEY).unwrap();
    let mut second = CoreService::open(second_data.path(), VAULT_ID, &KEY).unwrap();

    ingest(&mut first, "snapshot eventually compacts", 100);
    sync(&mut first, remote.path(), 1).unwrap();
    sync(&mut second, remote.path(), 2).unwrap();
    sync(&mut first, remote.path(), 1).unwrap();
    sync(&mut second, remote.path(), 2).unwrap();

    assert!(
        DirectoryTransport::open(remote.path())
            .unwrap()
            .list_all_segments()
            .unwrap()
            .is_empty()
    );
}

#[test]
fn deactivated_device_state_is_preserved_but_does_not_block_compaction() {
    let remote = tempdir().unwrap();
    let first_data = tempdir().unwrap();
    let second_data = tempdir().unwrap();
    let mut first = CoreService::open(first_data.path(), VAULT_ID, &KEY).unwrap();
    let mut second = CoreService::open(second_data.path(), VAULT_ID, &KEY).unwrap();

    ingest(&mut first, "deactivation keeps history", 100);
    sync(&mut first, remote.path(), 1).unwrap();
    sync(&mut second, remote.path(), 2).unwrap();
    first
        .set_directory_device_active(remote.path(), Uuid::from_u128(2), false)
        .unwrap();
    sync(&mut first, remote.path(), 1).unwrap();

    assert!(
        remote
            .path()
            .join(format!("device-{}.state.enc", Uuid::from_u128(2)))
            .exists()
    );
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
