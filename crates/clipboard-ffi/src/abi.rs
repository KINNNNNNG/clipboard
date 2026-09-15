use crate::{CoreBuffer, CoreStatus};
use clipboard_core::{
    ApiRequest, CoreError, CoreService, IngestImage, MAX_IMAGE_BYTES, UpdateError,
};
use clipboard_storage::StorageError;
use clipboard_sync::{
    PairingFileMaterial, RecoveryMaterial, decode_pairing_file, decode_recovery_code,
    encode_pairing_file, encode_recovery_code,
};
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

/// Decrypts a password-protected pairing file into one JSON material buffer.
///
/// # Safety
///
/// All non-null input pointers must remain valid for their supplied lengths during this call.
/// `out_material` must point to writable `CoreBuffer` storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_pairing_file_decode(
    file_ptr: *const u8,
    file_len: usize,
    password_ptr: *const u8,
    password_len: usize,
    out_material: *mut CoreBuffer,
) -> CoreStatus {
    catch_status(|| unsafe {
        pairing_file_decode_impl(file_ptr, file_len, password_ptr, password_len, out_material)
    })
}

/// Encodes vault and non-sensitive remote configuration into a password-protected pairing file.
///
/// # Safety
///
/// All non-null input pointers must remain valid for their supplied lengths during this call.
/// `out_file` must point to writable `CoreBuffer` storage.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn clipboard_pairing_file_encode(
    vault_id_ptr: *const u8,
    vault_id_len: usize,
    master_key_ptr: *const u8,
    master_key_len: usize,
    provider_ptr: *const u8,
    provider_len: usize,
    endpoint_ptr: *const u8,
    endpoint_len: usize,
    root_ptr: *const u8,
    root_len: usize,
    bucket_ptr: *const u8,
    bucket_len: usize,
    region_ptr: *const u8,
    region_len: usize,
    password_ptr: *const u8,
    password_len: usize,
    out_file: *mut CoreBuffer,
) -> CoreStatus {
    catch_status(|| unsafe {
        pairing_file_encode_impl(
            vault_id_ptr,
            vault_id_len,
            master_key_ptr,
            master_key_len,
            provider_ptr,
            provider_len,
            endpoint_ptr,
            endpoint_len,
            root_ptr,
            root_len,
            bucket_ptr,
            bucket_len,
            region_ptr,
            region_len,
            password_ptr,
            password_len,
            out_file,
        )
    })
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
        Err(error) => return status_for_core_error(error),
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

unsafe fn pairing_file_decode_impl(
    file_ptr: *const u8,
    file_len: usize,
    password_ptr: *const u8,
    password_len: usize,
    out_material: *mut CoreBuffer,
) -> CoreStatus {
    if file_ptr.is_null()
        || file_len == 0
        || password_ptr.is_null()
        || password_len == 0
        || out_material.is_null()
    {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_material, CoreBuffer::default()) };
    let file = unsafe { slice::from_raw_parts(file_ptr, file_len) };
    let password =
        match str::from_utf8(unsafe { slice::from_raw_parts(password_ptr, password_len) }) {
            Ok(value) => value,
            Err(_) => return CoreStatus::InvalidUtf8,
        };
    let material = match decode_pairing_file(file, password) {
        Ok(value) => value,
        Err(_) => return CoreStatus::CoreError,
    };
    let bytes = match pairing_material_json(&material) {
        Ok(value) => value,
        Err(_) => return CoreStatus::CoreError,
    };
    unsafe { ptr::write(out_material, CoreBuffer::from_vec(bytes)) };
    CoreStatus::Ok
}

#[allow(clippy::too_many_arguments)]
unsafe fn pairing_file_encode_impl(
    vault_id_ptr: *const u8,
    vault_id_len: usize,
    master_key_ptr: *const u8,
    master_key_len: usize,
    provider_ptr: *const u8,
    provider_len: usize,
    endpoint_ptr: *const u8,
    endpoint_len: usize,
    root_ptr: *const u8,
    root_len: usize,
    bucket_ptr: *const u8,
    bucket_len: usize,
    region_ptr: *const u8,
    region_len: usize,
    password_ptr: *const u8,
    password_len: usize,
    out_file: *mut CoreBuffer,
) -> CoreStatus {
    if vault_id_ptr.is_null()
        || vault_id_len != 16
        || master_key_ptr.is_null()
        || master_key_len != 32
        || provider_ptr.is_null()
        || provider_len == 0
        || endpoint_ptr.is_null()
        || endpoint_len == 0
        || password_ptr.is_null()
        || password_len == 0
        || out_file.is_null()
        || (root_len != 0 && root_ptr.is_null())
        || (bucket_len != 0 && bucket_ptr.is_null())
        || (region_len != 0 && region_ptr.is_null())
    {
        return CoreStatus::InvalidArgument;
    }
    unsafe { ptr::write(out_file, CoreBuffer::default()) };
    let vault_id =
        match Uuid::from_slice(unsafe { slice::from_raw_parts(vault_id_ptr, vault_id_len) }) {
            Ok(value) => value,
            Err(_) => return CoreStatus::InvalidArgument,
        };
    let master_key = unsafe { slice::from_raw_parts(master_key_ptr, master_key_len) };
    let provider =
        match str::from_utf8(unsafe { slice::from_raw_parts(provider_ptr, provider_len) }) {
            Ok(value) => value,
            Err(_) => return CoreStatus::InvalidUtf8,
        };
    let endpoint =
        match str::from_utf8(unsafe { slice::from_raw_parts(endpoint_ptr, endpoint_len) }) {
            Ok(value) => value,
            Err(_) => return CoreStatus::InvalidUtf8,
        };
    let root = if root_len == 0 {
        None
    } else {
        Some(
            match str::from_utf8(unsafe { slice::from_raw_parts(root_ptr, root_len) }) {
                Ok(value) => value.to_owned(),
                Err(_) => return CoreStatus::InvalidUtf8,
            },
        )
    };
    let bucket = if bucket_len == 0 {
        None
    } else {
        Some(
            match str::from_utf8(unsafe { slice::from_raw_parts(bucket_ptr, bucket_len) }) {
                Ok(value) => value.to_owned(),
                Err(_) => return CoreStatus::InvalidUtf8,
            },
        )
    };
    let region = if region_len == 0 {
        None
    } else {
        Some(
            match str::from_utf8(unsafe { slice::from_raw_parts(region_ptr, region_len) }) {
                Ok(value) => value.to_owned(),
                Err(_) => return CoreStatus::InvalidUtf8,
            },
        )
    };
    let password =
        match str::from_utf8(unsafe { slice::from_raw_parts(password_ptr, password_len) }) {
            Ok(value) => value,
            Err(_) => return CoreStatus::InvalidUtf8,
        };
    let mut key = [0_u8; 32];
    key.copy_from_slice(master_key);
    let mut material = PairingFileMaterial::new(
        vault_id,
        key,
        provider.to_owned(),
        endpoint.to_owned(),
        root,
    );
    match (bucket, region) {
        (Some(bucket), Some(region)) => {
            material = material.with_oss_configuration(bucket, region);
        }
        (None, None) => {}
        _ => return CoreStatus::InvalidArgument,
    }
    let encoded = match encode_pairing_file(&material, password) {
        Ok(value) => value,
        Err(_) => return CoreStatus::CoreError,
    };
    unsafe { ptr::write(out_file, CoreBuffer::from_vec(encoded)) };
    CoreStatus::Ok
}

