#[repr(i32)]
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum CoreStatus {
    Ok = 0,
    InvalidArgument = 1,
    InvalidUtf8 = 2,
    InvalidJson = 3,
    CoreError = 4,
    Panic = 5,
    InvalidRegex = 6,
}
