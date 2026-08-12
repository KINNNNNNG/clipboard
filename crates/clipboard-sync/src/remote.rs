use std::fmt;

use serde::{Deserialize, Deserializer, Serialize};
use uuid::Uuid;

use crate::{SYNC_PROTOCOL_VERSION, SegmentHeader, SyncError};

pub const REMOTE_CONFIG_VERSION: u8 = 1;
pub const PENDING_OBJECT_SUFFIX: &str = ".pending";
const IMAGE_OBJECT_VERSION: u8 = 1;

/// A segment header admitted to the remote-store boundary.
///
/// The inner header is private so remote implementations cannot accept or return an unsupported
/// protocol version by accident.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RemoteSegmentHeader(SegmentHeader);

impl RemoteSegmentHeader {
    pub fn header(&self) -> &SegmentHeader {
        &self.0
    }
}

/// Identifies a versioned encrypted image object admitted to the remote-store boundary.
///
/// Image ciphertext is not interpreted by remote stores. The vault and object identifiers are
/// nevertheless explicit so a store cannot be asked to fetch an arbitrary path.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct RemoteImageObject {
    vault_id: Uuid,
    object_id: Uuid,
}

impl RemoteImageObject {
    pub fn new(vault_id: Uuid, object_id: Uuid) -> Self {
        Self {
            vault_id,
            object_id,
        }
    }

    pub fn vault_id(&self) -> Uuid {
        self.vault_id
    }

    pub fn object_id(&self) -> Uuid {
        self.object_id
    }
}

impl TryFrom<SegmentHeader> for RemoteSegmentHeader {
    type Error = SyncError;

    fn try_from(header: SegmentHeader) -> Result<Self, Self::Error> {
        validate_remote_segment_header(&header)?;
        Ok(Self(header))
    }
}

/// An encrypted-segment object store. Implementations never receive clipboard content.
pub trait RemoteStore: crate::RemoteMetadataStore + Send + Sync {
    fn list_completed(&self) -> Result<Vec<RemoteSegmentHeader>, SyncError>;
    fn get_completed(&self, header: &RemoteSegmentHeader) -> Result<Vec<u8>, SyncError>;
    fn put_pending_then_publish(
        &self,
        header: &RemoteSegmentHeader,
        ciphertext: &[u8],
    ) -> Result<(), SyncError>;
    fn get_image_object(&self, object: &RemoteImageObject) -> Result<Vec<u8>, SyncError>;
    fn put_image_object(
        &self,
        object: &RemoteImageObject,
        ciphertext: &[u8],
    ) -> Result<(), SyncError>;
    fn probe(&self) -> Result<(), SyncError>;

    fn last_error_code(&self) -> Option<String> {
        None
    }

    /// Returns a fixed, non-sensitive transport detail for the latest failed request.
    fn last_error_detail(&self) -> Option<String> {
        None
    }

    /// Returns a fixed operation identifier for the latest failed remote request.
    fn last_error_operation(&self) -> Option<String> {
        None
    }
}

#[derive(Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(tag = "provider")]
pub enum RemoteConfig {
    #[serde(rename = "webdav")]
    WebDav(WebDavConfig),
    #[serde(rename = "oss")]
    Oss(OssConfig),
}

impl RemoteConfig {
    pub fn webdav(
        endpoint: impl Into<String>,
        username: impl Into<String>,
        password: impl Into<String>,
    ) -> Self {
        Self::WebDav(WebDavConfig::new(endpoint, username, password))
    }

    pub fn oss(
        endpoint: impl Into<String>,
        region: impl Into<String>,
        bucket: impl Into<String>,
        prefix: impl Into<String>,
        access_key_id: impl Into<String>,
        access_key_secret: impl Into<String>,
    ) -> Self {
        Self::Oss(OssConfig::new(
            endpoint,
            region,
            bucket,
            prefix,
            access_key_id,
            access_key_secret,
        ))
    }
}

impl fmt::Debug for RemoteConfig {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        match self {
            Self::WebDav(config) => formatter
                .debug_struct("RemoteConfig::WebDav")
                .field("configuration_id", &config.configuration_id())
                .finish(),
            Self::Oss(config) => formatter
                .debug_struct("RemoteConfig::Oss")
                .field("configuration_id", &config.configuration_id())
                .finish(),
        }
    }
}

#[derive(Clone, PartialEq, Eq, Serialize)]
pub struct WebDavConfig {
    version: u8,
    endpoint: String,
    username: String,
    password: String,
}

impl WebDavConfig {
    pub fn new(
        endpoint: impl Into<String>,
        username: impl Into<String>,
        password: impl Into<String>,
    ) -> Self {
        Self {
            version: REMOTE_CONFIG_VERSION,
            endpoint: endpoint.into(),
            username: username.into(),
            password: password.into(),
        }
    }

    pub fn version(&self) -> u8 {
        self.version
    }

    pub fn endpoint(&self) -> &str {
        &self.endpoint
    }

    pub fn username(&self) -> &str {
        &self.username
    }

    pub fn password(&self) -> &str {
        &self.password
    }

    pub fn configuration_id(&self) -> String {
        config_identifier(&["webdav", &self.endpoint])
    }
}

impl fmt::Debug for WebDavConfig {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("WebDavConfig")
            .field("configuration_id", &self.configuration_id())
            .finish()
    }
}

impl<'de> Deserialize<'de> for WebDavConfig {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        #[derive(Deserialize)]
        struct WireConfig {
            version: u8,
            endpoint: String,
            username: String,
            password: String,
        }

        let config = WireConfig::deserialize(deserializer)?;
        if config.version != REMOTE_CONFIG_VERSION {
            return Err(serde::de::Error::custom(
                "unsupported remote configuration version",
            ));
        }
        Ok(Self {
            version: config.version,
            endpoint: config.endpoint,
            username: config.username,
            password: config.password,
        })
    }
}

