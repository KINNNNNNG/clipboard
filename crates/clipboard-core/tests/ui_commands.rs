use clipboard_core::{
    ApplyRetentionRequest, CoreCommand, CoreResponse, CoreService, DeleteRequest, IngestText,
    SearchFilters, SearchRequest, SetFavorite,
};
use clipboard_crypto::{KeyPurpose, VaultKey};
use clipboard_domain::{
    ClipboardContent, ClipboardItem, FavoriteState, FileBundle, FileEntry, Hlc, RetentionPolicy,
};
use clipboard_search::SearchMode;
use clipboard_storage::Database;
use rusqlite::Connection;
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x51; 32];
const DAY_MS: i64 = 86_400_000;

fn ingest_text(core: &mut CoreService, text: &str, source_app: &str, captured_ms: i64) -> Uuid {
    match core
        .execute(CoreCommand::IngestText(IngestText {
            text: text.into(),
            source_app: source_app.into(),
            captured_ms,
            source_app_display_name: None,
        }))
        .unwrap()
    {
        CoreResponse::Mutation { item_id } => item_id,
        _ => panic!("expected mutation response"),
    }
}

fn search(core: &mut CoreService, filters: SearchFilters) -> CoreResponse {
    core.execute(CoreCommand::Search(SearchRequest {
        pattern: String::new(),
        mode: SearchMode::Substring,
        filters,
    }))
    .unwrap()
}

#[test]
fn search_combines_time_source_and_kind_filters() {
    let directory = tempdir().unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(1), &KEY).unwrap();
    ingest_text(&mut core, "first", "notepad.exe", 100);
    ingest_text(&mut core, "second", "msedge.exe", 200);
    ingest_text(&mut core, "third", "notepad.exe", 300);

    let response = search(
        &mut core,
        SearchFilters {
            created_after_ms: Some(150),
            created_before_ms: Some(250),
            source_apps: vec!["msedge.exe".into()],
            kinds: vec!["text".into()],
        },
    );

    let items = response.search_items();
    assert_eq!(items.len(), 1);
    assert_eq!(items[0].preview, "second");

    assert!(
        search(
            &mut core,
            SearchFilters {
                kinds: vec!["image".into()],
                ..SearchFilters::default()
            }
        )
        .search_items()
        .is_empty()
    );
}

#[test]
fn favorite_survives_reopen_and_clear_unfavorite_preserves_it() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(2);
    let favorite_id;
    {
        let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
        favorite_id = ingest_text(&mut core, "keep", "notepad.exe", 100);
        ingest_text(&mut core, "clear", "notepad.exe", 200);
        core.execute(CoreCommand::SetFavorite(SetFavorite {
            item_id: favorite_id,
            favorite: true,
            updated: Hlc::new(300, 0, Uuid::from_u128(5)),
        }))
        .unwrap();
    }

    let mut reopened = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let before_clear = search(&mut reopened, SearchFilters::default());
    let persisted = before_clear
        .search_items()
        .iter()
        .find(|item| item.id == favorite_id)
        .unwrap();
    assert!(persisted.favorite);

    reopened.execute(CoreCommand::ClearUnfavorite).unwrap();
    let remaining = search(&mut reopened, SearchFilters::default());
    assert_eq!(remaining.search_items().len(), 1);
    assert_eq!(remaining.search_items()[0].id, favorite_id);
}

#[test]
fn delete_hides_item_and_enqueues_a_sync_tombstone() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(3);
    let item_id;
    {
        let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
        item_id = ingest_text(&mut core, "delete me", "notepad.exe", 100);
        core.execute(CoreCommand::Delete(DeleteRequest {
            item_id,
            updated: Hlc::new(200, 0, Uuid::from_u128(6)),
        }))
        .unwrap();
        assert!(
            search(&mut core, SearchFilters::default())
                .search_items()
                .is_empty()
        );
    }

    let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
    assert_eq!(database.outbox().pending_count().unwrap(), 2);
    let stored = database.items().list_all().unwrap();
    assert_eq!(stored.len(), 1);
    assert!(stored[0].delete_state.unwrap().deleted);
}

#[test]
fn consecutive_duplicate_text_reuses_item_and_updates_last_used_time() {
    let directory = tempdir().unwrap();
    let mut core = CoreService::open(directory.path(), Uuid::from_u128(4), &KEY).unwrap();

    let first_id = ingest_text(&mut core, "duplicate", "notepad.exe", 100);
    let second_id = ingest_text(&mut core, "duplicate", "terminal.exe", 200);

    assert_eq!(first_id, second_id);
    let response = search(&mut core, SearchFilters::default());
    assert_eq!(response.search_items().len(), 1);
    assert_eq!(response.search_items()[0].last_used_ms, 200);
    assert_eq!(response.search_items()[0].source_app, "terminal.exe");
}

#[test]
fn first_duplicate_after_v1_upgrade_reuses_legacy_text_and_backfills_fingerprint() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(6);
    let legacy_id = Uuid::from_u128(60);
    {
        let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
        database
            .items()
            .insert(&text_item(legacy_id, vault_id, "legacy duplicate", 100))
            .unwrap();
    }

    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let ingested_id = ingest_text(&mut core, "legacy duplicate", "terminal.exe", 200);

    assert_eq!(ingested_id, legacy_id);
    drop(core);
    let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
    let stored = database.items().list().unwrap();
    assert_eq!(stored.len(), 1);
    assert!(stored[0].content_fingerprint.is_some());
    assert_eq!(stored[0].last_used_ms, 200);
}

