use crate::{CoreBuffer, CoreStatus};
use clipboard_core::{ApiRequest, CoreError, CoreService, IngestImage, MAX_IMAGE_BYTES};
use clipboard_sync::{RecoveryMaterial, decode_recovery_code, encode_recovery_code};
use serde::Deserialize;
use std::{
    panic::{AssertUnwindSafe, catch_unwind},
    path::Path,
    ptr, slice, str,
    sync::Mutex,
};
use uuid::Uuid;
use zeroize::Zeroizing;

pub struct CoreHandle {
    service: Mutex<CoreService>,
}

/// Opens a clipboard core handle.
///
/// # Safety
///
/// All non-null pointers must remain valid for their supplied lengths during this call.
/// `out_handle` must point to writable memory for one handle pointer.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_open(
    data_dir_ptr: *const u8,
    data_dir_len: usize,
    vault_key_ptr: *const u8,
    vault_key_len: usize,
    out_handle: *mut *mut CoreHandle,
) -> CoreStatus {
    catch_status(|| unsafe {
        open_impl(
            data_dir_ptr,
            data_dir_len,
            vault_key_ptr,
            vault_key_len,
            Uuid::nil(),
            out_handle,
        )
    })
}

/// Opens a clipboard core handle for an explicit vault UUID.
///
/// # Safety
///
/// All non-null pointers must remain valid for their supplied lengths during this call.
/// `vault_id_ptr` must reference exactly 16 UUID bytes and `out_handle` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_open_v2(
    data_dir_ptr: *const u8,
    data_dir_len: usize,
    vault_key_ptr: *const u8,
    vault_key_len: usize,
    vault_id_ptr: *const u8,
    vault_id_len: usize,
    out_handle: *mut *mut CoreHandle,
) -> CoreStatus {
    catch_status(|| unsafe {
        if out_handle.is_null() {
            return CoreStatus::InvalidArgument;
        }
        ptr::write(out_handle, ptr::null_mut());
        if vault_id_ptr.is_null() || vault_id_len != 16 {
            return CoreStatus::InvalidArgument;
        }
        let vault_id_bytes = slice::from_raw_parts(vault_id_ptr, vault_id_len);
        let Ok(vault_id) = Uuid::from_slice(vault_id_bytes) else {
            return CoreStatus::InvalidArgument;
        };
        open_impl(
            data_dir_ptr,
            data_dir_len,
            vault_key_ptr,
            vault_key_len,
            vault_id,
            out_handle,
        )
    })
}

/// Executes one versioned JSON command.
///
/// # Safety
///
/// `handle` must come from `clipboard_core_open`. Input pointers must remain valid for their
/// lengths during this call. `out_response` must point to writable memory for one `CoreBuffer`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_execute(
    handle: *mut CoreHandle,
    request_ptr: *const u8,
    request_len: usize,
    out_response: *mut CoreBuffer,
) -> CoreStatus {
    catch_status(|| unsafe { execute_impl(handle, request_ptr, request_len, out_response) })
}

/// Ingests PNG bytes with versioned JSON metadata.
///
/// # Safety
///
/// `handle` must be a live core handle. All input pointers must remain valid for their supplied
/// lengths during this call and `out_response` must point to writable `CoreBuffer` storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_ingest_image(
    handle: *mut CoreHandle,
    metadata_json_ptr: *const u8,
    metadata_json_len: usize,
    png_ptr: *const u8,
    png_len: usize,
    out_response: *mut CoreBuffer,
) -> CoreStatus {
    catch_status(|| unsafe {
        ingest_image_impl(
            handle,
            metadata_json_ptr,
            metadata_json_len,
            png_ptr,
            png_len,
            out_response,
        )
    })
}

/// Reads decrypted PNG bytes for an image item UUID.
///
/// # Safety
///
/// `handle` must be a live core handle. `item_id_ptr` must remain valid for its supplied length
/// and `out_png` must point to writable `CoreBuffer` storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_read_image(
    handle: *mut CoreHandle,
    item_id_ptr: *const u8,
    item_id_len: usize,
    out_png: *mut CoreBuffer,
) -> CoreStatus {
    catch_status(|| unsafe { read_image_impl(handle, item_id_ptr, item_id_len, out_png) })
}

/// Encodes a vault UUID and 32-byte master key as a recovery code.
///
/// # Safety
///
/// Input pointers must remain valid for their supplied lengths and `out_code` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_recovery_encode(
    vault_id_ptr: *const u8,
    vault_id_len: usize,
    master_key_ptr: *const u8,
    master_key_len: usize,
    out_code: *mut CoreBuffer,
) -> CoreStatus {
    catch_status(|| unsafe {
        recovery_encode_impl(
            vault_id_ptr,
            vault_id_len,
            master_key_ptr,
            master_key_len,
            out_code,
        )
    })
}

