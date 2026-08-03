use clipboard_core::MAX_IMAGE_BYTES;
use clipboard_ffi::{
    CoreBuffer, CoreHandle, CoreStatus, clipboard_core_close, clipboard_core_free_buffer,
    clipboard_core_ingest_image, clipboard_core_open_v2, clipboard_core_read_image,
};
use clipboard_storage::Database;
use std::{fs, ptr, slice};
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x71; 32];

unsafe fn open_v2(path: &str, vault_id: Uuid) -> *mut CoreHandle {
    let mut handle = ptr::null_mut();
    assert_eq!(
        unsafe {
            clipboard_core_open_v2(
                path.as_ptr(),
                path.len(),
                KEY.as_ptr(),
                KEY.len(),
                vault_id.as_bytes().as_ptr(),
                vault_id.as_bytes().len(),
                &mut handle,
            )
        },
        CoreStatus::Ok
    );
    assert!(!handle.is_null());
    handle
}

#[test]
fn image_abi_round_trips_binary_png_and_uses_requested_vault() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let vault_id = Uuid::from_u128(201);
    let handle = unsafe { open_v2(&path, vault_id) };
    let png = png_fixture();
    let item_id = unsafe { ingest(handle, &png) };

    let item_id_text = item_id.to_string();
    let mut output = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_core_read_image(
                handle,
                item_id_text.as_ptr(),
                item_id_text.len(),
                &mut output,
            )
        },
        CoreStatus::Ok
    );
    assert_eq!(
        unsafe { slice::from_raw_parts(output.ptr, output.len) },
        png
    );
    unsafe {
        clipboard_core_free_buffer(output);
        clipboard_core_close(handle);
    }

    let database = Database::open(&directory.path().join("history.db"), &KEY).unwrap();
    let stored = database.items().list().unwrap();
    assert_eq!(stored.len(), 1);
    assert_eq!(stored[0].vault_id, vault_id);
}

#[test]
fn image_abi_rejects_invalid_pointers_lengths_and_identifiers() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let vault_id = Uuid::from_u128(202);
    let mut handle = ptr::null_mut();
    assert_eq!(
        unsafe {
            clipboard_core_open_v2(
                path.as_ptr(),
                path.len(),
                KEY.as_ptr(),
                KEY.len(),
                vault_id.as_bytes().as_ptr(),
                15,
                &mut handle,
            )
        },
        CoreStatus::InvalidArgument
    );
    assert!(handle.is_null());

    let handle = unsafe { open_v2(&path, vault_id) };
    let metadata = metadata_json();
    let png = png_fixture();
    let mut output = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_core_ingest_image(
                handle,
                ptr::null(),
                metadata.len(),
                png.as_ptr(),
                png.len(),
                &mut output,
            )
        },
        CoreStatus::InvalidArgument
    );
    assert_eq!(
        unsafe {
            clipboard_core_ingest_image(
                handle,
                metadata.as_ptr(),
                metadata.len(),
                png.as_ptr(),
                0,
                &mut output,
            )
        },
        CoreStatus::InvalidArgument
    );
    assert_eq!(
        unsafe {
            clipboard_core_ingest_image(
                handle,
                metadata.as_ptr(),
                metadata.len(),
                png.as_ptr(),
                MAX_IMAGE_BYTES + 1,
                &mut output,
            )
        },
        CoreStatus::InvalidArgument
    );

    let invalid_id = b"not-a-uuid";
    assert_eq!(
        unsafe {
            clipboard_core_read_image(handle, invalid_id.as_ptr(), invalid_id.len(), &mut output)
        },
        CoreStatus::InvalidArgument
    );
    assert!(output.ptr.is_null());
    unsafe { clipboard_core_close(handle) };
}

#[test]
fn image_abi_reports_tamper_as_core_error_without_returning_output() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let handle = unsafe { open_v2(&path, Uuid::from_u128(203)) };
    let item_id = unsafe { ingest(handle, &png_fixture()) };
    let object_path = fs::read_dir(directory.path().join("objects"))
        .unwrap()
        .filter_map(Result::ok)
        .map(|entry| entry.path())
        .find(|path| path.is_file())
        .unwrap();
    let mut encrypted = fs::read(&object_path).unwrap();
    *encrypted.last_mut().unwrap() ^= 0x40;
    fs::write(object_path, encrypted).unwrap();

    let item_id_text = item_id.to_string();
    let mut output = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_core_read_image(
                handle,
                item_id_text.as_ptr(),
                item_id_text.len(),
                &mut output,
            )
        },
        CoreStatus::CoreError
    );
    assert!(output.ptr.is_null());
    assert_eq!(output.len, 0);
    unsafe { clipboard_core_close(handle) };
}

unsafe fn ingest(handle: *mut CoreHandle, png: &[u8]) -> Uuid {
    let metadata = metadata_json();
    let mut output = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_core_ingest_image(
                handle,
                metadata.as_ptr(),
                metadata.len(),
                png.as_ptr(),
                png.len(),
                &mut output,
            )
        },
        CoreStatus::Ok
    );
    let response = unsafe { slice::from_raw_parts(output.ptr, output.len) };
    let json: serde_json::Value = serde_json::from_slice(response).unwrap();
    let item_id = Uuid::parse_str(json["item_id"].as_str().unwrap()).unwrap();
    unsafe { clipboard_core_free_buffer(output) };
    item_id
}

fn metadata_json() -> Vec<u8> {
    br#"{"api_version":1,"width":1,"height":1,"source_app":"mspaint.exe","captured_ms":100}"#
        .to_vec()
}

fn png_fixture() -> Vec<u8> {
    hex::decode(include_str!("../../../tests/fixtures/1x1.png.hex").trim()).unwrap()
}
