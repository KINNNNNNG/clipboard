use std::{mem, ptr};

#[repr(C)]
#[derive(Debug)]
pub struct CoreBuffer {
    pub ptr: *mut u8,
    pub len: usize,
    pub capacity: usize,
}

impl CoreBuffer {
    pub(crate) fn from_vec(mut bytes: Vec<u8>) -> Self {
        let buffer = Self {
            ptr: bytes.as_mut_ptr(),
            len: bytes.len(),
            capacity: bytes.capacity(),
        };
        mem::forget(bytes);
        buffer
    }

    pub(crate) unsafe fn reclaim(self) {
        if self.ptr.is_null() {
            return;
        }
        drop(unsafe { Vec::from_raw_parts(self.ptr, self.len, self.capacity) });
    }
}

impl Default for CoreBuffer {
    fn default() -> Self {
        Self {
            ptr: ptr::null_mut(),
            len: 0,
            capacity: 0,
        }
    }
}
