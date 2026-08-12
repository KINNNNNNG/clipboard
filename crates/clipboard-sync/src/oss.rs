use std::{collections::BTreeMap, io::Read, sync::Mutex};

use hmac::{Hmac, Mac};
use quick_xml::{Reader, events::Event};
use reqwest::{
    Method, StatusCode, Url,
    blocking::{Client, RequestBuilder, Response},
};
use sha2::{Digest, Sha256};
use time::{OffsetDateTime, macros::format_description};

use crate::{
    OssConfig, RemoteImageObject, RemoteSegmentHeader, RemoteStore, SyncError,
    completed_image_object_name, completed_object_name, parse_completed_object_name,
    pending_image_object_name, pending_object_name,
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
    last_error_code: Mutex<Option<&'static str>>,
    last_error_detail: Mutex<Option<String>>,
    last_error_operation: Mutex<Option<&'static str>>,
    last_string_to_sign: Mutex<Option<Vec<u8>>>,
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
            last_error_code: Mutex::new(None),
            last_error_detail: Mutex::new(None),
            last_error_operation: Mutex::new(None),
            last_string_to_sign: Mutex::new(None),
        })
    }

    fn object_key(&self, object_name: &str) -> String {
        if self.prefix.is_empty() {
            object_name.to_owned()
        } else {
            format!("{}/{}", self.prefix, object_name)
        }
    }

    fn canonical_uri(&self, url: &Url) -> String {
        let path = url.path();
        if self.path_style {
            path.to_owned()
        } else {
            format!("/{}{}", self.bucket, path)
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
        let additional_header_names = additional_headers
            .keys()
            .map(|name| name.to_ascii_lowercase())
            .filter(|name| !is_default_signed_header(name))
            .collect::<Vec<_>>();
        let mut headers = additional_headers
            .into_iter()
            .map(|(name, value)| (name.to_ascii_lowercase(), normalize_header_value(&value)))
            .collect::<BTreeMap<_, _>>();
        // OSS V4 uses this literal for regular API requests; it does not hash the payload.
        let payload_hash = "UNSIGNED-PAYLOAD".to_owned();
        headers.insert("x-oss-content-sha256".to_owned(), payload_hash.clone());
        headers.insert("x-oss-date".to_owned(), timestamp.clone());
        let canonical_headers = headers
            .iter()
            .map(|(name, value)| format!("{name}:{value}\n"))
            .collect::<String>();
        let canonical_request = format!(
            "{}\n{}\n{}\n{}\n{}\n{}",
            method.as_str(),
            self.canonical_uri(&url),
            canonical_query(&url),
            canonical_headers,
            additional_header_names.join(";"),
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
        let authorization = if additional_header_names.is_empty() {
            format!(
                "OSS4-HMAC-SHA256 Credential={}/{scope},Signature={signature}",
                self.access_key_id
            )
        } else {
            format!(
                "OSS4-HMAC-SHA256 Credential={}/{scope},AdditionalHeaders={},Signature={signature}",
                self.access_key_id,
                additional_header_names.join(";")
            )
        };

        let mut request = self.client.request(method, url).body(body.to_vec());
        for (name, value) in headers {
            request = request.header(name, value);
        }
        *self
            .last_string_to_sign
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(string_to_sign.into_bytes());
        Ok(request.header("Authorization", authorization))
    }

    fn send(
        &self,
        operation: &'static str,
        request: RequestBuilder,
    ) -> Result<Response, SyncError> {
        self.clear_last_error();
        *self
            .last_error_operation
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(operation);
        let response = request.send().map_err(|error| {
            self.set_last_error_detail(network_error_detail(&error));
            SyncError::RemoteUnavailable
        })?;
        if response.status().is_success() {
            Ok(response)
        } else {
            let status = response.status();
            let mut body = Vec::new();
            let _ = response.take(64 * 1024).read_to_end(&mut body);
            let code = parse_oss_error_code(&body);
            *self
                .last_error_code
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner()) = code;
            let response_kind = if is_oss_error_body(&body) {
                "oss_error"
            } else {
                "non_oss"
            };
            self.set_last_error_detail(format!("http_{}_{}", status.as_u16(), response_kind));
            if code == Some("SignatureDoesNotMatch") {
                self.record_signature_diagnostic(&body);
            }
            Err(map_status(status))
        }
    }

    fn clear_last_error(&self) {
        *self
            .last_error_code
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = None;
        *self
            .last_error_detail
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = None;
    }

    fn set_last_error_detail(&self, detail: impl Into<String>) {
        *self
            .last_error_detail
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(detail.into());
    }

    fn record_signature_diagnostic(&self, body: &[u8]) {
        let Some(server_string_to_sign) = parse_oss_decimal_bytes(body, b"StringToSignBytes")
        else {
            return;
        };
        let expected = self
            .last_string_to_sign
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone();
        let detail = if expected.as_deref() == Some(server_string_to_sign.as_slice()) {
            "signature_key_mismatch"
        } else {
            "signature_request_mismatch"
        };
        self.set_last_error_detail(detail);
    }
}