#[derive(Clone, PartialEq, Eq, Serialize)]
pub struct OssConfig {
    version: u8,
    endpoint: String,
    region: String,
    bucket: String,
    prefix: String,
    access_key_id: String,
    access_key_secret: String,
}

impl OssConfig {
    pub fn new(
        endpoint: impl Into<String>,
        region: impl Into<String>,
        bucket: impl Into<String>,
        prefix: impl Into<String>,
        access_key_id: impl Into<String>,
        access_key_secret: impl Into<String>,
    ) -> Self {
        Self {
            version: REMOTE_CONFIG_VERSION,
            endpoint: endpoint.into(),
            region: region.into(),
            bucket: bucket.into(),
            prefix: prefix.into(),
            access_key_id: access_key_id.into(),
            access_key_secret: access_key_secret.into(),
        }
    }

    pub fn version(&self) -> u8 {
        self.version
    }

    pub fn endpoint(&self) -> &str {
        &self.endpoint
    }

    pub fn region(&self) -> &str {
        &self.region
    }

    pub fn bucket(&self) -> &str {
        &self.bucket
    }

    pub fn prefix(&self) -> &str {
        &self.prefix
    }

    pub fn access_key_id(&self) -> &str {
        &self.access_key_id
    }

    pub fn access_key_secret(&self) -> &str {
        &self.access_key_secret
    }

    pub fn configuration_id(&self) -> String {
        config_identifier(&[
            "oss",
            &self.endpoint,
            &self.region,
            &self.bucket,
            &self.prefix,
        ])
    }
}

impl fmt::Debug for OssConfig {
    fn fmt(&self, formatter: &mut fmt::Formatter<'_>) -> fmt::Result {
        formatter
            .debug_struct("OssConfig")
            .field("configuration_id", &self.configuration_id())
            .finish()
    }
}

impl<'de> Deserialize<'de> for OssConfig {
    fn deserialize<D>(deserializer: D) -> Result<Self, D::Error>
    where
        D: Deserializer<'de>,
    {
        #[derive(Deserialize)]
        struct WireConfig {
            version: u8,
            endpoint: String,
            region: String,
            bucket: String,
            prefix: String,
            access_key_id: String,
            access_key_secret: String,
        }

        let config = WireConfig::deserialize(deserializer)?;
        if config.version != REMOTE_CONFIG_VERSION {
            return Err(serde::de::Error::custom(
                "unsupported remote configuration version",
            ));
        }
        Ok(Self {
            version: config.version,
            endpoint: config.endpoint,
            region: config.region,
            bucket: config.bucket,
            prefix: config.prefix,
            access_key_id: config.access_key_id,
            access_key_secret: config.access_key_secret,
        })
    }
}

pub fn completed_object_name(header: &SegmentHeader) -> Result<String, SyncError> {
    validate_remote_segment_header(header)?;
    Ok(format!(
        "{:02x}-{}-{}-{}.enc",
        header.protocol_version, header.vault_id, header.device_id, header.segment_id
    ))
}

pub fn pending_object_name(header: &SegmentHeader) -> Result<String, SyncError> {
    Ok(format!(
        "{}{}",
        completed_object_name(header)?,
        PENDING_OBJECT_SUFFIX
    ))
}

pub fn completed_image_object_name(object: &RemoteImageObject) -> String {
    format!(
        "image-{:02x}-{}-{}.enc",
        IMAGE_OBJECT_VERSION, object.vault_id, object.object_id
    )
}

pub fn pending_image_object_name(object: &RemoteImageObject) -> String {
    format!(
        "{}{}",
        completed_image_object_name(object),
        PENDING_OBJECT_SUFFIX
    )
}

pub fn parse_completed_object_name(name: &str) -> Option<RemoteSegmentHeader> {
    let stem = name.strip_suffix(".enc")?;
    let mut parts = stem.split('-');
    let version = parts.next()?;
    if version.len() != 2
        || !version
            .bytes()
            .all(|byte| byte.is_ascii_digit() || matches!(byte, b'a'..=b'f'))
    {
        return None;
    }
    let protocol_version = u8::from_str_radix(version, 16).ok()?;
    if protocol_version != SYNC_PROTOCOL_VERSION {
        return None;
    }

    let header = SegmentHeader {
        protocol_version,
        vault_id: parse_uuid(&mut parts)?,
        device_id: parse_uuid(&mut parts)?,
        segment_id: parse_uuid(&mut parts)?,
    };
    if parts.next().is_some() {
        return None;
    }
    RemoteSegmentHeader::try_from(header).ok()
}

fn config_identifier(parts: &[&str]) -> String {
    let mut hasher = blake3::Hasher::new();
    for part in parts {
        hasher.update(&(part.len() as u64).to_le_bytes());
        hasher.update(part.as_bytes());
    }
    hasher.finalize().to_hex().to_string()
}

/// Validates that a segment can cross the remote-store boundary.
///
/// This is also used by [`RemoteSegmentHeader::try_from`] at the remote-store boundary.
pub fn validate_remote_segment_header(header: &SegmentHeader) -> Result<(), SyncError> {
    if header.protocol_version != SYNC_PROTOCOL_VERSION {
        return Err(SyncError::UnsupportedProtocolVersion(
            header.protocol_version,
        ));
    }
    Ok(())
}

fn parse_uuid<'a>(parts: &mut impl Iterator<Item = &'a str>) -> Option<Uuid> {
    let value = format!(
        "{}-{}-{}-{}-{}",
        parts.next()?,
        parts.next()?,
        parts.next()?,
        parts.next()?,
        parts.next()?
    );
    Uuid::parse_str(&value).ok()
}
