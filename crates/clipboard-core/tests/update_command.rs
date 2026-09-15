use clipboard_core::{
    ApiRequest, CheckUpdate, CoreCommand, CoreResponse, CoreService, DownloadUpdate, UpdateError,
    UpdateTransport,
};
use std::collections::HashMap;
use std::fs;
use std::path::Path;
use tempfile::tempdir;
use uuid::Uuid;

const KEY: [u8; 32] = [0x52; 32];
const VAULT_ID: Uuid = Uuid::from_u128(0x42);
const CHECKSUMS_URL: &str =
    "https://github.com/KINNNNNNG/clipboard/releases/download/v0.2.0/SHA256SUMS.txt";
const RELEASE_API_URL: &str = "https://api.github.com/repos/KINNNNNNG/clipboard/releases/latest";

struct FakeTransport {
    text: HashMap<String, String>,
    files: HashMap<String, Vec<u8>>,
}

impl UpdateTransport for FakeTransport {
    fn fetch_text(&self, url: &str, max_bytes: u64) -> Result<String, UpdateError> {
        let body = self
            .text
            .get(url)
            .ok_or_else(|| UpdateError::Transport(format!("no body for {url}")))?;
        if body.len() as u64 > max_bytes {
            return Err(UpdateError::Transport("too large".to_owned()));
        }
        Ok(body.clone())
    }

    fn download_file(
        &self,
        url: &str,
        destination: &Path,
        max_bytes: u64,
    ) -> Result<u64, UpdateError> {
        let bytes = self
            .files
            .get(url)
            .ok_or_else(|| UpdateError::Transport(format!("no payload for {url}")))?;
        if bytes.len() as u64 > max_bytes {
            return Err(UpdateError::InstallerTooLarge {
                actual: bytes.len() as u64,
                maximum: max_bytes,
            });
        }
        fs::write(destination, bytes)?;
        Ok(bytes.len() as u64)
    }
}

fn installer_url(version: &str) -> String {
    format!(
        "https://github.com/KINNNNNNG/clipboard/releases/download/v{version}/Clipboard-Setup-v{version}.exe"
    )
}

fn release_payload(version: &str) -> String {
    format!(
        r#"{{"tag_name":"v{version}","html_url":"https://github.com/KINNNNNNG/clipboard/releases/tag/v{version}","published_at":"2026-09-15T00:00:00Z","prerelease":false,"assets":[{{"name":"Clipboard-Setup-v{version}.exe","browser_download_url":"{}"}},{{"name":"SHA256SUMS.txt","browser_download_url":"{CHECKSUMS_URL}"}}]}}"#,
        installer_url(version)
    )
}

fn transport_with_release(version: &str) -> FakeTransport {
    let mut text = HashMap::new();
    text.insert(RELEASE_API_URL.to_owned(), release_payload(version));
    FakeTransport {
        text,
        files: HashMap::new(),
    }
}

#[test]
fn update_commands_deserialize_from_the_versioned_envelope() {
    let check: ApiRequest = serde_json::from_str(
        r#"{"api_version":1,"type":"check_update","payload":{"current_version":"0.1.0","include_prerelease":true}}"#,
    )
    .unwrap();
    match check.validate().unwrap() {
        CoreCommand::CheckUpdate(command) => {
            assert_eq!(command.current_version, "0.1.0");
            assert!(command.include_prerelease);
        }
        _ => panic!("expected check_update command"),
    }

    let defaulted: ApiRequest = serde_json::from_str(
        r#"{"api_version":1,"type":"check_update","payload":{"current_version":"0.1.0"}}"#,
    )
    .unwrap();
    match defaulted.validate().unwrap() {
        CoreCommand::CheckUpdate(command) => assert!(!command.include_prerelease),
        _ => panic!("expected check_update command"),
    }

    let download: ApiRequest = serde_json::from_str(
        r#"{"api_version":1,"type":"download_update","payload":{"version":"0.2.0","installer_url":"https://github.com/a/b.exe","checksums_url":"https://github.com/a/SHA256SUMS.txt","target_dir":"C:\\updates"}}"#,
    )
    .unwrap();
    match download.validate().unwrap() {
        CoreCommand::DownloadUpdate(command) => assert_eq!(command.version, "0.2.0"),
        _ => panic!("expected download_update command"),
    }
}

