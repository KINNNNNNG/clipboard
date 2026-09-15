//! Update discovery and installer retrieval for the Windows client.

use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::cmp::Ordering;
use std::fs::{self, File};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::Duration;
use thiserror::Error;

/// GitHub API endpoint for the newest published release of this repository.
pub const RELEASE_API_URL: &str =
    "https://api.github.com/repos/KINNNNNNG/clipboard/releases/latest";
/// Asset name prefix of the packaged installer.
pub const INSTALLER_NAME_PREFIX: &str = "Clipboard-Setup-";
/// Name of the published checksum manifest.
pub const CHECKSUMS_FILE_NAME: &str = "SHA256SUMS.txt";
/// Upper bound for the downloaded installer.
pub const MAX_INSTALLER_BYTES: u64 = 200 * 1024 * 1024;
/// Upper bound for metadata responses.
pub const MAX_METADATA_BYTES: u64 = 1024 * 1024;

const ALLOWED_HOSTS: [&str; 4] = [
    "api.github.com",
    "github.com",
    "objects.githubusercontent.com",
    "release-assets.githubusercontent.com",
];

#[derive(Debug, Error)]
pub enum UpdateError {
    #[error("current version is invalid: {0}")]
    InvalidCurrentVersion(String),
    #[error("release metadata is invalid: {0}")]
    InvalidRelease(String),
    #[error("release is missing required asset: {0}")]
    MissingAsset(String),
    #[error("url is not allowed: {0}")]
    UnsupportedUrl(String),
    #[error("update transport failed: {0}")]
    Transport(String),
    #[error("published checksums do not contain {0}")]
    ChecksumMissing(String),
    #[error("installer checksum mismatch: expected {expected}, actual {actual}")]
    ChecksumMismatch { expected: String, actual: String },
    #[error("installer is {actual} bytes; maximum is {maximum} bytes")]
    InstallerTooLarge { actual: u64, maximum: u64 },
    #[error(transparent)]
    Io(#[from] std::io::Error),
}

/// A strict `MAJOR.MINOR.PATCH` version with optional prerelease identifiers.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseVersion {
    major: u64,
    minor: u64,
    patch: u64,
    prerelease: Vec<PrereleasePart>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
enum PrereleasePart {
    Numeric(u64),
    Text(String),
}

impl ReleaseVersion {
    /// Parses `1.2.3`, `v1.2.3` and `1.2.3-rc.1`. Build metadata is ignored.
    pub fn parse(value: &str) -> Option<Self> {
        let trimmed = value.trim();
        let without_prefix = trimmed.strip_prefix('v').unwrap_or(trimmed);
        let without_build = without_prefix.split('+').next()?;
        let (numbers, prerelease) = match without_build.split_once('-') {
            Some((numbers, prerelease)) => (numbers, Some(prerelease)),
            None => (without_build, None),
        };

        let mut segments = numbers.split('.');
        let major = parse_numeric(segments.next()?)?;
        let minor = parse_numeric(segments.next()?)?;
        let patch = parse_numeric(segments.next()?)?;
        if segments.next().is_some() {
            return None;
        }

        let prerelease = match prerelease {
            None => Vec::new(),
            Some(text) => {
                if text.is_empty() {
                    return None;
                }
                let mut parts = Vec::new();
                for part in text.split('.') {
                    if part.is_empty() {
                        return None;
                    }
                    parts.push(match parse_numeric(part) {
                        Some(number) => PrereleasePart::Numeric(number),
                        None => PrereleasePart::Text(part.to_owned()),
                    });
                }
                parts
            }
        };

        Some(Self {
            major,
            minor,
            patch,
            prerelease,
        })
    }

    pub fn is_prerelease(&self) -> bool {
        !self.prerelease.is_empty()
    }
}

impl std::fmt::Display for ReleaseVersion {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(formatter, "{}.{}.{}", self.major, self.minor, self.patch)?;
        if self.prerelease.is_empty() {
            return Ok(());
        }
        write!(formatter, "-")?;
        for (index, part) in self.prerelease.iter().enumerate() {
            if index > 0 {
                write!(formatter, ".")?;
            }
            match part {
                PrereleasePart::Numeric(number) => write!(formatter, "{number}")?,
                PrereleasePart::Text(text) => write!(formatter, "{text}")?,
            }
        }
        Ok(())
    }
}

