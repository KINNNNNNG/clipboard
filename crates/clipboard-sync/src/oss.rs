use std::collections::BTreeMap;

use hmac::{Hmac, Mac};
use quick_xml::{Reader, events::Event};
use reqwest::{
    Method, StatusCode, Url,
    blocking::{Client, RequestBuilder, Response},
};
use sha2::{Digest, Sha256};
use time::{OffsetDateTime, macros::format_description};

use crate::{
    OssConfig, RemoteSegmentHeader, RemoteStore, SyncError, completed_object_name,
    parse_completed_object_name, pending_object_name,
};

type HmacSha256 = Hmac<Sha256>;
const OSS_SERVICE: &str = "oss";
const OSS_TERMINATOR: &str = "aliyun_v4_request";

pub struct OssStore {
    endpoint: Url,
    bucket: String,
    prefix: String,
    region: String,
    access_key_id: String,
    access_key_secret: String,
    path_style: bool,
    client: Client,
}

impl OssStore {
    pub fn new(config: OssConfig) -> Result<Self, SyncError> {
        let mut endpoint =
            Url::parse(config.endpoint()).map_err(|_| SyncError::RemoteUnavailable)?;
        endpoint.set_query(None);
        endpoint.set_fragment(None);
        let path_style = endpoint.host_str().is_none_or(|host| {
            host.parse::<std::net::IpAddr>().is_ok()
                || !host.starts_with(&format!("{}.", config.bucket()))
        });
        let client = Client::builder()
            .build()
            .map_err(|_| SyncError::RemoteUnavailable)?;
        Ok(Self {
            endpoint,
            bucket: config.bucket().to_owned(),
            prefix: config.prefix().trim_matches('/').to_owned(),
            region: config.region().to_owned(),
            access_key_id: config.access_key_id().to_owned(),
            access_key_secret: config.access_key_secret().to_owned(),
            path_style,
            client,
        })
    }

    fn object_key(&self, object_name: &str) -> String {
        if self.prefix.is_empty() {
            object_name.to_owned()
        } else {
            format!("{}/{}", self.prefix, object_name)
        }
    }

    fn object_url(&self, key: &str) -> Result<Url, SyncError> {
        let mut url = self.endpoint.clone();
        let path = if self.path_style {
            format!("/{}/{}", self.bucket, key)
        } else {
            format!("/{key}")
        };
        url.set_path(&path);
        Ok(url)
    }

    fn list_url(&self, max_keys: Option<u8>) -> Result<Url, SyncError> {
        let mut url = self.object_url("")?;
        {
            let mut query = url.query_pairs_mut();
            query.append_pair("list-type", "2");
            let prefix = (!self.prefix.is_empty()).then(|| format!("{}/", self.prefix));
            query.append_pair("prefix", prefix.as_deref().unwrap_or_default());
            if let Some(max_keys) = max_keys {
                query.append_pair("max-keys", &max_keys.to_string());
            }
        }
        Ok(url)
    }

