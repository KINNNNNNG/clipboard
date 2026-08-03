use clipboard_domain::{ClipboardContent, ClipboardItem};
use clipboard_storage::Database;
use uuid::Uuid;

#[test]
fn inserted_items_can_be_loaded_in_recently_used_order() {
    let database = Database::open_in_memory(&[0x11; 32]).unwrap();
    let vault_id = Uuid::new_v4();
    let older = ClipboardItem::new(
        Uuid::now_v7(),
        vault_id,
        ClipboardContent::Text("older".into()),
        "notepad.exe".into(),
        1,
    );
    let newer = ClipboardItem::new(
        Uuid::now_v7(),
        vault_id,
        ClipboardContent::Text("newer".into()),
        "terminal.exe".into(),
        2,
    );
    database.items().insert(&older).unwrap();
    database.items().insert(&newer).unwrap();

    let loaded = database.items().list().unwrap();

    assert_eq!(loaded, vec![newer, older]);
}
