#![forbid(unsafe_code)]

//! Clipboard use-case orchestration and versioned command protocol.

mod command;
mod error;
mod response;
mod service;

pub use command::{ApiRequest, CoreCommand, DeleteRequest, IngestText, SearchRequest, SetFavorite};
pub use error::CoreError;
pub use response::{CoreResponse, SearchItem};
pub use service::CoreService;

pub const CRATE_READY: bool = true;