    fn signed_request(
        &self,
        method: Method,
        url: Url,
        body: &[u8],
        additional_headers: BTreeMap<String, String>,
    ) -> Result<RequestBuilder, SyncError> {
        let now = OffsetDateTime::now_utc();
        let timestamp = now
            .format(&format_description!(
                "[year][month][day]T[hour][minute][second]Z"
            ))
            .map_err(|_| SyncError::RemoteUnavailable)?;
        let date = now
            .format(&format_description!("[year][month][day]"))
            .map_err(|_| SyncError::RemoteUnavailable)?;
        let payload_hash = hex::encode(Sha256::digest(body));
        let additional_header_names = additional_headers
            .keys()
            .map(|name| name.to_ascii_lowercase())
            .collect::<Vec<_>>();
        let mut headers = additional_headers
            .into_iter()
            .map(|(name, value)| (name.to_ascii_lowercase(), normalize_header_value(&value)))
            .collect::<BTreeMap<_, _>>();
        headers.insert("host".to_owned(), host_header(&url)?);
        headers.insert("x-oss-content-sha256".to_owned(), payload_hash.clone());
        headers.insert("x-oss-date".to_owned(), timestamp.clone());
        let canonical_headers = headers
            .iter()
            .map(|(name, value)| format!("{name}:{value}\n"))
            .collect::<String>();
        let signed_headers = headers.keys().cloned().collect::<Vec<_>>().join(";");
        let canonical_request = format!(
            "{}\n{}\n{}\n{}\n{}\n{}",
            method.as_str(),
            canonical_uri(&url),
            canonical_query(&url),
            canonical_headers,
            signed_headers,
            payload_hash
        );
        let scope = format!("{date}/{}/{OSS_SERVICE}/{OSS_TERMINATOR}", self.region);
        let string_to_sign = format!(
            "OSS4-HMAC-SHA256\n{timestamp}\n{scope}\n{}",
            hex::encode(Sha256::digest(canonical_request.as_bytes()))
        );
        let date_key = hmac(&format!("aliyun_v4{}", self.access_key_secret), &date)?;
        let region_key = hmac_bytes(&date_key, &self.region)?;
        let service_key = hmac_bytes(&region_key, OSS_SERVICE)?;
        let signing_key = hmac_bytes(&service_key, OSS_TERMINATOR)?;
        let signature = hex::encode(hmac_bytes(&signing_key, &string_to_sign)?);
        let authorization = format!(
            "OSS4-HMAC-SHA256 Credential={}/{scope},AdditionalHeaders={},Signature={signature}",
            self.access_key_id,
            additional_header_names.join(";")
        );

        let mut request = self.client.request(method, url).body(body.to_vec());
        for (name, value) in headers {
            request = request.header(name, value);
        }
        Ok(request.header("Authorization", authorization))
    }

    fn send(&self, request: RequestBuilder) -> Result<Response, SyncError> {
        let response = request.send().map_err(|_| SyncError::RemoteUnavailable)?;
        if response.status().is_success() {
            Ok(response)
        } else {
            Err(map_status(response.status()))
        }
    }
}

fn canonical_uri(url: &Url) -> String {
    url.path().to_owned()
}

fn canonical_query(url: &Url) -> String {
    let mut pairs = url
        .query_pairs()
        .map(|(name, value)| (percent_encode(&name), percent_encode(&value)))
        .collect::<Vec<_>>();
    pairs.sort_unstable();
    pairs
        .into_iter()
        .map(|(name, value)| format!("{name}={value}"))
        .collect::<Vec<_>>()
        .join("&")
}

