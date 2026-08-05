use clipboard_domain::{ClipboardContent, ClipboardItem};
use clipboard_storage::Database;
use rusqlite::{Connection, params};
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x31; 32];

#[test]
fn legacy_database_adds_nullable_source_display_name_without_rewriting_history() {
    let directory = tempdir().unwrap();
    let path = directory.path().join("history.db");
    initialize_v1_database(&path);

    let vault_id = Uuid::from_u128(301);
    let item = ClipboardItem::new(
        Uuid::from_u128(302),
        vault_id,
        ClipboardContent::Text("legacy history".into()),
        "notepad.exe".into(),
        100,
    );
    let database = Database::open(&path, &KEY).unwrap();
    database.items().insert(&item).unwrap();
    drop(database);

    let reopened = Database::open(&path, &KEY).unwrap();
    let loaded = reopened.items().list().unwrap();
    assert_eq!(loaded, vec![item]);
    assert_eq!(loaded[0].source_app_display_name, None);
}

fn initialize_v1_database(path: &std::path::Path) {
    let connection = Connection::open(path).unwrap();
    connection
        .pragma_update(None, "key", format!("x'{}'", hex::encode(KEY)))
        .unwrap();
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
