use clipboard_domain::{ClipboardContent, ClipboardItem};
use clipboard_storage::Database;
use uuid::Uuid;

fn open_database() -> Database {
    Database::open_in_memory(&[0x44; 32]).unwrap()
}

fn enqueue_text(database: &Database, text: &str, created_ms: i64) -> ClipboardItem {
    let item = ClipboardItem::new(
        Uuid::now_v7(),
        Uuid::new_v4(),
        ClipboardContent::Text(text.into()),
        "notepad.exe".into(),
        created_ms,
    );
    database.items().insert(&item).unwrap();
    database.outbox().enqueue_item(&item).unwrap();
    item
}

#[test]
fn pending_entries_are_ordered_by_created_time_then_sequence() {
    let database = open_database();
    let later = enqueue_text(&database, "later", 2);
    let earlier = enqueue_text(&database, "earlier", 1);

    let entries = database.outbox().pending().unwrap();

    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0].item_id, earlier.id);
    assert_eq!(entries[0].created_ms, 1);
    assert_eq!(entries[1].item_id, later.id);
    assert_eq!(entries[1].created_ms, 2);
}

#[test]
fn pending_entries_with_the_same_timestamp_are_ordered_by_sequence() {
    let database = open_database();
    let first = enqueue_text(&database, "first", 1);
    let second = enqueue_text(&database, "second", 1);

    let entries = database.outbox().pending().unwrap();

    assert_eq!(entries.len(), 2);
    assert_eq!(entries[0].item_id, first.id);
    assert_eq!(entries[1].item_id, second.id);
    assert!(entries[0].id < entries[1].id);
}

#[test]
fn acknowledging_one_outbox_entry_leaves_later_entries_pending() {
    let database = open_database();
    enqueue_text(&database, "first", 1);
    let second = enqueue_text(&database, "second", 2);
    let entries = database.outbox().pending().unwrap();

    assert!(database.outbox().acknowledge(entries[0].id).unwrap());

    let remaining = database.outbox().pending().unwrap();
    assert_eq!(remaining.len(), 1);
    assert_eq!(remaining[0].item_id, second.id);
}

#[test]
fn acknowledging_an_unknown_outbox_entry_is_reported_without_deleting_pending_entries() {
    let database = open_database();
    let item = enqueue_text(&database, "pending", 1);

    assert!(!database.outbox().acknowledge(123_456).unwrap());

    let remaining = database.outbox().pending().unwrap();
    assert_eq!(remaining.len(), 1);
    assert_eq!(remaining[0].item_id, item.id);
}
