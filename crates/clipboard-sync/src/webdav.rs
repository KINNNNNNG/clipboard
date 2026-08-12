use quick_xml::{Reader, events::Event};
use reqwest::{
    Method, StatusCode, Url,
    blocking::{Client, Response},
};
use std::sync::Mutex;
use uuid::Uuid;

use crate::SegmentHeader;
use crate::{
    HEADER_NAME, RemoteHeader, RemoteMetadataStore, SnapshotId, device_state_name,
    parse_device_state_name, parse_snapshot_name, snapshot_name,
};
use crate::{
    PENDING_OBJECT_SUFFIX, RemoteImageObject, RemoteSegmentHeader, RemoteStore, SyncError,
    WebDavConfig, completed_image_object_name, completed_object_name, parse_completed_object_name,
    pending_image_object_name,
};

const CSTCLOUD_WEB_DAV_HOST: &str = "data.cstcloud.cn";
const CSTCLOUD_WEB_DAV_PATH: &str = "/dav";
const CSTCLOUD_ZOTERO_USER_AGENT: &str =
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0 Zotero/7.0";
const CSTCLOUD_OBJECT_PREFIX: &str = "clipboard-sync-";

pub struct WebDavStore {
    endpoint: Url,
    client: Client,
    username: String,
    password: String,
    last_error_detail: Mutex<Option<String>>,
    last_error_operation: Mutex<Option<&'static str>>,
}

impl WebDavStore {
    pub fn new(config: WebDavConfig) -> Result<Self, SyncError> {
        let mut endpoint =
            Url::parse(config.endpoint()).map_err(|_| SyncError::RemoteUnavailable)?;
        endpoint.set_query(None);
        endpoint.set_fragment(None);
        if !endpoint.path().ends_with('/') {
            endpoint.set_path(&format!("{}/", endpoint.path()));
        }

        let mut client = Client::builder();
        if let Some(user_agent) = user_agent_for_endpoint(&endpoint) {
            client = client.user_agent(user_agent);
        }
        let client = client.build().map_err(|_| SyncError::RemoteUnavailable)?;
        Ok(Self {
            endpoint,
            client,
            username: config.username().to_owned(),
            password: config.password().to_owned(),
            last_error_detail: Mutex::new(None),
            last_error_operation: Mutex::new(None),
        })
    }

    fn root_request(&self, method: Method) -> reqwest::blocking::RequestBuilder {
        self.client
            .request(method, self.endpoint.clone())
            .basic_auth(&self.username, Some(&self.password))
    }

    fn object_url(&self, object_name: &str) -> Result<Url, SyncError> {
        self.endpoint
            .join(object_name)
            .map_err(|_| SyncError::RemoteUnavailable)
    }

    fn object_request(
        &self,
        method: Method,
        object_name: &str,
    ) -> Result<reqwest::blocking::RequestBuilder, SyncError> {
        Ok(self
            .client
            .request(method, self.object_url(object_name)?)
            .basic_auth(&self.username, Some(&self.password)))
    }

    fn send(
        &self,
        operation: &'static str,
        request: reqwest::blocking::RequestBuilder,
    ) -> Result<Response, SyncError> {
        *self
            .last_error_detail
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = None;
        *self
            .last_error_operation
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(operation);
        let response = request.send().map_err(|error| {
            let detail = if error.is_timeout() {
                "network_timeout"
            } else if error.is_connect() {
                "network_connect"
            } else {
                "network_request"
            };
            *self
                .last_error_detail
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(detail.to_owned());
            SyncError::RemoteUnavailable
        })?;
        if response.status().is_success() {
            Ok(response)
        } else {
            let status = response.status();
            *self
                .last_error_detail
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner()) =
                Some(format!("http_{}", status.as_u16()));
            Err(map_status(status))
        }
    }
}

impl RemoteStore for WebDavStore {
    fn list_completed(&self) -> Result<Vec<RemoteSegmentHeader>, SyncError> {
        let response = self.send(
            "webdav_list",
            self.root_request(
                Method::from_bytes(b"PROPFIND").map_err(|_| SyncError::RemoteUnavailable)?,
            )
            .header("Depth", "1"),
        )?;
        let body = response.text().map_err(|_| SyncError::RemoteUnavailable)?;
        Ok(parse_href_names(&body)
            .into_iter()
            .filter_map(|name| parse_completed_name_for_endpoint(&self.endpoint, &name))
            .collect())
    }

