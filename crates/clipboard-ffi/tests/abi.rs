use clipboard_ffi::{
    CoreBuffer, CoreHandle, CoreStatus, clipboard_core_close, clipboard_core_execute,
    clipboard_core_free_buffer, clipboard_core_open,
};
use std::{ptr, slice};
use tempfile::tempdir;

unsafe fn open_handle(path: &str) -> *mut CoreHandle {
    let key = [0x55_u8; 32];
    let mut handle = ptr::null_mut();
    assert_eq!(
        unsafe {
            clipboard_core_open(
                path.as_ptr(),
                path.len(),
                key.as_ptr(),
                key.len(),
                &mut handle,
            )
        },
        CoreStatus::Ok
    );
    assert!(!handle.is_null());
    handle
}

#[test]
fn abi_opens_executes_and_frees_response() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let handle = unsafe { open_handle(&path) };

    let ingest = br#"{"api_version":1,"type":"ingest_text","payload":{"text":"clipboard","source_app":"ffi-test","captured_ms":10}}"#;
    let mut response = CoreBuffer::default();
    assert_eq!(
        unsafe { clipboard_core_execute(handle, ingest.as_ptr(), ingest.len(), &mut response) },
        CoreStatus::Ok
    );
    unsafe { clipboard_core_free_buffer(response) };

    let search =
        br#"{"api_version":1,"type":"search","payload":{"pattern":"clip","mode":"substring"}}"#;
    let mut response = CoreBuffer::default();
    assert_eq!(
        unsafe { clipboard_core_execute(handle, search.as_ptr(), search.len(), &mut response) },
        CoreStatus::Ok
    );
    let bytes = unsafe { slice::from_raw_parts(response.ptr, response.len) };
    let json: serde_json::Value = serde_json::from_slice(bytes).unwrap();
    assert_eq!(json["items"].as_array().unwrap().len(), 1);

    unsafe {
        clipboard_core_free_buffer(response);
        clipboard_core_close(handle);
    }
}

#[test]
fn open_rejects_null_pointers_and_wrong_key_length() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let key = [0x55_u8; 32];
    let mut handle = ptr::null_mut();

    assert_eq!(
        unsafe {
            clipboard_core_open(
                ptr::null(),
                path.len(),
                key.as_ptr(),
                key.len(),
                &mut handle,
            )
        },
        CoreStatus::InvalidArgument
    );
    assert_eq!(
        unsafe {
            clipboard_core_open(
                path.as_ptr(),
                path.len(),
                key.as_ptr(),
                key.len() - 1,
                &mut handle,
            )
        },
        CoreStatus::InvalidArgument
    );
    assert_eq!(
        unsafe {
            clipboard_core_open(
                path.as_ptr(),
                path.len(),
                key.as_ptr(),
                key.len(),
                ptr::null_mut(),
            )
        },
        CoreStatus::InvalidArgument
    );
    assert!(handle.is_null());
}

#[test]
fn execute_reports_utf8_json_and_core_errors_without_allocating_output() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let handle = unsafe { open_handle(&path) };
    let cases: [(&[u8], CoreStatus); 3] = [
        (&[0xff], CoreStatus::InvalidUtf8),
        (b"{", CoreStatus::InvalidJson),
        (
            br#"{"api_version":2,"type":"search","payload":{"pattern":"x","mode":"substring"}}"#,
            CoreStatus::CoreError,
        ),
    ];

    for (request, expected) in cases {
        let mut response = CoreBuffer::default();
        assert_eq!(
            unsafe {
                clipboard_core_execute(handle, request.as_ptr(), request.len(), &mut response)
            },
            expected
        );
        assert!(response.ptr.is_null());
        assert_eq!(response.len, 0);
        assert_eq!(response.capacity, 0);
    }

    assert_eq!(
        unsafe { clipboard_core_execute(handle, ptr::null(), 1, ptr::null_mut()) },
        CoreStatus::InvalidArgument
    );
    unsafe { clipboard_core_close(handle) };
}
