use crate::{CoreBuffer, CoreStatus};
use clipboard_core::{ApiRequest, CoreService};
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

    let service = match CoreService::open(Path::new(data_dir), Uuid::nil(), &vault_key) {
        Ok(service) => service,
        Err(_) => return CoreStatus::CoreError,
    };
    let handle = Box::new(CoreHandle {
        service: Mutex::new(service),
    });
    unsafe { ptr::write(out_handle, Box::into_raw(handle)) };
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
        Err(_) => return CoreStatus::CoreError,
    };
    let response_json = match serde_json::to_vec(&response) {
        Ok(json) => json,
        Err(_) => return CoreStatus::CoreError,
    };
    unsafe { ptr::write(out_response, CoreBuffer::from_vec(response_json)) };
    CoreStatus::Ok
}

fn catch_status(operation: impl FnOnce() -> CoreStatus) -> CoreStatus {
    catch_unwind(AssertUnwindSafe(operation)).unwrap_or(CoreStatus::Panic)
}