    fn get_completed(&self, header: &RemoteSegmentHeader) -> Result<Vec<u8>, SyncError> {
        let object_name = completed_name_for_endpoint(&self.endpoint, header)?;
        self.send(
            "webdav_get",
            self.object_request(Method::GET, &object_name)?,
        )?
        .bytes()
        .map(|bytes| bytes.to_vec())
        .map_err(|_| SyncError::RemoteUnavailable)
    }

    fn put_pending_then_publish(
        &self,
        header: &RemoteSegmentHeader,
        ciphertext: &[u8],
    ) -> Result<(), SyncError> {
        let pending = pending_name_for_endpoint(&self.endpoint, header)?;
        let completed = completed_name_for_endpoint(&self.endpoint, header)?;
        let mut request = self
            .object_request(Method::PUT, &pending)?
            .header("If-None-Match", "*");
        if is_cstcloud_zotero_endpoint(&self.endpoint) {
            request = request.header("Content-Type", "application/zip");
        }
        self.send("webdav_put_pending", request.body(ciphertext.to_vec()))?;
        let destination = self.object_url(&completed)?;
        self.send(
            "webdav_move_publish",
            self.object_request(
                Method::from_bytes(b"MOVE").map_err(|_| SyncError::RemoteUnavailable)?,
                &pending,
            )?
            .header("Destination", destination.as_str())
            .header("Overwrite", "F"),
        )?;
        Ok(())
    }