#[test]
fn check_update_reports_the_newer_release_through_the_service() {
    let data = tempdir().unwrap();
    let service = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    let transport = transport_with_release("0.2.0");

    let response = service
        .check_update_with_transport(
            CheckUpdate {
                current_version: "0.1.0".to_owned(),
                include_prerelease: false,
            },
            &transport,
        )
        .unwrap();

    let json = serde_json::to_value(&response).unwrap();
    assert_eq!(json["available"], serde_json::json!(true));
    assert_eq!(json["latest_version"], serde_json::json!("0.2.0"));
    assert_eq!(json["current_version"], serde_json::json!("0.1.0"));
    assert_eq!(
        json["installer_url"],
        serde_json::json!(installer_url("0.2.0"))
    );
}

#[test]
fn check_update_reports_no_update_when_the_release_is_not_newer() {
    let data = tempdir().unwrap();
    let service = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();
    let transport = transport_with_release("0.1.0");

    let response = service
        .check_update_with_transport(
            CheckUpdate {
                current_version: "0.1.0".to_owned(),
                include_prerelease: false,
            },
            &transport,
        )
        .unwrap();

    let json = serde_json::to_value(&response).unwrap();
    assert_eq!(json["available"], serde_json::json!(false));
    assert!(json["installer_url"].is_null());
}

#[test]
fn download_update_returns_a_verified_installer_path() {
    let data = tempdir().unwrap();
    let target = tempdir().unwrap();
    let service = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();

    let payload = b"installer-payload".to_vec();
    let expected = {
        use sha2::{Digest, Sha256};
        let mut hasher = Sha256::new();
        hasher.update(&payload);
        hex::encode(hasher.finalize())
    };
    let manifest = format!("{expected}  Clipboard-Setup-v0.2.0.exe\n");

    let mut text = HashMap::new();
    text.insert(CHECKSUMS_URL.to_owned(), manifest);
    let mut files = HashMap::new();
    files.insert(installer_url("0.2.0"), payload.clone());
    let transport = FakeTransport { text, files };

    let response = service
        .download_update_with_transport(
            DownloadUpdate {
                version: "0.2.0".to_owned(),
                installer_url: installer_url("0.2.0"),
                checksums_url: CHECKSUMS_URL.to_owned(),
                target_dir: target.path().to_string_lossy().into_owned(),
            },
            &transport,
        )
        .unwrap();

    let json = serde_json::to_value(&response).unwrap();
    assert_eq!(json["version"], serde_json::json!("0.2.0"));
    assert_eq!(json["size_bytes"], serde_json::json!(payload.len()));
    let installer_path = json["installer_path"].as_str().unwrap();
    assert!(installer_path.ends_with("Clipboard-Setup-v0.2.0.exe"));
    assert_eq!(fs::read(installer_path).unwrap(), payload);
}

#[test]
fn download_update_surfaces_a_checksum_mismatch_as_a_download_error() {
    let data = tempdir().unwrap();
    let target = tempdir().unwrap();
    let service = CoreService::open(data.path(), VAULT_ID, &KEY).unwrap();

    let mut text = HashMap::new();
    text.insert(
        CHECKSUMS_URL.to_owned(),
        "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855  Clipboard-Setup-v0.2.0.exe\n"
            .to_owned(),
    );
    let mut files = HashMap::new();
    files.insert(installer_url("0.2.0"), b"tampered".to_vec());
    let transport = FakeTransport { text, files };

    let error = service
        .download_update_with_transport(
            DownloadUpdate {
                version: "0.2.0".to_owned(),
                installer_url: installer_url("0.2.0"),
                checksums_url: CHECKSUMS_URL.to_owned(),
                target_dir: target.path().to_string_lossy().into_owned(),
            },
            &transport,
        )
        .unwrap_err();

    assert!(matches!(
        error,
        clipboard_core::CoreError::UpdateDownload(UpdateError::ChecksumMismatch { .. })
    ));
}

#[test]
fn update_response_variants_serialize_flat() {
    let check = CoreResponse::UpdateCheck {
        available: false,
        current_version: "0.1.0".to_owned(),
        latest_version: "0.1.0".to_owned(),
        installer_url: None,
        checksums_url: None,
        release_url: None,
        published_at: None,
    };
    let json = serde_json::to_value(&check).unwrap();
    assert!(json.get("available").is_some() && json.get("latest_version").is_some());

    let download = CoreResponse::UpdateDownload {
        version: "0.2.0".to_owned(),
        installer_path: "C:\\updates\\Clipboard-Setup-v0.2.0.exe".to_owned(),
        size_bytes: 7,
    };
    let json = serde_json::to_value(&download).unwrap();
    assert!(json.get("installer_path").is_some() && json.get("size_bytes").is_some());
}