impl Ord for ReleaseVersion {
    fn cmp(&self, other: &Self) -> Ordering {
        self.major
            .cmp(&other.major)
            .then_with(|| self.minor.cmp(&other.minor))
            .then_with(|| self.patch.cmp(&other.patch))
            .then_with(|| compare_prerelease(&self.prerelease, &other.prerelease))
    }
}

impl PartialOrd for ReleaseVersion {
    fn partial_cmp(&self, other: &Self) -> Option<Ordering> {
        Some(self.cmp(other))
    }
}

fn parse_numeric(value: &str) -> Option<u64> {
    if value.is_empty() || !value.bytes().all(|byte| byte.is_ascii_digit()) {
        return None;
    }
    if value.len() > 1 && value.starts_with('0') {
        return None;
    }
    value.parse().ok()
}

fn compare_prerelease(left: &[PrereleasePart], right: &[PrereleasePart]) -> Ordering {
    match (left.is_empty(), right.is_empty()) {
        (true, true) => return Ordering::Equal,
        (true, false) => return Ordering::Greater,
        (false, true) => return Ordering::Less,
        (false, false) => {}
    }

    for (left_part, right_part) in left.iter().zip(right.iter()) {
        let ordering = match (left_part, right_part) {
            (PrereleasePart::Numeric(left_number), PrereleasePart::Numeric(right_number)) => {
                left_number.cmp(right_number)
            }
            (PrereleasePart::Numeric(_), PrereleasePart::Text(_)) => Ordering::Less,
            (PrereleasePart::Text(_), PrereleasePart::Numeric(_)) => Ordering::Greater,
            (PrereleasePart::Text(left_text), PrereleasePart::Text(right_text)) => {
                left_text.cmp(right_text)
            }
        };
        if ordering != Ordering::Equal {
            return ordering;
        }
    }

    left.len().cmp(&right.len())
}

#[derive(Debug, Deserialize)]
struct GitHubRelease {
    tag_name: String,
    #[serde(default)]
    html_url: Option<String>,
    #[serde(default)]
    published_at: Option<String>,
    #[serde(default)]
    prerelease: bool,
    #[serde(default)]
    assets: Vec<GitHubAsset>,
}

#[derive(Debug, Deserialize)]
struct GitHubAsset {
    name: String,
    browser_download_url: String,
}

/// Release assets that the updater is allowed to consume.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ReleaseAssets {
    pub version: ReleaseVersion,
    pub installer_name: String,
    pub installer_url: String,
    pub checksums_url: String,
    pub release_url: Option<String>,
    pub published_at: Option<String>,
    pub prerelease: bool,
}

/// Parses the GitHub release payload and rejects anything the updater must not consume.
pub fn parse_release(json: &str) -> Result<ReleaseAssets, UpdateError> {
    let release: GitHubRelease = serde_json::from_str(json)
        .map_err(|error| UpdateError::InvalidRelease(error.to_string()))?;
    let version = ReleaseVersion::parse(&release.tag_name)
        .ok_or_else(|| UpdateError::InvalidRelease(format!("tag {}", release.tag_name)))?;

    let installer_name = format!("{INSTALLER_NAME_PREFIX}v{version}.exe");
    let installer = release
        .assets
        .iter()
        .find(|asset| asset.name == installer_name)
        .ok_or_else(|| UpdateError::MissingAsset(installer_name.clone()))?;
    ensure_allowed_url(&installer.browser_download_url)?;

    let checksums = release
        .assets
        .iter()
        .find(|asset| asset.name == CHECKSUMS_FILE_NAME)
        .ok_or_else(|| UpdateError::MissingAsset(CHECKSUMS_FILE_NAME.to_owned()))?;
    ensure_allowed_url(&checksums.browser_download_url)?;

    Ok(ReleaseAssets {
        version,
        installer_name,
        installer_url: installer.browser_download_url.clone(),
        checksums_url: checksums.browser_download_url.clone(),
        release_url: release.html_url,
        published_at: release.published_at,
        prerelease: release.prerelease,
    })
}

/// Reads the expected SHA-256 of `file_name` from a published checksum manifest.
pub fn parse_checksum(manifest: &str, file_name: &str) -> Option<String> {
    for line in manifest.lines() {
        let mut parts = line.split_whitespace();
        let Some(hash) = parts.next() else { continue };
        let Some(name) = parts.next() else { continue };
        if name.trim_start_matches('*') != file_name {
            continue;
        }
        let normalized = hash.to_ascii_lowercase();
        if normalized.len() == 64 && normalized.bytes().all(|byte| byte.is_ascii_hexdigit()) {
            return Some(normalized);
        }
    }
    None
}

