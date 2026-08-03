use clipboard_search::{SearchEngine, SearchError, SearchMode, SearchQuery};

#[test]
fn substring_search_matches_chinese_text_case_insensitively() {
    let engine =
        SearchEngine::from_documents([("1", "项目部署地址", ""), ("2", "HELLO Codex", "")]);

    assert_eq!(
        engine
            .search(&SearchQuery::new("hello", SearchMode::Substring))
            .unwrap(),
        vec!["2"]
    );
    assert_eq!(
        engine
            .search(&SearchQuery::new("部署", SearchMode::Substring))
            .unwrap(),
        vec!["1"]
    );
}

#[test]
fn regex_search_matches_local_file_path() {
    let engine = SearchEngine::from_documents([("f1", "report.docx", r"C:\work\2026\report.docx")]);

    assert_eq!(
        engine
            .search(&SearchQuery::new(r"2026\\.*\.docx$", SearchMode::Regex,))
            .unwrap(),
        vec!["f1"]
    );
}

#[test]
fn invalid_regex_is_reported() {
    let engine = SearchEngine::from_documents([("1", "clipboard", "")]);

    assert!(matches!(
        engine.search(&SearchQuery::new("[", SearchMode::Regex)),
        Err(SearchError::InvalidRegex(_))
    ));
}

#[test]
fn search_results_preserve_document_order() {
    let engine = SearchEngine::from_documents([
        ("newest", "clipboard", ""),
        ("older", "clipboard history", ""),
    ]);

    assert_eq!(
        engine
            .search(&SearchQuery::new("clipboard", SearchMode::Substring))
            .unwrap(),
        vec!["newest", "older"]
    );
}