fn pairing_material_json(material: &PairingFileMaterial) -> Result<Vec<u8>, serde_json::Error> {
    #[derive(serde::Serialize)]
    struct WireMaterial<'a> {
        vault_id: Uuid,
        master_key_hex: String,
        provider: &'a str,
        endpoint: &'a str,
        root_or_prefix: &'a Option<String>,
        bucket: &'a Option<String>,
        region: &'a Option<String>,
    }
    serde_json::to_vec(&WireMaterial {
        vault_id: material.vault_id,
        master_key_hex: hex::encode(material.master_key),
        provider: &material.provider,
        endpoint: &material.endpoint,
        root_or_prefix: &material.root_or_prefix,
        bucket: &material.bucket,
        region: &material.region,
    })
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
        CoreError::Storage(error) => status_for_storage_error(error),
        CoreError::UpdateCheck(error) => {
            status_for_update_error(error, CoreStatus::UpdateCheckFailed)
        }
        CoreError::UpdateDownload(error) => {
            status_for_update_error(error, CoreStatus::UpdateDownloadFailed)
        }
        _ => CoreStatus::CoreError,
    }
}

fn status_for_update_error(error: UpdateError, fallback: CoreStatus) -> CoreStatus {
    match error {
        UpdateError::ChecksumMismatch { .. } | UpdateError::ChecksumMissing(_) => {
            CoreStatus::UpdateChecksumMismatch
        }
        _ => fallback,
    }
}

fn status_for_storage_error(error: StorageError) -> CoreStatus {
    match error {
        StorageError::Locked => CoreStatus::StorageLocked,
        StorageError::VaultMismatch => CoreStatus::VaultKeyMismatch,
        StorageError::Unreadable | StorageError::VaultMarkerInvalid => CoreStatus::VaultUnreadable,
        StorageError::Corrupt => CoreStatus::VaultCorrupt,
        StorageError::UnsupportedSchemaVersion { .. } | StorageError::InvalidMigrationHistory => {
            CoreStatus::StorageMigration
        }
        _ => CoreStatus::CoreError,
    }
}

fn catch_status(operation: impl FnOnce() -> CoreStatus) -> CoreStatus {
    catch_unwind(AssertUnwindSafe(operation)).unwrap_or(CoreStatus::Panic)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn update_errors_map_to_dedicated_status_codes() {
        assert_eq!(
            status_for_core_error(CoreError::UpdateCheck(UpdateError::Transport(
                "offline".to_owned()
            ))),
            CoreStatus::UpdateCheckFailed
        );
        assert_eq!(
            status_for_core_error(CoreError::UpdateDownload(UpdateError::Transport(
                "offline".to_owned()
            ))),
            CoreStatus::UpdateDownloadFailed
        );
        assert_eq!(
            status_for_core_error(CoreError::UpdateDownload(UpdateError::ChecksumMissing(
                "Clipboard-Setup-v0.2.0.exe".to_owned()
            ))),
            CoreStatus::UpdateChecksumMismatch
        );
        assert_eq!(
            status_for_core_error(CoreError::UpdateCheck(UpdateError::ChecksumMismatch {
                expected: "a".to_owned(),
                actual: "b".to_owned(),
            })),
            CoreStatus::UpdateChecksumMismatch
        );
    }

    #[test]
    fn status_codes_keep_their_published_values() {
        assert_eq!(CoreStatus::Ok as i32, 0);
        assert_eq!(CoreStatus::StorageLocked as i32, 7);
        assert_eq!(CoreStatus::StorageMigration as i32, 11);
        assert_eq!(CoreStatus::UpdateCheckFailed as i32, 12);
        assert_eq!(CoreStatus::UpdateDownloadFailed as i32, 13);
        assert_eq!(CoreStatus::UpdateChecksumMismatch as i32, 14);
    }
}
