#![deny(unsafe_op_in_unsafe_fn)]

//! Stable C ABI for the shared clipboard core.

mod abi;
mod buffer;
mod status;

pub use abi::{
    CoreHandle, clipboard_core_close, clipboard_core_execute, clipboard_core_free_buffer,
    clipboard_core_ingest_image, clipboard_core_open, clipboard_core_open_v2,
    clipboard_core_read_image, clipboard_pairing_file_decode, clipboard_pairing_file_encode,
    clipboard_recovery_decode, clipboard_recovery_encode, clipboard_snapshot_create,
    clipboard_snapshot_list, clipboard_snapshot_restore,
};
pub use buffer::CoreBuffer;
pub use status::CoreStatus;

pub const CRATE_READY: bool = true;