/// Decodes a recovery code into 16 vault UUID bytes followed by a 32-byte master key.
///
/// # Safety
///
/// `code_ptr` must remain valid for its supplied length and `out_material` must be writable.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_recovery_decode(
    code_ptr: *const u8,
    code_len: usize,
    out_material: *mut CoreBuffer,
) -> CoreStatus {
    catch_status(|| unsafe { recovery_decode_impl(code_ptr, code_len, out_material) })
}

/// Frees a response allocated by `clipboard_core_execute`.
///
/// # Safety
///
/// `buffer` must be returned by a successful `clipboard_core_execute` call and must be freed once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_free_buffer(buffer: CoreBuffer) {
    let _ = catch_unwind(AssertUnwindSafe(|| unsafe { buffer.reclaim() }));
}

/// Closes a clipboard core handle.
///
/// # Safety
///
/// `handle` must be null or returned by `clipboard_core_open`, and may be closed only once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_core_close(handle: *mut CoreHandle) {
    let _ = catch_unwind(AssertUnwindSafe(|| {
        if !handle.is_null() {
            drop(unsafe { Box::from_raw(handle) });
        }
    }));
}

unsafe fn open_impl(
    data_dir_ptr: *const u8,
    data_dir_len: usize,
    vault_key_ptr: *const u8,
    vault_key_len: usize,
    vault_id: Uuid,
    out_handle: *mut *mut CoreHandle,
) -> CoreStatus {
    if out_handle.is_null() {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_handle, ptr::null_mut()) };
    if data_dir_ptr.is_null() || data_dir_len == 0 || vault_key_ptr.is_null() || vault_key_len != 32
    {
        return CoreStatus::InvalidArgument;
    }

    let data_dir_bytes = unsafe { slice::from_raw_parts(data_dir_ptr, data_dir_len) };
    let data_dir = match str::from_utf8(data_dir_bytes) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidUtf8,
    };
    let input_key = unsafe { slice::from_raw_parts(vault_key_ptr, vault_key_len) };
    let mut vault_key = Zeroizing::new([0_u8; 32]);
    vault_key.copy_from_slice(input_key);

    let service = match CoreService::open(Path::new(data_dir), vault_id, &vault_key) {
        Ok(service) => service,
        Err(_) => return CoreStatus::CoreError,
    };
    let handle = Box::new(CoreHandle {
        service: Mutex::new(service),
    });
    unsafe { ptr::write(out_handle, Box::into_raw(handle)) };
    CoreStatus::Ok
}

#[derive(Deserialize)]
struct ImageMetadataRequest {
    api_version: u32,
    width: u32,
    height: u32,
    source_app: String,
    captured_ms: i64,
    #[serde(default)]
    source_app_display_name: Option<String>,
}

unsafe fn ingest_image_impl(
    handle: *mut CoreHandle,
    metadata_json_ptr: *const u8,
    metadata_json_len: usize,
    png_ptr: *const u8,
    png_len: usize,
    out_response: *mut CoreBuffer,
) -> CoreStatus {
    if handle.is_null()
        || metadata_json_ptr.is_null()
        || metadata_json_len == 0
        || png_ptr.is_null()
        || png_len == 0
        || png_len > MAX_IMAGE_BYTES
        || out_response.is_null()
    {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_response, CoreBuffer::default()) };

    let metadata_bytes = unsafe { slice::from_raw_parts(metadata_json_ptr, metadata_json_len) };
    let metadata_json = match str::from_utf8(metadata_bytes) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidUtf8,
    };
    let metadata = match serde_json::from_str::<ImageMetadataRequest>(metadata_json) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidJson,
    };
    if metadata.api_version != 1 || metadata.width == 0 || metadata.height == 0 {
        return CoreStatus::InvalidArgument;
    }
    let png = unsafe { slice::from_raw_parts(png_ptr, png_len) };

    let handle = unsafe { &*handle };
    let service = match handle.service.lock() {
        Ok(service) => service,
        Err(_) => return CoreStatus::CoreError,
    };
    let response = match service.ingest_image(
        IngestImage {
            width: metadata.width,
            height: metadata.height,
            source_app: metadata.source_app,
            captured_ms: metadata.captured_ms,
            source_app_display_name: metadata.source_app_display_name,
        },
        png,
    ) {
        Ok(response) => response,
        Err(_) => return CoreStatus::CoreError,
    };
    let response_json = match serde_json::to_vec(&response) {
        Ok(json) => json,
        Err(_) => return CoreStatus::CoreError,
    };
    unsafe { ptr::write(out_response, CoreBuffer::from_vec(response_json)) };
    CoreStatus::Ok
}

