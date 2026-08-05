use clipboard_domain::{ClipboardContent, ClipboardItem, FileBundle, FileEntry};
use clipboard_storage::{Database, OutboxError};
use uuid::Uuid;

fn local_file_item() -> ClipboardItem {
    let bundle = FileBundle::new(vec![FileEntry::file("C:\\a.txt".into(), 1, 1)]).unwrap();
    ClipboardItem::new(
        Uuid::now_v7(),
        Uuid::new_v4(),
        ClipboardContent::FileBundle(bundle),
        "explorer.exe".into(),
        1,
    )
    .with_source_app_display_name(Some("文件资源管理器".into()))
}

#[test]
fn local_file_bundle_cannot_enter_outbox() {
    let database = Database::open_in_memory(&[0x11; 32]).unwrap();
    let mut item = local_file_item();
    database.items().insert(&item).unwrap();
    item.last_used_ms = 2;
    database.items().update(&item).unwrap();

    assert!(matches!(
        database.outbox().enqueue_item(&item),
        Err(OutboxError::LocalOnly)
    ));
    assert_eq!(database.outbox().pending_count().unwrap(), 0);
    assert_eq!(
        item.source_app_display_name.as_deref(),
        Some("文件资源管理器")
    );
}

#[test]
fn database_trigger_rejects_local_file_bundle_when_domain_guard_is_bypassed() {
    let database = Database::open_in_memory(&[0x11; 32]).unwrap();
    let item = local_file_item();
    database.items().insert(&item).unwrap();

    let error = database
        .outbox()
        .enqueue_event(item.id, r#"{"type":"upsert"}"#, 2)
        .unwrap_err();

    assert!(matches!(error, OutboxError::Sqlite(_)));
    assert!(
        error
            .to_string()
            .contains("local_only item cannot enter sync outbox")
    );
}

#[test]
fn vault_scoped_text_can_enter_outbox() {
    let database = Database::open_in_memory(&[0x11; 32]).unwrap();
    let item = ClipboardItem::new(
        Uuid::now_v7(),
        Uuid::new_v4(),
        ClipboardContent::Text("sync me".into()),
        "notepad.exe".into(),
        1,
    );
    database.items().insert(&item).unwrap();

    database.outbox().enqueue_item(&item).unwrap();
    assert_eq!(database.outbox().pending_count().unwrap(), 1);
}
