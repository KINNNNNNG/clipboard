use clipboard_ffi::{
    CoreBuffer, CoreHandle, CoreStatus, clipboard_core_close, clipboard_core_execute,
    clipboard_core_free_buffer, clipboard_core_open, clipboard_recovery_decode,
    clipboard_recovery_encode,
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

#[test]
fn execute_reports_invalid_regex_separately_from_other_core_errors() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let handle = unsafe { open_handle(&path) };
    let request = br#"{"api_version":1,"type":"search","payload":{"pattern":"[","mode":"regex"}}"#;
    let mut response = CoreBuffer::default();

    assert_eq!(
        unsafe { clipboard_core_execute(handle, request.as_ptr(), request.len(), &mut response) },
        CoreStatus::InvalidRegex
    );
    assert!(response.ptr.is_null());
    assert_eq!(response.len, 0);

    unsafe { clipboard_core_close(handle) };
}

#[test]
fn execute_accepts_remote_sync_json_and_does_not_return_credentials_on_setup_failure() {
    let directory = tempdir().unwrap();
    let path = directory.path().to_string_lossy();
    let handle = unsafe { open_handle(&path) };
    let request = br#"{"api_version":1,"type":"probe_remote","payload":{"remote":{"provider":"webdav","version":1,"endpoint":"not-a-url","username":"alice","password":"secret"}}}"#;
    let mut response = CoreBuffer::default();

    assert_eq!(
        unsafe { clipboard_core_execute(handle, request.as_ptr(), request.len(), &mut response) },
        CoreStatus::CoreError
    );
    assert!(response.ptr.is_null());
    assert_eq!(response.len, 0);
    assert_eq!(response.capacity, 0);

    unsafe { clipboard_core_close(handle) };
}

#[test]
fn recovery_code_round_trips_without_returning_key_on_invalid_input() {
    let vault_id = uuid::Uuid::from_u128(0xabcdef);
    let key = [0x7a_u8; 32];
    let mut encoded = CoreBuffer::default();
    assert_eq!(
        unsafe {
            clipboard_recovery_encode(
                vault_id.as_bytes().as_ptr(),
                vault_id.as_bytes().len(),
                key.as_ptr(),
                key.len(),
                &mut encoded,
            )
        },
        CoreStatus::Ok
    );
    let code = unsafe { slice::from_raw_parts(encoded.ptr, encoded.len) }.to_vec();
    unsafe { clipboard_core_free_buffer(encoded) };

    let mut material = CoreBuffer::default();
    assert_eq!(
        unsafe { clipboard_recovery_decode(code.as_ptr(), code.len(), &mut material) },
        CoreStatus::Ok
    );
    let decoded = unsafe { slice::from_raw_parts(material.ptr, material.len) };
    assert_eq!(decoded.len(), 48);
    assert_eq!(&decoded[..16], vault_id.as_bytes());
    assert_eq!(&decoded[16..], &key);
    unsafe { clipboard_core_free_buffer(material) };

    let mut invalid = CoreBuffer::default();
    assert_eq!(
        unsafe { clipboard_recovery_decode(b"invalid".as_ptr(), 7, &mut invalid) },
        CoreStatus::CoreError
    );
    assert!(invalid.ptr.is_null());
    assert_eq!(invalid.len, 0);
}
