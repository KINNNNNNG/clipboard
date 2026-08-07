use quick_xml::{Reader, events::Event};
use reqwest::{
    Method, StatusCode, Url,
    blocking::{Client, Response},
};

use crate::{
    RemoteSegmentHeader, RemoteStore, SyncError, WebDavConfig, completed_object_name,
    parse_completed_object_name, pending_object_name,
};

pub struct WebDavStore {
    endpoint: Url,
    client: Client,
    username: String,
    password: String,
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

        let client = Client::builder()
            .build()
            .map_err(|_| SyncError::RemoteUnavailable)?;
        Ok(Self {
            endpoint,
            client,
            username: config.username().to_owned(),
            password: config.password().to_owned(),
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

    fn send(&self, request: reqwest::blocking::RequestBuilder) -> Result<Response, SyncError> {
        let response = request.send().map_err(|_| SyncError::RemoteUnavailable)?;
        if response.status().is_success() {
            Ok(response)
        } else {
            Err(map_status(response.status()))
        }
    }
}

impl RemoteStore for WebDavStore {
    fn list_completed(&self) -> Result<Vec<RemoteSegmentHeader>, SyncError> {
        let response = self.send(
            self.root_request(
                Method::from_bytes(b"PROPFIND").map_err(|_| SyncError::RemoteUnavailable)?,
            )
            .header("Depth", "1"),
        )?;
        let body = response.text().map_err(|_| SyncError::RemoteUnavailable)?;
        Ok(parse_href_names(&body)
            .into_iter()
            .filter_map(|name| parse_completed_object_name(&name))
            .collect())
    }

    fn get_completed(&self, header: &RemoteSegmentHeader) -> Result<Vec<u8>, SyncError> {
        let object_name = completed_object_name(header.header())?;
        self.send(self.object_request(Method::GET, &object_name)?)?
            .bytes()
            .map(|bytes| bytes.to_vec())
            .map_err(|_| SyncError::RemoteUnavailable)
    }

    fn put_pending_then_publish(
        &self,
        header: &RemoteSegmentHeader,
        ciphertext: &[u8],
    ) -> Result<(), SyncError> {
        let pending = pending_object_name(header.header())?;
        let completed = completed_object_name(header.header())?;
        self.send(
            self.object_request(Method::PUT, &pending)?
                .header("If-None-Match", "*")
                .body(ciphertext.to_vec()),
        )?;
        let destination = self.object_url(&completed)?;
        self.send(
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
            self.root_request(
                Method::from_bytes(b"PROPFIND").map_err(|_| SyncError::RemoteUnavailable)?,
            )
            .header("Depth", "0"),
        )?;
        Ok(())
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
