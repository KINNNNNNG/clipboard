use clipboard_domain::{
    ClipboardContent, ClipboardItem, FileBundle, FileBundleError, FileEntry, SyncScope,
};
use uuid::Uuid;

#[test]
fn file_bundle_is_always_local_only() {
    assert_eq!(FileBundle::new(vec![]), Err(FileBundleError::Empty));

    let bundle = FileBundle::new(vec![FileEntry::file(
        r"C:\work\report.docx".into(),
        42,
        1_725_000_000_000,
    )])
    .unwrap();

    let item = ClipboardItem::new(
        Uuid::now_v7(),
        Uuid::new_v4(),
        ClipboardContent::FileBundle(bundle),
        "explorer.exe".into(),
        1_725_000_000_000,
    );

    assert_eq!(item.sync_scope(), SyncScope::LocalOnly);
    assert!(!item.is_syncable());
}

#[test]
fn text_and_image_are_vault_scoped() {
    let vault_id = Uuid::new_v4();
    let text = ClipboardItem::new(
        Uuid::now_v7(),
        vault_id,
        ClipboardContent::Text("hello".into()),
        "notepad.exe".into(),
        1_725_000_000_000,
    );
    let image = ClipboardItem::new(
        Uuid::now_v7(),
        vault_id,
        ClipboardContent::Image {
            object_id: Uuid::new_v4(),
            width: 640,
            height: 480,
            bytes: 1_024,
        },
        "mspaint.exe".into(),
        1_725_000_000_001,
    );

    assert_eq!(text.sync_scope(), SyncScope::Vault);
    assert_eq!(image.sync_scope(), SyncScope::Vault);
    assert!(text.is_syncable());
    assert!(image.is_syncable());
}
