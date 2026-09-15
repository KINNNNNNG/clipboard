#![forbid(unsafe_code)]

//! Clipboard use-case orchestration and versioned command protocol.

mod command;
mod error;
mod file_cache;
mod object_store;
mod response;
mod service;
mod snapshot;
mod update;

pub use command::{
    ApiRequest, ApplyRetentionRequest, CacheFileBundle, CheckUpdate, CoreCommand, DeleteRequest,
    DownloadUpdate, IngestFileBundle, IngestImage, IngestText, MarkUsed, ProbeRemote,
    ReadFileBundle, SearchFilters, SearchRequest, SetFavorite, SyncDirectory, SyncRemote,
    UncacheFileBundle,
};
pub use error::CoreError;
pub use response::{CoreResponse, SearchItem, SyncDirectoryResponse};
pub use service::{CoreService, MAX_IMAGE_BYTES};
pub use snapshot::{
    DEFAULT_SNAPSHOT_KEEP, MAX_SNAPSHOT_KEEP, SNAPSHOT_DIRECTORY_PREFIX, SNAPSHOT_FORMAT_VERSION,
    SNAPSHOT_MANIFEST_FILE_NAME, SnapshotEntry, SnapshotError, SnapshotManifest, SnapshotSummary,
    clamp_keep, create as create_snapshot, list as list_snapshots, prune as prune_snapshots,
    restore as restore_snapshot, verify as verify_snapshot,
};
pub use update::{
    CHECKSUMS_FILE_NAME, HttpUpdateTransport, MAX_INSTALLER_BYTES, RELEASE_API_URL, ReleaseAssets,
    ReleaseVersion, UpdateCheckOutcome, UpdateDownloadOutcome, UpdateError, UpdateTransport,
    check_update, download_update, ensure_allowed_url, parse_checksum, parse_release, sha256_file,
};

pub const CRATE_READY: bool = true;