/// Restricts downloads to the hosts the updater trusts.
pub fn ensure_allowed_url(url: &str) -> Result<(), UpdateError> {
    let rest = url
        .strip_prefix("https://")
        .ok_or_else(|| UpdateError::UnsupportedUrl(url.to_owned()))?;
    let authority = rest.split(['/', '?', '#']).next().unwrap_or_default();
    let host = authority.rsplit('@').next().unwrap_or(authority);
    let host = host.split(':').next().unwrap_or(host);
    if ALLOWED_HOSTS
        .iter()
        .any(|allowed| host.eq_ignore_ascii_case(allowed))
    {
        return Ok(());
    }
    Err(UpdateError::UnsupportedUrl(url.to_owned()))
}

/// Streams the file through SHA-256 without loading it into memory.
pub fn sha256_file(path: &Path) -> Result<String, UpdateError> {
    let mut file = File::open(path)?;
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 64 * 1024];
    loop {
        let read = file.read(&mut buffer)?;
        if read == 0 {
            break;
        }
        hasher.update(&buffer[..read]);
    }
    Ok(hex::encode(hasher.finalize()))
}

/// Transport seam so update logic stays testable without network access.
pub trait UpdateTransport {
    fn fetch_text(&self, url: &str, max_bytes: u64) -> Result<String, UpdateError>;

    fn download_file(
        &self,
        url: &str,
        destination: &Path,
        max_bytes: u64,
    ) -> Result<u64, UpdateError>;
}

/// Blocking HTTPS transport used by the packaged client.
pub struct HttpUpdateTransport {
    client: reqwest::blocking::Client,
}

impl HttpUpdateTransport {
    pub fn new() -> Result<Self, UpdateError> {
        let client = reqwest::blocking::Client::builder()
            .user_agent(format!("Clipboard/{}", env!("CARGO_PKG_VERSION")))
            .timeout(Duration::from_secs(120))
            .build()
            .map_err(|error| UpdateError::Transport(error.to_string()))?;
        Ok(Self { client })
    }
}

impl UpdateTransport for HttpUpdateTransport {
    fn fetch_text(&self, url: &str, max_bytes: u64) -> Result<String, UpdateError> {
        ensure_allowed_url(url)?;
        let response = self
            .client
            .get(url)
            .send()
            .map_err(|error| UpdateError::Transport(error.to_string()))?;
        if !response.status().is_success() {
            return Err(UpdateError::Transport(format!(
                "unexpected status {}",
                response.status()
            )));
        }
        let mut reader = response.take(max_bytes + 1);
        let mut buffer = Vec::new();
        reader.read_to_end(&mut buffer)?;
        if buffer.len() as u64 > max_bytes {
            return Err(UpdateError::Transport(
                "response exceeds the maximum metadata size".to_owned(),
            ));
        }
        String::from_utf8(buffer).map_err(|error| UpdateError::Transport(error.to_string()))
    }

    fn download_file(
        &self,
        url: &str,
        destination: &Path,
        max_bytes: u64,
    ) -> Result<u64, UpdateError> {
        ensure_allowed_url(url)?;
        let response = self
            .client
            .get(url)
            .send()
            .map_err(|error| UpdateError::Transport(error.to_string()))?;
        if !response.status().is_success() {
            return Err(UpdateError::Transport(format!(
                "unexpected status {}",
                response.status()
            )));
        }
        if let Some(length) = response.content_length()
            && length > max_bytes
        {
            return Err(UpdateError::InstallerTooLarge {
                actual: length,
                maximum: max_bytes,
            });
        }

        let mut file = File::create(destination)?;
        let mut reader = response.take(max_bytes + 1);
        let mut buffer = [0u8; 64 * 1024];
        let mut written = 0u64;
        loop {
            let read = reader.read(&mut buffer)?;
            if read == 0 {
                break;
            }
            written += read as u64;
            if written > max_bytes {
                return Err(UpdateError::InstallerTooLarge {
                    actual: written,
                    maximum: max_bytes,
                });
            }
            file.write_all(&buffer[..read])?;
        }
        file.flush()?;
        Ok(written)
    }
}

/// Result of comparing the running client against the newest release.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UpdateCheckOutcome {
    pub available: bool,
    pub current_version: String,
    pub latest_version: String,
    pub installer_url: Option<String>,
    pub checksums_url: Option<String>,
    pub release_url: Option<String>,
    pub published_at: Option<String>,
}