unsafe fn read_image_impl(
    handle: *mut CoreHandle,
    item_id_ptr: *const u8,
    item_id_len: usize,
    out_png: *mut CoreBuffer,
) -> CoreStatus {
    if handle.is_null() || item_id_ptr.is_null() || item_id_len == 0 || out_png.is_null() {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_png, CoreBuffer::default()) };

    let item_id_bytes = unsafe { slice::from_raw_parts(item_id_ptr, item_id_len) };
    let item_id_text = match str::from_utf8(item_id_bytes) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidUtf8,
    };
    let item_id = match Uuid::parse_str(item_id_text) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidArgument,
    };

    let handle = unsafe { &*handle };
    let service = match handle.service.lock() {
        Ok(service) => service,
        Err(_) => return CoreStatus::CoreError,
    };
    let png = match service.read_image(item_id) {
        Ok(png) => png,
        Err(_) => return CoreStatus::CoreError,
    };
    unsafe { ptr::write(out_png, CoreBuffer::from_vec(png)) };
    CoreStatus::Ok
}

unsafe fn recovery_encode_impl(
    vault_id_ptr: *const u8,
    vault_id_len: usize,
    master_key_ptr: *const u8,
    master_key_len: usize,
    out_code: *mut CoreBuffer,
) -> CoreStatus {
    if vault_id_ptr.is_null()
        || vault_id_len != 16
        || master_key_ptr.is_null()
        || master_key_len != 32
        || out_code.is_null()
    {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_code, CoreBuffer::default()) };
    let vault_id =
        match Uuid::from_slice(unsafe { slice::from_raw_parts(vault_id_ptr, vault_id_len) }) {
            Ok(value) => value,
            Err(_) => return CoreStatus::InvalidArgument,
        };
    let mut master_key = Zeroizing::new([0_u8; 32]);
    master_key.copy_from_slice(unsafe { slice::from_raw_parts(master_key_ptr, master_key_len) });
    let code = match encode_recovery_code(&RecoveryMaterial::new(vault_id, *master_key)) {
        Ok(value) => value,
        Err(_) => return CoreStatus::CoreError,
    };
    unsafe { ptr::write(out_code, CoreBuffer::from_vec(code.into_bytes())) };
    CoreStatus::Ok
}

unsafe fn recovery_decode_impl(
    code_ptr: *const u8,
    code_len: usize,
    out_material: *mut CoreBuffer,
) -> CoreStatus {
    if code_ptr.is_null() || code_len == 0 || out_material.is_null() {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_material, CoreBuffer::default()) };
    let code = match str::from_utf8(unsafe { slice::from_raw_parts(code_ptr, code_len) }) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidUtf8,
    };
    let material = match decode_recovery_code(code) {
        Ok(value) => value,
        Err(_) => return CoreStatus::CoreError,
    };
    let mut bytes = Zeroizing::new([0_u8; 48]);
    bytes[..16].copy_from_slice(material.vault_id.as_bytes());
    bytes[16..].copy_from_slice(&material.master_key);
    unsafe { ptr::write(out_material, CoreBuffer::from_vec(bytes.to_vec())) };
    CoreStatus::Ok
}

unsafe fn execute_impl(
    handle: *mut CoreHandle,
    request_ptr: *const u8,
    request_len: usize,
    out_response: *mut CoreBuffer,
) -> CoreStatus {
    if handle.is_null() || request_ptr.is_null() || out_response.is_null() {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_response, CoreBuffer::default()) };

    let request_bytes = unsafe { slice::from_raw_parts(request_ptr, request_len) };
    let request_json = match str::from_utf8(request_bytes) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidUtf8,
    };
    let request = match serde_json::from_str::<ApiRequest>(request_json) {
        Ok(value) => value,
        Err(_) => return CoreStatus::InvalidJson,
    };
    let command = match request.validate() {
        Ok(command) => command,
        Err(_) => return CoreStatus::CoreError,
    };

    let handle = unsafe { &*handle };
    let mut service = match handle.service.lock() {
        Ok(service) => service,
        Err(_) => return CoreStatus::CoreError,
    };
    let response = match service.execute(command) {
        Ok(response) => response,
        Err(error) => return status_for_core_error(error),
    };
    let response_json = match serde_json::to_vec(&response) {
        Ok(json) => json,
        Err(_) => return CoreStatus::CoreError,
    };
    unsafe { ptr::write(out_response, CoreBuffer::from_vec(response_json)) };
    CoreStatus::Ok
}

fn status_for_core_error(error: CoreError) -> CoreStatus {
    match error {
        CoreError::Search(_) => CoreStatus::InvalidRegex,
        _ => CoreStatus::CoreError,
    }
}

fn catch_status(operation: impl FnOnce() -> CoreStatus) -> CoreStatus {
    catch_unwind(AssertUnwindSafe(operation)).unwrap_or(CoreStatus::Panic)
}
