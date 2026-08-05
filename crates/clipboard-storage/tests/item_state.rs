use clipboard_domain::{ClipboardContent, ClipboardItem, DeleteState, FavoriteState, Hlc};
use clipboard_storage::{Database, StorageError};
use rusqlite::{Connection, params};
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x31; 32];

fn text_item(id: Uuid, vault_id: Uuid, created_ms: i64) -> ClipboardItem {
    ClipboardItem::new(
        id,
        vault_id,
        ClipboardContent::Text("persisted state".into()),
        "notepad.exe".into(),
        created_ms,
    )
}

#[test]
fn migration_adds_fingerprint_and_state_survives_reopen() {
    let directory = tempdir().unwrap();
    let path = directory.path().join("history.db");
    initialize_v1_database(&path);

    let vault_id = Uuid::from_u128(8);
    let mut item = text_item(Uuid::from_u128(9), vault_id, 100);
    let favorite = FavoriteState {
        value: true,
        updated: Hlc::new(110, 0, Uuid::from_u128(3)),
    };
    item.content_fingerprint = Some([0x77; 32]);
    item.favorite_state = Some(favorite);

    let database = Database::open(&path, &KEY).unwrap();
    database.items().insert(&item).unwrap();
    drop(database);

    let reopened = Database::open(&path, &KEY).unwrap();
    let loaded = reopened.items().list().unwrap();
    assert_eq!(loaded, vec![item]);
    assert_eq!(loaded[0].favorite_state, Some(favorite));
    assert_eq!(loaded[0].content_fingerprint, Some([0x77; 32]));
}

#[test]
fn deleted_items_are_hidden_but_available_to_internal_state_queries() {
    let database = Database::open_in_memory(&KEY).unwrap();
    let mut item = text_item(Uuid::from_u128(20), Uuid::from_u128(21), 200);
    database.items().insert(&item).unwrap();

    let deleted = DeleteState {
        deleted: true,
        updated: Hlc::new(210, 0, Uuid::from_u128(4)),
    };
    item.delete_state = Some(deleted);
    database.items().update(&item).unwrap();

    assert!(database.items().list().unwrap().is_empty());
    let all_items = database.items().list_all().unwrap();
    assert_eq!(all_items.len(), 1);
    assert_eq!(all_items[0].delete_state, Some(deleted));
}

#[test]
fn database_rejects_schema_versions_newer_than_this_binary_supports() {
    let directory = tempdir().unwrap();
    let path = directory.path().join("history.db");
    initialize_v1_database(&path);
    add_migration_record(&path, 999);

    assert!(matches!(
        Database::open(&path, &KEY),
        Err(StorageError::UnsupportedSchemaVersion {
            found: 999,
            supported: 3
        })
    ));
}

#[test]
fn database_rejects_non_contiguous_migration_history() {
    let directory = tempdir().unwrap();
    let path = directory.path().join("history.db");
    initialize_v1_database(&path);
    let connection = open_encrypted_connection(&path);
    connection
        .execute("DELETE FROM schema_migrations WHERE version = 1", [])
        .unwrap();
    connection
        .execute(
            "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (2, 0)",
            [],
        )
        .unwrap();
    drop(connection);

    assert!(matches!(
        Database::open(&path, &KEY),
        Err(StorageError::InvalidMigrationHistory)
    ));
}

fn initialize_v1_database(path: &std::path::Path) {
    let connection = open_encrypted_connection(path);
    connection
        .execute_batch(include_str!("../migrations/001_initial.sql"))
        .unwrap();
    connection
        .execute(
            "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, ?2)",
            params![1_i64, 0_i64],
        )
        .unwrap();
}

fn add_migration_record(path: &std::path::Path, version: i64) {
    let connection = open_encrypted_connection(path);
    connection
        .execute(
            "INSERT INTO schema_migrations(version, applied_at_ms) VALUES (?1, 0)",
            params![version],
        )
        .unwrap();
}

fn open_encrypted_connection(path: &std::path::Path) -> Connection {
    let connection = Connection::open(path).unwrap();
    let encoded_key = format!("x'{}'", hex::encode(KEY));
    connection.pragma_update(None, "key", encoded_key).unwrap();
    connection
}