/// Result of a verified installer download.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct UpdateDownloadOutcome {
    pub installer_path: PathBuf,
    pub version: ReleaseVersion,
    pub size_bytes: u64,
}

/// Reports whether the newest published release is newer than `current_version`.
pub fn check_update(
    current_version: &str,
    include_prerelease: bool,
    transport: &dyn UpdateTransport,
) -> Result<UpdateCheckOutcome, UpdateError> {
    let current = ReleaseVersion::parse(current_version)
        .ok_or_else(|| UpdateError::InvalidCurrentVersion(current_version.to_owned()))?;
    let payload = transport.fetch_text(RELEASE_API_URL, MAX_METADATA_BYTES)?;
    let release = parse_release(&payload)?;
    let prerelease = release.prerelease || release.version.is_prerelease();
    let available = release.version > current && (include_prerelease || !prerelease);

    Ok(UpdateCheckOutcome {
        available,
        current_version: current.to_string(),
        latest_version: release.version.to_string(),
        installer_url: available.then(|| release.installer_url.clone()),
        checksums_url: available.then(|| release.checksums_url.clone()),
        release_url: release.release_url.clone(),
        published_at: release.published_at.clone(),
    })
}

/// Downloads the installer and only keeps it when its SHA-256 matches the manifest.
pub fn download_update(
    version: &str,
    installer_url: &str,
    checksums_url: &str,
    target_dir: &Path,
    transport: &dyn UpdateTransport,
) -> Result<UpdateDownloadOutcome, UpdateError> {
    let version = ReleaseVersion::parse(version)
        .ok_or_else(|| UpdateError::InvalidRelease(version.to_owned()))?;
    ensure_allowed_url(installer_url)?;
    ensure_allowed_url(checksums_url)?;

    let installer_name = installer_file_name(installer_url)?;
    let expected_name = format!("{INSTALLER_NAME_PREFIX}v{version}.exe");
    if installer_name != expected_name {
        return Err(UpdateError::InvalidRelease(format!(
            "installer asset {installer_name} does not match {expected_name}"
        )));
    }

    fs::create_dir_all(target_dir)?;
    let manifest = transport.fetch_text(checksums_url, MAX_METADATA_BYTES)?;
    let expected = parse_checksum(&manifest, &installer_name)
        .ok_or_else(|| UpdateError::ChecksumMissing(installer_name.clone()))?;

    let partial = target_dir.join(format!("{installer_name}.partial"));
    let size = transport.download_file(installer_url, &partial, MAX_INSTALLER_BYTES)?;
    let actual = sha256_file(&partial)?;
    if actual != expected {
        let _ = fs::remove_file(&partial);
        return Err(UpdateError::ChecksumMismatch { expected, actual });
    }

    let destination = target_dir.join(&installer_name);
    fs::rename(&partial, &destination)?;
    Ok(UpdateDownloadOutcome {
        installer_path: destination,
        version,
        size_bytes: size,
    })
}