fn canonical_query(url: &Url) -> String {
    let mut pairs = url
        .query_pairs()
        .map(|(name, value)| (percent_encode(&name), percent_encode(&value)))
        .collect::<Vec<_>>();
    pairs.sort_unstable();
    pairs
        .into_iter()
        .map(|(name, value)| {
            if value.is_empty() {
                name
            } else {
                format!("{name}={value}")
            }
        })
        .collect::<Vec<_>>()
        .join("&")
}

fn normalize_header_value(value: &str) -> String {
    value.split_whitespace().collect::<Vec<_>>().join(" ")
}

fn is_default_signed_header(name: &str) -> bool {
    name == "content-type" || name == "content-md5" || name.starts_with("x-oss-")
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
        let response = self.send(
            "oss_list",
            self.signed_request(Method::GET, self.list_url(None)?, &[], BTreeMap::new())?,
        )?;
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
        let response = self.send(
            "oss_get",
            self.signed_request(
                Method::GET,
                self.object_url(&self.object_key(&name))?,
                &[],
                BTreeMap::new(),
            )?,
        )?;
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
        create_headers.insert("x-oss-forbid-overwrite".to_owned(), "true".to_owned());
        self.send(
            "oss_put_pending",
            self.signed_request(
                Method::PUT,
                self.object_url(&pending_key)?,
                ciphertext,
                create_headers,
            )?,
        )?;

        let mut copy_headers = BTreeMap::new();
        copy_headers.insert("x-oss-forbid-overwrite".to_owned(), "true".to_owned());
        copy_headers.insert(
            "x-oss-copy-source".to_owned(),
            format!("/{}/{}", self.bucket, pending_key),
        );
        self.send(
            "oss_copy_publish",
            self.signed_request(
                Method::PUT,
                self.object_url(&completed_key)?,
                &[],
                copy_headers,
            )?,
        )?;

        self.send(
            "oss_delete_pending",
            self.signed_request(
                Method::DELETE,
                self.object_url(&pending_key)?,
                &[],
                BTreeMap::new(),
            )?,
        )?;
        Ok(())
    }

    fn get_image_object(&self, object: &RemoteImageObject) -> Result<Vec<u8>, SyncError> {
        let name = completed_image_object_name(object);
        self.send(
            "oss_get_image",
            self.signed_request(
                Method::GET,
                self.object_url(&self.object_key(&name))?,
                &[],
                BTreeMap::new(),
            )?,
        )?
        .bytes()
        .map(|bytes| bytes.to_vec())
        .map_err(|_| SyncError::RemoteUnavailable)
    }

    fn put_image_object(
        &self,
        object: &RemoteImageObject,
        ciphertext: &[u8],
    ) -> Result<(), SyncError> {
        let pending_name = pending_image_object_name(object);
        let completed_name = completed_image_object_name(object);
        let pending_key = self.object_key(&pending_name);
        let completed_key = self.object_key(&completed_name);

        let mut create_headers = BTreeMap::new();
        create_headers.insert("x-oss-forbid-overwrite".to_owned(), "true".to_owned());
        self.send(
            "oss_put_image_pending",
            self.signed_request(
                Method::PUT,
                self.object_url(&pending_key)?,
                ciphertext,
                create_headers,
            )?,
        )?;

        let mut copy_headers = BTreeMap::new();
        copy_headers.insert("x-oss-forbid-overwrite".to_owned(), "true".to_owned());
        copy_headers.insert(
            "x-oss-copy-source".to_owned(),
            format!("/{}/{}", self.bucket, pending_key),
        );
        self.send(
            "oss_copy_image_publish",
            self.signed_request(
                Method::PUT,
                self.object_url(&completed_key)?,
                &[],
                copy_headers,
            )?,
        )?;
        self.send(
            "oss_delete_image_pending",
            self.signed_request(
                Method::DELETE,
                self.object_url(&pending_key)?,
                &[],
                BTreeMap::new(),
            )?,
        )?;
        Ok(())
    }

    fn probe(&self) -> Result<(), SyncError> {
        self.send(
            "oss_list",
            self.signed_request(Method::GET, self.list_url(Some(1))?, &[], BTreeMap::new())?,
        )?;
        Ok(())
    }

    fn last_error_code(&self) -> Option<String> {
        self.last_error_code
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .map(str::to_owned)
    }

    fn last_error_detail(&self) -> Option<String> {
        self.last_error_detail
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .clone()
    }

    fn last_error_operation(&self) -> Option<String> {
        self.last_error_operation
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner())
            .map(str::to_owned)
    }
}

