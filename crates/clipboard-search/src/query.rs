use serde::{Deserialize, Serialize};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Deserialize, Serialize)]
#[serde(rename_all = "snake_case")]
pub enum SearchMode {
    Substring,
    Regex,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SearchQuery {
    pub pattern: String,
    pub mode: SearchMode,
}

impl SearchQuery {
    pub fn new(pattern: impl Into<String>, mode: SearchMode) -> Self {
        Self {
            pattern: pattern.into(),
            mode,
        }
    }
}