fn installer_file_name(url: &str) -> Result<String, UpdateError> {
    let without_scheme = url
        .strip_prefix("https://")
        .ok_or_else(|| UpdateError::UnsupportedUrl(url.to_owned()))?;
    let path = without_scheme.split(['?', '#']).next().unwrap_or_default();
    let name = path.rsplit('/').next().unwrap_or_default();
    if name.is_empty() {
        return Err(UpdateError::InvalidRelease(format!(
            "installer url {url} has no file name"
        )));
    }
    Ok(name.to_owned())
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::collections::HashMap;
    use tempfile::TempDir;

    const README_HASH: &str = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    struct FakeTransport {
        text: HashMap<String, String>,
        files: HashMap<String, Vec<u8>>,
    }

    impl FakeTransport {
        fn new() -> Self {
            Self {
                text: HashMap::new(),
                files: HashMap::new(),
            }
        }

        fn with_text(mut self, url: &str, body: &str) -> Self {
            self.text.insert(url.to_owned(), body.to_owned());
            self
        }

        fn with_file(mut self, url: &str, bytes: Vec<u8>) -> Self {
            self.files.insert(url.to_owned(), bytes);
            self
        }
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

    const CHECKSUMS_URL: &str =
        "https://github.com/KINNNNNNG/clipboard/releases/download/v0.2.0/SHA256SUMS.txt";

    fn release_payload(version: &str, prerelease: bool, installer_url: &str) -> String {
        let installer_name = format!("Clipboard-Setup-v{version}.exe");
        format!(
            r#"{{"tag_name":"v{version}","html_url":"https://github.com/KINNNNNNG/clipboard/releases/tag/v{version}","published_at":"2026-09-15T00:00:00Z","prerelease":{prerelease},"assets":[{{"name":"{installer_name}","browser_download_url":"{installer_url}"}},{{"name":"SHA256SUMS.txt","browser_download_url":"{CHECKSUMS_URL}"}}]}}"#
        )
    }

    #[test]
    fn version_parsing_accepts_release_and_prerelease_forms() {
        assert_eq!(ReleaseVersion::parse("0.1.0").unwrap().to_string(), "0.1.0");
        assert_eq!(
            ReleaseVersion::parse("v0.2.10").unwrap().to_string(),
            "0.2.10"
        );
        assert_eq!(
            ReleaseVersion::parse("1.2.3-rc.1").unwrap().to_string(),
            "1.2.3-rc.1"
        );
        assert_eq!(
            ReleaseVersion::parse("1.2.3+build.5").unwrap().to_string(),
            "1.2.3"
        );

        for invalid in ["1.2", "1.2.3.4", "01.2.3", "1.2.3-", "v", "1.2.x", ""] {
            assert!(
                ReleaseVersion::parse(invalid).is_none(),
                "{invalid} must be rejected"
            );
        }
    }

    #[test]
    fn version_ordering_follows_semantic_precedence() {
        let ordered = [
            "0.1.0",
            "0.1.1",
            "0.2.0",
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-beta",
            "1.0.0-rc.1",
            "1.0.0",
        ];
        for pair in ordered.windows(2) {
            let left = ReleaseVersion::parse(pair[0]).unwrap();
            let right = ReleaseVersion::parse(pair[1]).unwrap();
            assert!(left < right, "{} must sort before {}", pair[0], pair[1]);
        }
    }

    #[test]
    fn release_parsing_requires_the_expected_assets() {
        let payload = release_payload("0.2.0", false, &installer_url("0.2.0"));
        let assets = parse_release(&payload).unwrap();
        assert_eq!(assets.version.to_string(), "0.2.0");
        assert_eq!(assets.installer_name, "Clipboard-Setup-v0.2.0.exe");
        assert_eq!(assets.checksums_url, CHECKSUMS_URL);
        assert_eq!(assets.published_at.as_deref(), Some("2026-09-15T00:00:00Z"));
    }

    #[test]
    fn release_parsing_rejects_missing_assets_and_untrusted_hosts() {
        let missing = r#"{"tag_name":"v0.2.0","assets":[{"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/SHA256SUMS.txt"}]}"#;
        assert!(matches!(
            parse_release(missing),
            Err(UpdateError::MissingAsset(name)) if name == "Clipboard-Setup-v0.2.0.exe"
        ));

        let untrusted = r#"{"tag_name":"v0.2.0","assets":[{"name":"Clipboard-Setup-v0.2.0.exe","browser_download_url":"http://evil.example/Clipboard-Setup-v0.2.0.exe"},{"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/x/SHA256SUMS.txt"}]}"#;
        assert!(matches!(
            parse_release(untrusted),
            Err(UpdateError::UnsupportedUrl(_))
        ));

        let bad_tag = r#"{"tag_name":"latest","assets":[]}"#;
        assert!(matches!(
            parse_release(bad_tag),
            Err(UpdateError::InvalidRelease(_))
        ));
    }

    #[test]
    fn url_allow_list_accepts_release_hosts_only() {
        assert!(ensure_allowed_url("https://api.github.com/repos/x/y").is_ok());
        assert!(ensure_allowed_url("https://github.com/x/y/releases/download/v1/a.exe").is_ok());
        assert!(ensure_allowed_url("https://objects.githubusercontent.com/x").is_ok());
        assert!(ensure_allowed_url("http://github.com/x").is_err());
        assert!(ensure_allowed_url("https://evil.example/a.exe").is_err());
        assert!(ensure_allowed_url("https://github.com.evil.example/a.exe").is_err());
    }

    #[test]
    fn checksum_manifest_lookup_is_exact() {
        let manifest =
            format!("{README_HASH}  other.exe\n{README_HASH}  Clipboard-Setup-v0.2.0.exe\n");
        assert_eq!(
            parse_checksum(&manifest, "Clipboard-Setup-v0.2.0.exe").as_deref(),
            Some(README_HASH)
        );
        assert!(parse_checksum(&manifest, "Clipboard-Setup-v0.3.0.exe").is_none());
        assert!(
            parse_checksum(
                "deadbeef  Clipboard-Setup-v0.2.0.exe",
                "Clipboard-Setup-v0.2.0.exe"
            )
            .is_none()
        );
    }

    #[test]
    fn check_update_reports_only_newer_releases() {
        let newer = FakeTransport::new().with_text(
            RELEASE_API_URL,
            &release_payload("0.2.0", false, &installer_url("0.2.0")),
        );
        let outcome = check_update("0.1.0", false, &newer).unwrap();
        assert!(outcome.available);
        assert_eq!(outcome.latest_version, "0.2.0");
        assert_eq!(
            outcome.installer_url.as_deref(),
            Some(installer_url("0.2.0").as_str())
        );

        let same = FakeTransport::new().with_text(
            RELEASE_API_URL,
            &release_payload("0.1.0", false, &installer_url("0.1.0")),
        );
        let outcome = check_update("0.1.0", false, &same).unwrap();
        assert!(!outcome.available);
        assert!(outcome.installer_url.is_none());

        let older = FakeTransport::new().with_text(
            RELEASE_API_URL,
            &release_payload("0.0.9", false, &installer_url("0.0.9")),
        );
        assert!(!check_update("0.1.0", false, &older).unwrap().available);
    }

    #[test]
    fn check_update_skips_prereleases_unless_requested() {
        let payload = release_payload("0.2.0", true, &installer_url("0.2.0"));
        let transport = FakeTransport::new().with_text(RELEASE_API_URL, &payload);
        assert!(!check_update("0.1.0", false, &transport).unwrap().available);
        assert!(check_update("0.1.0", true, &transport).unwrap().available);
    }

    #[test]
    fn check_update_rejects_an_invalid_current_version() {
        let transport = FakeTransport::new();
        assert!(matches!(
            check_update("unknown", false, &transport),
            Err(UpdateError::InvalidCurrentVersion(_))
        ));
    }

    #[test]
    fn download_update_keeps_only_a_verified_installer() {
        let directory = TempDir::new().unwrap();
        let payload = b"installer-bytes".to_vec();
        let expected = {
            let mut hasher = Sha256::new();
            hasher.update(&payload);
            hex::encode(hasher.finalize())
        };
        let manifest = format!("{expected}  Clipboard-Setup-v0.2.0.exe\n");
        let url = installer_url("0.2.0");
        let transport = FakeTransport::new()
            .with_text(CHECKSUMS_URL, &manifest)
            .with_file(&url, payload.clone());

        let outcome =
            download_update("0.2.0", &url, CHECKSUMS_URL, directory.path(), &transport).unwrap();
        assert_eq!(outcome.size_bytes, payload.len() as u64);
        assert_eq!(
            outcome.installer_path.file_name().unwrap(),
            "Clipboard-Setup-v0.2.0.exe"
        );
        assert_eq!(fs::read(&outcome.installer_path).unwrap(), payload);
        assert!(
            !directory
                .path()
                .join("Clipboard-Setup-v0.2.0.exe.partial")
                .exists()
        );
    }

    #[test]
    fn download_update_discards_a_mismatched_installer() {
        let directory = TempDir::new().unwrap();
        let manifest = format!("{README_HASH}  Clipboard-Setup-v0.2.0.exe\n");
        let url = installer_url("0.2.0");
        let transport = FakeTransport::new()
            .with_text(CHECKSUMS_URL, &manifest)
            .with_file(&url, b"tampered".to_vec());

        let error = download_update("0.2.0", &url, CHECKSUMS_URL, directory.path(), &transport)
            .unwrap_err();
        assert!(matches!(error, UpdateError::ChecksumMismatch { .. }));
        assert!(!directory.path().join("Clipboard-Setup-v0.2.0.exe").exists());
        assert!(
            !directory
                .path()
                .join("Clipboard-Setup-v0.2.0.exe.partial")
                .exists()
        );
    }

    #[test]
    fn download_update_rejects_a_version_mismatched_asset_name() {
        let directory = TempDir::new().unwrap();
        let transport = FakeTransport::new();
        let error = download_update(
            "0.2.0",
            &installer_url("0.3.0"),
            CHECKSUMS_URL,
            directory.path(),
            &transport,
        )
        .unwrap_err();
        assert!(matches!(error, UpdateError::InvalidRelease(_)));
    }
}
