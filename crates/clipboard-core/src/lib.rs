#![forbid(unsafe_code)]

//! Clipboard use-case orchestration and versioned command protocol.

mod command;
mod error;
mod file_cache;
mod object_store;
mod response;
mod service;

pub use command::{
    ApiRequest, ApplyRetentionRequest, CacheFileBundle, CoreCommand, DeleteRequest,
    IngestFileBundle, IngestImage, IngestText, ProbeRemote, ReadFileBundle, SearchFilters,
    SearchRequest, SetFavorite, SyncDirectory, SyncRemote, UncacheFileBundle,
};
pub use error::CoreError;
pub use response::{CoreResponse, SearchItem, SyncDirectoryResponse};
pub use service::{CoreService, MAX_IMAGE_BYTES};

pub const CRATE_READY: bool = true;