#[test]
fn text_does_not_reuse_an_image_with_the_same_fingerprint() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(8);
    let image_id = Uuid::from_u128(80);
    let fingerprint = VaultKey::from_bytes(KEY)
        .derive(vault_id, KeyPurpose::Fingerprint)
        .unwrap()
        .keyed_hash(b"same bytes");
    {
        let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
        let mut image = ClipboardItem::new(
            image_id,
            vault_id,
            ClipboardContent::Image {
                object_id: Uuid::from_u128(81),
                width: 1,
                height: 1,
                bytes: 10,
            },
            "mspaint.exe".into(),
            100,
        );
        image.content_fingerprint = Some(fingerprint);
        database.items().insert(&image).unwrap();
    }

    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let text_id = ingest_text(&mut core, "same bytes", "notepad.exe", 200);

    assert_ne!(text_id, image_id);
    assert_eq!(
        search(&mut core, SearchFilters::default())
            .search_items()
            .len(),
        2
    );
}

#[test]
fn retention_executes_age_image_space_and_count_rules_without_removing_favorites() {
    let directory = tempdir().unwrap();
    let vault_id = Uuid::from_u128(10);
    let now_ms = 10 * DAY_MS;
    {
        let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();

        let mut favorite = text_item(Uuid::from_u128(11), vault_id, "favorite", 0);
        favorite.favorite_state = Some(FavoriteState {
            value: true,
            updated: Hlc::new(1, 0, Uuid::from_u128(7)),
        });
        database.items().insert(&favorite).unwrap();

        database
            .items()
            .insert(&text_item(Uuid::from_u128(12), vault_id, "expired", 0))
            .unwrap();
        database
            .items()
            .insert(&ClipboardItem::new(
                Uuid::from_u128(13),
                vault_id,
                ClipboardContent::Image {
                    object_id: Uuid::from_u128(30),
                    width: 10,
                    height: 10,
                    bytes: 100,
                },
                "mspaint.exe".into(),
                now_ms - 100,
            ))
            .unwrap();
        database
            .items()
            .insert(&ClipboardItem::new(
                Uuid::from_u128(14),
                vault_id,
                ClipboardContent::FileBundle(
                    FileBundle::new(vec![FileEntry::file("C:\\old.txt".into(), 10, 1)]).unwrap(),
                ),
                "explorer.exe".into(),
                now_ms - 50,
            ))
            .unwrap();
        database
            .items()
            .insert(&text_item(Uuid::from_u128(15), vault_id, "recent", now_ms))
            .unwrap();
    }

    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    let response = core
        .execute(CoreCommand::ApplyRetention(ApplyRetentionRequest {
            now_ms,
            policy: RetentionPolicy {
                max_regular_items: Some(1),
                max_age_days: Some(1),
                max_image_bytes: Some(0),
            },
        }))
        .unwrap();

    match response {
        CoreResponse::Retention {
            deleted_local,
            tombstones_created,
        } => {
            assert_eq!(deleted_local, 1);
            assert_eq!(tombstones_created, 2);
        }
        _ => panic!("expected retention response"),
    }
    let remaining = search(&mut core, SearchFilters::default());
    assert_eq!(remaining.search_items().len(), 2);
    assert!(
        remaining
            .search_items()
            .iter()
            .any(|item| item.preview == "favorite" && item.favorite)
    );
    assert!(
        remaining
            .search_items()
            .iter()
            .any(|item| item.preview == "recent")
    );
    drop(core);

    let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
    assert_eq!(database.outbox().pending_count().unwrap(), 2);
}

#[test]
fn retention_rolls_back_local_deletes_when_tombstone_enqueue_fails() {
    let directory = tempdir().unwrap();
    let database_path = directory.path().join("history.db");
    let vault_id = Uuid::from_u128(20);
    {
        let database = Database::open(&database_path, &KEY).unwrap();
        database
            .items()
            .insert(&text_item(Uuid::from_u128(21), vault_id, "syncable", 100))
            .unwrap();
        database
            .items()
            .insert(&ClipboardItem::new(
                Uuid::from_u128(22),
                vault_id,
                ClipboardContent::FileBundle(
                    FileBundle::new(vec![FileEntry::file("C:\\keep.txt".into(), 10, 1)]).unwrap(),
                ),
                "explorer.exe".into(),
                200,
            ))
            .unwrap();
    }
    install_failing_outbox_trigger(&database_path);

    let mut core = CoreService::open(directory.path(), vault_id, &KEY).unwrap();
    assert!(
        core.execute(CoreCommand::ApplyRetention(ApplyRetentionRequest {
            now_ms: 300,
            policy: RetentionPolicy {
                max_regular_items: Some(0),
                max_age_days: None,
                max_image_bytes: None,
            },
        }))
        .is_err()
    );
    drop(core);

    let database = Database::open(&database_path, &KEY).unwrap();
    let stored = database.items().list_all().unwrap();
    assert_eq!(stored.len(), 2);
    assert!(stored.iter().all(|item| item.delete_state.is_none()));
    assert_eq!(database.outbox().pending_count().unwrap(), 0);
}

fn text_item(id: Uuid, vault_id: Uuid, text: &str, created_ms: i64) -> ClipboardItem {
    ClipboardItem::new(
        id,
        vault_id,
        ClipboardContent::Text(text.into()),
        "notepad.exe".into(),
        created_ms,
    )
}

fn install_failing_outbox_trigger(path: &std::path::Path) {
    let connection = Connection::open(path).unwrap();
    let encoded_key = format!("x'{}'", hex::encode(KEY));
    connection.pragma_update(None, "key", encoded_key).unwrap();
    connection
        .execute_batch(
            "CREATE TRIGGER fail_cleanup_outbox
             BEFORE INSERT ON sync_outbox
             BEGIN
                 SELECT RAISE(ABORT, 'injected outbox failure');
             END;",
        )
        .unwrap();
}
