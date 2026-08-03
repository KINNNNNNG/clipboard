use thiserror::Error;

#[derive(Debug, Error, PartialEq, Eq)]
pub enum SearchError {
    #[error("invalid regular expression: {0}")]
    InvalidRegex(String),
}