fn normalize_header_value(value: &str) -> String {
    value.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn percent_encode(value: &str) -> String {
    value
        .bytes()
        .flat_map(|byte| match byte {
            b'A'..=b'Z' | b'a'..=b'z' | b'0'..=b'9' | b'-' | b'_' | b'.' | b'~' => {
                vec![byte as char]
            }
            _ => format!("%{byte:02X}").chars().collect(),
        })
        .collect()
}

impl RemoteStore for OssStore {
    fn list_completed(&self) -> Result<Vec<RemoteSegmentHeader>, SyncError> {
        let response = self.send(self.signed_request(
            Method::GET,
            self.list_url(None)?,
            &[],
            BTreeMap::new(),
        )?)?;
        let body = response.text().map_err(|_| SyncError::RemoteUnavailable)?;
        let prefix = (!self.prefix.is_empty()).then(|| format!("{}/", self.prefix));
        Ok(parse_object_keys(&body)
            .into_iter()
            .filter_map(|key| match &prefix {
                Some(prefix) => key.strip_prefix(prefix).map(str::to_owned),
                None => Some(key),
            })
            .filter_map(|name| parse_completed_object_name(&name))
            .collect())
    }

    fn get_completed(&self, header: &RemoteSegmentHeader) -> Result<Vec<u8>, SyncError> {
        let name = completed_object_name(header.header())?;
        let response = self.send(self.signed_request(
            Method::GET,
            self.object_url(&self.object_key(&name))?,
            &[],
            BTreeMap::new(),
        )?)?;
        response
            .bytes()
            .map(|bytes| bytes.to_vec())
            .map_err(|_| SyncError::RemoteUnavailable)
    }

    fn put_pending_then_publish(
        &self,
        header: &RemoteSegmentHeader,
        ciphertext: &[u8],
    ) -> Result<(), SyncError> {
        let pending_name = pending_object_name(header.header())?;
        let completed_name = completed_object_name(header.header())?;
        let pending_key = self.object_key(&pending_name);
        let completed_key = self.object_key(&completed_name);

        let mut create_headers = BTreeMap::new();
        create_headers.insert("if-none-match".to_owned(), "*".to_owned());
        self.send(self.signed_request(
            Method::PUT,
            self.object_url(&pending_key)?,
            ciphertext,
            create_headers,
        )?)?;

        let mut copy_headers = BTreeMap::new();
        copy_headers.insert("if-none-match".to_owned(), "*".to_owned());
        copy_headers.insert(
            "x-oss-copy-source".to_owned(),
            format!("/{}/{}", self.bucket, pending_key),
        );
        self.send(self.signed_request(
            Method::PUT,
            self.object_url(&completed_key)?,
            &[],
            copy_headers,
        )?)?;

        self.send(self.signed_request(
            Method::DELETE,
            self.object_url(&pending_key)?,
            &[],
            BTreeMap::new(),
        )?)?;
        Ok(())
    }

    fn probe(&self) -> Result<(), SyncError> {
        self.send(self.signed_request(
            Method::GET,
            self.list_url(Some(1))?,
            &[],
            BTreeMap::new(),
        )?)?;
        Ok(())
    }
}

fn hmac(secret: &str, message: &str) -> Result<Vec<u8>, SyncError> {
    hmac_bytes(secret.as_bytes(), message)
}

fn hmac_bytes(secret: &[u8], message: &str) -> Result<Vec<u8>, SyncError> {
    let mut mac = HmacSha256::new_from_slice(secret).map_err(|_| SyncError::RemoteUnavailable)?;
    mac.update(message.as_bytes());
    Ok(mac.finalize().into_bytes().to_vec())
}

fn host_header(url: &Url) -> Result<String, SyncError> {
    let host = url.host_str().ok_or(SyncError::RemoteUnavailable)?;
    Ok(url
        .port()
        .map_or_else(|| host.to_owned(), |port| format!("{host}:{port}")))
}

fn map_status(status: StatusCode) -> SyncError {
    match status {
        StatusCode::UNAUTHORIZED | StatusCode::FORBIDDEN => SyncError::Authentication,
        StatusCode::CONFLICT | StatusCode::PRECONDITION_FAILED => SyncError::Conflict,
        StatusCode::TOO_MANY_REQUESTS => SyncError::RateLimited,
        _ => SyncError::RemoteUnavailable,
    }
}

fn parse_object_keys(body: &str) -> Vec<String> {
    let mut reader = Reader::from_str(body);
    reader.config_mut().trim_text(true);
    let mut key_depth = 0_u8;
    let mut keys = Vec::new();
    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) if element.local_name().as_ref() == b"Key" => {
                key_depth = key_depth.saturating_add(1);
            }
            Ok(Event::End(element)) if element.local_name().as_ref() == b"Key" => {
                key_depth = key_depth.saturating_sub(1);
            }
            Ok(Event::Text(text)) if key_depth > 0 => {
                if let Ok(key) = text.unescape() {
                    keys.push(key.into_owned());
                }
            }
            Ok(Event::Eof) | Err(_) => break,
            _ => {}
        }
    }
    keys
}

/// Extracts only documented, non-sensitive OSS error codes from a bounded XML body.
pub fn parse_oss_error_code(body: &[u8]) -> Option<&'static str> {
    const MAX_ERROR_BODY_BYTES: usize = 64 * 1024;
    if body.len() > MAX_ERROR_BODY_BYTES {
        return None;
    }
    let mut reader = Reader::from_reader(body);
    reader.config_mut().trim_text(true);
    let mut in_code = false;
    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) if element.local_name().as_ref() == b"Code" => {
                in_code = true;
            }
            Ok(Event::Text(text)) if in_code => {
                let value = text.unescape().ok()?;
                return match value.as_ref() {
                    "SignatureDoesNotMatch" => Some("SignatureDoesNotMatch"),
                    "AccessDenied" => Some("AccessDenied"),
                    "NoSuchBucket" => Some("NoSuchBucket"),
                    "NoSuchKey" => Some("NoSuchKey"),
                    "InvalidAccessKeyId" => Some("InvalidAccessKeyId"),
                    _ => None,
                };
            }
            Ok(Event::End(element)) if element.local_name().as_ref() == b"Code" => {
                in_code = false;
            }
            Ok(Event::Eof) | Err(_) => return None,
            _ => {}
        }
    }
}