    fn get_image_object(&self, object: &RemoteImageObject) -> Result<Vec<u8>, SyncError> {
        self.send(
            "webdav_get_image",
            self.object_request(Method::GET, &completed_image_object_name(object))?,
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
        let pending = pending_image_object_name(object);
        let completed = completed_image_object_name(object);
        self.send(
            "webdav_put_image_pending",
            self.object_request(Method::PUT, &pending)?
                .header("If-None-Match", "*")
                .body(ciphertext.to_vec()),
        )?;
        let destination = self.object_url(&completed)?;
        self.send(
            "webdav_move_image_publish",
            self.object_request(
                Method::from_bytes(b"MOVE").map_err(|_| SyncError::RemoteUnavailable)?,
                &pending,
            )?
            .header("Destination", destination.as_str())
            .header("Overwrite", "F"),
        )?;
        Ok(())
    }

    fn probe(&self) -> Result<(), SyncError> {
        self.send(
            "webdav_probe",
            self.root_request(
                Method::from_bytes(b"PROPFIND").map_err(|_| SyncError::RemoteUnavailable)?,
            )
            .header("Depth", "0"),
        )?;
        Ok(())
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

impl WebDavStore {
    fn get_optional_metadata(
        &self,
        operation: &'static str,
        name: &str,
    ) -> Result<Option<Vec<u8>>, SyncError> {
        let request = self.object_request(Method::GET, name)?;
        let response = request.send().map_err(|error| {
            let detail = if error.is_timeout() {
                "network_timeout"
            } else if error.is_connect() {
                "network_connect"
            } else {
                "network_request"
            };
            *self
                .last_error_detail
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(detail.to_owned());
            *self
                .last_error_operation
                .lock()
                .unwrap_or_else(|poisoned| poisoned.into_inner()) = Some(operation);
            SyncError::RemoteUnavailable
        })?;
        if response.status() == StatusCode::NOT_FOUND {
            return Ok(None);
        }
        if !response.status().is_success() {
            return Err(map_status(response.status()));
        }
        response
            .bytes()
            .map(|bytes| Some(bytes.to_vec()))
            .map_err(|_| SyncError::RemoteUnavailable)
    }
}

impl RemoteMetadataStore for WebDavStore {
    fn get_header(&self) -> Result<Option<RemoteHeader>, SyncError> {
        let Some(bytes) = self.get_optional_metadata("webdav_get_header", HEADER_NAME)? else {
            return Ok(None);
        };
        let header: RemoteHeader =
            serde_json::from_slice(&bytes).map_err(|_| SyncError::InvalidSegment)?;
        header.validate()?;
        Ok(Some(header))
    }

    fn put_header(&self, header: &RemoteHeader) -> Result<(), SyncError> {
        header.validate()?;
        let bytes = serde_json::to_vec(header).map_err(|_| SyncError::InvalidSegment)?;
        self.send(
            "webdav_put_header",
            self.object_request(Method::PUT, HEADER_NAME)?
                .header("If-None-Match", "*")
                .body(bytes),
        )?;
        Ok(())
    }

    fn list_device_states(&self) -> Result<Vec<Uuid>, SyncError> {
        let response = self.send(
            "webdav_list_device_states",
            self.root_request(
                Method::from_bytes(b"PROPFIND").map_err(|_| SyncError::RemoteUnavailable)?,
            )
            .header("Depth", "1"),
        )?;
        let mut devices =
            parse_href_names(&response.text().map_err(|_| SyncError::RemoteUnavailable)?)
                .into_iter()
                .filter_map(|name| parse_device_state_name(&name))
                .collect::<Vec<_>>();
        devices.sort_unstable();
        Ok(devices)
    }

    fn get_device_state(&self, device_id: Uuid) -> Result<Option<Vec<u8>>, SyncError> {
        self.get_optional_metadata("webdav_get_device_state", &device_state_name(device_id))
    }

    fn put_device_state(&self, device_id: Uuid, ciphertext: &[u8]) -> Result<(), SyncError> {
        self.send(
            "webdav_put_device_state",
            self.object_request(Method::PUT, &device_state_name(device_id))?
                .body(ciphertext.to_vec()),
        )?;
        Ok(())
    }

    fn get_snapshot(&self, snapshot_id: SnapshotId) -> Result<Option<Vec<u8>>, SyncError> {
        self.get_optional_metadata("webdav_get_snapshot", &snapshot_name(snapshot_id))
    }

    fn put_snapshot(&self, snapshot_id: SnapshotId, ciphertext: &[u8]) -> Result<(), SyncError> {
        self.send(
            "webdav_put_snapshot",
            self.object_request(Method::PUT, &snapshot_name(snapshot_id))?
                .header("If-None-Match", "*")
                .body(ciphertext.to_vec()),
        )?;
        Ok(())
    }

    fn list_snapshots(&self) -> Result<Vec<SnapshotId>, SyncError> {
        let response = self.send(
            "webdav_list_snapshots",
            self.root_request(
                Method::from_bytes(b"PROPFIND").map_err(|_| SyncError::RemoteUnavailable)?,
            )
            .header("Depth", "1"),
        )?;
        let mut snapshots =
            parse_href_names(&response.text().map_err(|_| SyncError::RemoteUnavailable)?)
                .into_iter()
                .filter_map(|name| parse_snapshot_name(&name))
                .collect::<Vec<_>>();
        snapshots.sort_unstable();
        Ok(snapshots)
    }

    fn delete_segment(&self, header: &SegmentHeader) -> Result<bool, SyncError> {
        let name =
            completed_name_for_endpoint(&self.endpoint, &RemoteSegmentHeader::try_from(*header)?)?;
        let response = self
            .object_request(Method::DELETE, &name)?
            .send()
            .map_err(|_| SyncError::RemoteUnavailable)?;
        if response.status() == StatusCode::NOT_FOUND {
            Ok(false)
        } else if response.status().is_success() {
            Ok(true)
        } else {
            Err(map_status(response.status()))
        }
    }
}

fn map_status(status: StatusCode) -> SyncError {
    match status {
        StatusCode::UNAUTHORIZED | StatusCode::FORBIDDEN => SyncError::Authentication,
        StatusCode::CONFLICT | StatusCode::PRECONDITION_FAILED => SyncError::Conflict,
        StatusCode::TOO_MANY_REQUESTS => SyncError::RateLimited,
        _ => SyncError::RemoteUnavailable,
    }
}

fn user_agent_for_endpoint(endpoint: &Url) -> Option<&'static str> {
    is_cstcloud_zotero_endpoint(endpoint).then_some(CSTCLOUD_ZOTERO_USER_AGENT)
}

fn is_cstcloud_zotero_endpoint(endpoint: &Url) -> bool {
    endpoint.host_str() == Some(CSTCLOUD_WEB_DAV_HOST)
        && endpoint.path().trim_end_matches('/') == CSTCLOUD_WEB_DAV_PATH
}

fn completed_name_for_endpoint(
    endpoint: &Url,
    header: &RemoteSegmentHeader,
) -> Result<String, SyncError> {
    let completed = completed_object_name(header.header())?;
    if !is_cstcloud_zotero_endpoint(endpoint) {
        return Ok(completed);
    }

    let stem = completed
        .strip_suffix(".enc")
        .ok_or(SyncError::RemoteUnavailable)?;
    Ok(format!("{CSTCLOUD_OBJECT_PREFIX}{stem}.zip"))
}

fn pending_name_for_endpoint(
    endpoint: &Url,
    header: &RemoteSegmentHeader,
) -> Result<String, SyncError> {
    let completed = completed_name_for_endpoint(endpoint, header)?;
    if is_cstcloud_zotero_endpoint(endpoint) {
        let stem = completed
            .strip_suffix(".zip")
            .ok_or(SyncError::RemoteUnavailable)?;
        return Ok(format!("{stem}.pending.zip"));
    }

    Ok(format!("{completed}{PENDING_OBJECT_SUFFIX}"))
}

fn parse_completed_name_for_endpoint(endpoint: &Url, name: &str) -> Option<RemoteSegmentHeader> {
    if !is_cstcloud_zotero_endpoint(endpoint) {
        return parse_completed_object_name(name);
    }

    let stem = name
        .strip_prefix(CSTCLOUD_OBJECT_PREFIX)?
        .strip_suffix(".zip")?;
    parse_completed_object_name(&format!("{stem}.enc"))
}

fn parse_href_names(body: &str) -> Vec<String> {
    let mut reader = Reader::from_str(body);
    reader.config_mut().trim_text(true);
    let mut href_depth = 0_u8;
    let mut names = Vec::new();

    loop {
        match reader.read_event() {
            Ok(Event::Start(element)) if element.local_name().as_ref() == b"href" => {
                href_depth = href_depth.saturating_add(1);
            }
            Ok(Event::End(element)) if element.local_name().as_ref() == b"href" => {
                href_depth = href_depth.saturating_sub(1);
            }
            Ok(Event::Text(text)) if href_depth > 0 => {
                if let Ok(href) = text.unescape() {
                    let path = Url::parse(&href)
                        .ok()
                        .map(|url| url.path().to_owned())
                        .unwrap_or_else(|| href.into_owned());
                    if let Some(name) = path.rsplit('/').find(|part| !part.is_empty()) {
                        names.push(name.to_owned());
                    }
                }
            }
            Ok(Event::Eof) | Err(_) => break,
            _ => {}
        }
    }

    names
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::SegmentHeader;
    use uuid::Uuid;

    #[test]
    fn cstcloud_zotero_endpoint_uses_the_required_user_agent() {
        let endpoint = Url::parse("https://data.cstcloud.cn/dav/").unwrap();
        let unrelated = Url::parse("https://webdav.example.test/root/").unwrap();

        assert_eq!(
            user_agent_for_endpoint(&endpoint),
            Some(CSTCLOUD_ZOTERO_USER_AGENT)
        );
        assert_eq!(user_agent_for_endpoint(&unrelated), None);
    }

    #[test]
    fn cstcloud_zotero_endpoint_maps_segments_to_an_isolated_zip_namespace() {
        let endpoint = Url::parse("https://data.cstcloud.cn/dav/").unwrap();
        let header = RemoteSegmentHeader::try_from(SegmentHeader {
            protocol_version: 1,
            vault_id: Uuid::from_u128(1),
            device_id: Uuid::from_u128(2),
            segment_id: Uuid::from_u128(3),
        })
        .unwrap();
        let ordinary_name = completed_object_name(header.header()).unwrap();
        let stem = ordinary_name.strip_suffix(".enc").unwrap();
        let completed = completed_name_for_endpoint(&endpoint, &header).unwrap();

        assert_eq!(completed, format!("clipboard-sync-{stem}.zip"));
        assert_eq!(
            pending_name_for_endpoint(&endpoint, &header).unwrap(),
            format!("clipboard-sync-{stem}.pending.zip")
        );
        assert_eq!(
            parse_completed_name_for_endpoint(&endpoint, &completed),
            Some(header)
        );
    }
}
