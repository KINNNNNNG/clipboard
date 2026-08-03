use crate::{SearchError, SearchMode, SearchQuery};
use regex::RegexBuilder;

struct SearchDocument {
    id: String,
    searchable: String,
}

pub struct SearchEngine {
    documents: Vec<SearchDocument>,
}

impl SearchEngine {
    pub fn from_documents<I, Id, Text, PathText>(documents: I) -> Self
    where
        I: IntoIterator<Item = (Id, Text, PathText)>,
        Id: Into<String>,
        Text: Into<String>,
        PathText: Into<String>,
    {
        let documents = documents
            .into_iter()
            .map(|(id, text, path)| {
                let text = text.into();
                let path = path.into();
                let searchable = if path.is_empty() {
                    text
                } else {
                    format!("{text}\n{path}")
                };
                SearchDocument {
                    id: id.into(),
                    searchable,
                }
            })
            .collect();
        Self { documents }
    }

    pub fn search(&self, query: &SearchQuery) -> Result<Vec<String>, SearchError> {
        match query.mode {
            SearchMode::Substring => {
                let pattern = query.pattern.to_lowercase();
                Ok(self
                    .documents
                    .iter()
                    .filter(|document| document.searchable.to_lowercase().contains(&pattern))
                    .map(|document| document.id.clone())
                    .collect())
            }
            SearchMode::Regex => {
                let regex = RegexBuilder::new(&query.pattern)
                    .case_insensitive(true)
                    .size_limit(2 * 1024 * 1024)
                    .dfa_size_limit(4 * 1024 * 1024)
                    .build()
                    .map_err(|error| SearchError::InvalidRegex(error.to_string()))?;
                Ok(self
                    .documents
                    .iter()
                    .filter(|document| regex.is_match(&document.searchable))
                    .map(|document| document.id.clone())
                    .collect())
            }
        }
    }
}
