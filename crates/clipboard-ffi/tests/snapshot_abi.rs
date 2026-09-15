use clipboard_ffi::{
    CoreBuffer, CoreHandle, CoreStatus, clipboard_core_close, clipboard_core_free_buffer,
    clipboard_core_open_v2, clipboard_snapshot_create, clipboard_snapshot_list,
    clipboard_snapshot_restore,
};
use std::fs;
use std::path::Path;
use std::ptr::null_mut;
use std::slice;
use tempfile::tempdir;
use uuid::Uuid;

const VAULT_ID: Uuid = Uuid::from_u128(0x0d01);
const KEY: [u8; 32] = [0x71; 32];

fn open(status: &mut CoreStatus, data_dir: &str) {
    let mut handle: *mut CoreHandle = null_mut();
    *status = unsafe {
        clipboard_core_open_v2(
            data_dir.as_ptr(),
            data_dir.len(),
            KEY.as_ptr(),
            KEY.len(),
            VAULT_ID.as_bytes().as_ptr(),
            16,
            &mut handle,
        )
    };
    if *status == CoreStatus::Ok {
        unsafe { clipboard_core_close(handle) };
    }
}

fn read(buffer: CoreBuffer) -> String {
    let bytes = unsafe { slice::from_raw_parts(buffer.ptr, buffer.len) };
    let text = String::from_utf8(bytes.to_vec()).unwrap();
    unsafe { clipboard_core_free_buffer(buffer) };
    text
}

#[test]
fn snapshot_abi_creates_lists_and_restores_a_vault() {
    let data = tempdir().unwrap();
    let snapshots = tempdir().unwrap();
    let data_path = data.path().to_string_lossy().into_owned();
    let root_path = snapshots.path().to_string_lossy().into_owned();

    let mut status = CoreStatus::CoreError;
    open(&mut status, &data_path);
    assert_eq!(status, CoreStatus::Ok);

    let objects = data.path().join("objects");
    fs::create_dir_all(&objects).unwrap();
    fs::write(objects.join("image-object"), b"encrypted-image").unwrap();

    let mut created = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_snapshot_create(
                data_path.as_ptr(),
                data_path.len(),
                VAULT_ID.as_bytes().as_ptr(),
                16,
                KEY.as_ptr(),
                KEY.len(),
                root_path.as_ptr(),
                root_path.len(),
                1_760_000_000_000,
                3,
                &mut created,
            )
        },
        CoreStatus::Ok
    );
    let summary: serde_json::Value = serde_json::from_str(&read(created)).unwrap();
    assert_eq!(summary["file_count"], serde_json::json!(1));
    let snapshot_dir = summary["directory"].as_str().unwrap().to_owned();

    let mut listed = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_snapshot_list(
                root_path.as_ptr(),
                root_path.len(),
                VAULT_ID.as_bytes().as_ptr(),
                16,
                KEY.as_ptr(),
                KEY.len(),
                &mut listed,
            )
        },
        CoreStatus::Ok
    );
    let snapshots_json: serde_json::Value = serde_json::from_str(&read(listed)).unwrap();
    assert_eq!(snapshots_json["snapshots"].as_array().unwrap().len(), 1);

    fs::write(data.path().join("history.db"), b"damaged").unwrap();
    status = CoreStatus::Ok;
    open(&mut status, &data_path);
    assert_ne!(status, CoreStatus::Ok, "the damaged database must not open");

    let mut restored = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_snapshot_restore(
                snapshot_dir.as_ptr(),
                snapshot_dir.len(),
                data_path.as_ptr(),
                data_path.len(),
                VAULT_ID.as_bytes().as_ptr(),
                16,
                KEY.as_ptr(),
                KEY.len(),
                1_760_000_100_000,
                &mut restored,
            )
        },
        CoreStatus::Ok
    );
    let restored_json: serde_json::Value = serde_json::from_str(&read(restored)).unwrap();
    assert_eq!(restored_json["file_count"], serde_json::json!(1));

    status = CoreStatus::CoreError;
    open(&mut status, &data_path);
    assert_eq!(status, CoreStatus::Ok, "the restored database must open");
    assert_eq!(
        fs::read(Path::new(&data_path).join("objects").join("image-object")).unwrap(),
        b"encrypted-image"
    );
    assert!(
        fs::read_dir(&data_path)
            .unwrap()
            .filter_map(Result::ok)
            .any(|entry| entry
                .file_name()
                .to_string_lossy()
                .starts_with("history.db.corrupt-")),
        "the damaged database must be quarantined"
    );
}

#[test]
fn snapshot_abi_rejects_invalid_arguments() {
    let data = tempdir().unwrap();
    let root = tempdir().unwrap();
    let data_path = data.path().to_string_lossy().into_owned();
    let root_path = root.path().to_string_lossy().into_owned();
    let mut buffer = CoreBuffer::default();

    assert_eq!(
        unsafe {
            clipboard_snapshot_create(
                data_path.as_ptr(),
                data_path.len(),
                VAULT_ID.as_bytes().as_ptr(),
                8,
                KEY.as_ptr(),
                KEY.len(),
                root_path.as_ptr(),
                root_path.len(),
                1_760_000_000_000,
                3,
                &mut buffer,
            )
        },
        CoreStatus::InvalidArgument
    );

    assert_eq!(
        unsafe {
            clipboard_snapshot_list(
                root_path.as_ptr(),
                root_path.len(),
                VAULT_ID.as_bytes().as_ptr(),
                16,
                KEY.as_ptr(),
                31,
                &mut buffer,
            )
        },
        CoreStatus::InvalidArgument
    );
}