fn network_error_detail(error: &reqwest::Error) -> &'static str {
    if error.is_timeout() {
        "network_timeout"
    } else if error.is_connect() {
        "network_connect"
    } else {
        "network_request"
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

fn is_oss_error_body(body: &[u8]) -> bool {
    let mut reader = Reader::from_reader(body);
    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) => return element.local_name().as_ref() == b"Error",
            Ok(Event::Eof) | Err(_) => return false,
            _ => {}
        }
    }
}

fn parse_oss_decimal_bytes(body: &[u8], element_name: &[u8]) -> Option<Vec<u8>> {
    let mut reader = Reader::from_reader(body);
    let mut inside = false;
    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) if element.local_name().as_ref() == element_name => {
                inside = true;
            }
            Ok(Event::Text(text)) if inside => {
                let value = text.unescape().ok()?;
                return value
                    .split_whitespace()
                    .map(str::parse::<u8>)
                    .collect::<Result<Vec<_>, _>>()
                    .ok();
            }
            Ok(Event::End(element)) if element.local_name().as_ref() == element_name => {
                inside = false;
            }
            Ok(Event::Eof) | Err(_) => return None,
            _ => {}
        }
    }
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
                    "InvalidRequest" => Some("InvalidRequest"),
                    "AuthorizationHeaderMalformed" => Some("AuthorizationHeaderMalformed"),
                    "InvalidArgument" => Some("InvalidArgument"),
                    "InvalidBucketName" => Some("InvalidBucketName"),
                    "InvalidObjectName" => Some("InvalidObjectName"),
                    "InvalidURI" => Some("InvalidURI"),
                    "InvalidSecurityToken" => Some("InvalidSecurityToken"),
                    "RequestTimeTooSkewed" => Some("RequestTimeTooSkewed"),
                    "MalformedXML" => Some("MalformedXML"),
                    "MissingArgument" => Some("MissingArgument"),
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn v4_virtual_host_canonical_uri_includes_the_bucket() {
        let store = OssStore::new(OssConfig::new(
            "https://bucket.oss-cn-hangzhou.aliyuncs.com",
            "cn-hangzhou",
            "bucket",
            "",
            "AKIDEXAMPLE",
            "secret",
        ))
        .unwrap();
        let url = store.list_url(Some(1)).unwrap();

        assert_eq!(store.canonical_uri(&url), "/bucket/");
    }
}
