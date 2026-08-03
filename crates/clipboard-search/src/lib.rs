#![forbid(unsafe_code)]

//! Deterministic text, path and regular-expression search.

mod engine;
mod error;
mod query;

pub use engine::SearchEngine;
pub use error::SearchError;
pub use query::{SearchMode, SearchQuery};

pub const CRATE_READY: bool = true;
