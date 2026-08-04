use clipboard_domain::normalize_windows_path;

#[test]
fn normalizes_dot_segments_and_rejects_paths_outside_roots() {
    assert_eq!(
        normalize_windows_path(r"C:\Docs\.\Sub\..\A.txt\\").unwrap(),
        r"c:\docs\a.txt"
    );
    assert_eq!(
        normalize_windows_path(r"\\Server\Share\Dir\..\A.txt").unwrap(),
        r"\\server\share\a.txt"
    );

    for path in [r"C:\..\a.txt", r"\\server\share\..\a.txt"] {
        assert!(normalize_windows_path(path).is_err(), "{path}");
    }
}

#[test]
fn rejects_device_namespaces_and_incomplete_unc_paths() {
    for path in [
        r"\\",
        r"\\server",
        "\\\\server\\",
        r"\\.\pipe\name",
        r"\\?\C:\path",
    ] {
        assert!(normalize_windows_path(path).is_err(), "{path}");
    }
}
