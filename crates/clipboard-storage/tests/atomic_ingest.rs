use clipboard_domain::{ClipboardContent, ClipboardItem, FileBundle, FileEntry};
use clipboard_storage::{Database, StorageError};
use uuid::Uuid;

#[test]
fn syncable_item_and_outbox_event_commit_as_one_operation() {
    let database = Database::open_in_memory(&[0x11; 32]).unwrap();
    let item = ClipboardItem::new(
        Uuid::now_v7(),
        Uuid::new_v4(),
        ClipboardContent::Text("atomic".into()),
        "notepad.exe".into(),
        1,
    );

    database.items().insert_and_enqueue(&item).unwrap();

    assert_eq!(database.items().list().unwrap(), vec![item]);
    assert_eq!(database.outbox().pending_count().unwrap(), 1);
}

#[test]
fn local_only_item_is_rejected_before_atomic_write() {
    let database = Database::open_in_memory(&[0x11; 32]).unwrap();
    let bundle = FileBundle::new(vec![FileEntry::file("C:\\a.txt".into(), 1, 1)]).unwrap();
    let item = ClipboardItem::new(
        Uuid::now_v7(),
        Uuid::new_v4(),
        ClipboardContent::FileBundle(bundle),
        "explorer.exe".into(),
        1,
    );

    assert!(matches!(
        database.items().insert_and_enqueue(&item),
        Err(StorageError::LocalOnly)
    ));
    assert!(database.items().list().unwrap().is_empty());
    assert_eq!(database.outbox().pending_count().unwrap(), 0);
}
