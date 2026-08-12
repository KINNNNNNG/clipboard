use std::{
    collections::BTreeMap,
    io::{Read, Write},
    net::{TcpListener, TcpStream},
    sync::{Arc, Mutex},
    thread::{self, JoinHandle},
};

use clipboard_sync::{
    RemoteSegmentHeader, RemoteStore, SegmentHeader, SyncError, WebDavConfig, WebDavStore,
    completed_object_name, pending_object_name,
};
use uuid::Uuid;

#[derive(Debug)]
struct RecordedRequest {
    method: String,
    path: String,
    headers: BTreeMap<String, String>,
    body: Vec<u8>,
}

struct WebDavFixture {
    base_url: String,
    requests: Arc<Mutex<Vec<RecordedRequest>>>,
    worker: Option<JoinHandle<()>>,
}

impl WebDavFixture {
    fn start(responses: Vec<(u16, String)>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let base_url = format!("http://{}/sync/", listener.local_addr().unwrap());
        let requests = Arc::new(Mutex::new(Vec::new()));
        let worker_requests = Arc::clone(&requests);
        let worker = thread::spawn(move || {
            for (status, body) in responses {
                let (stream, _) = listener.accept().unwrap();
                worker_requests
                    .lock()
                    .unwrap()
                    .push(read_request(stream, status, &body));
            }
        });
        Self {
            base_url,
            requests,
            worker: Some(worker),
        }
    }

    fn store(&self) -> WebDavStore {
        WebDavStore::new(WebDavConfig::new(&self.base_url, "alice", "secret")).unwrap()
    }

    fn finish(mut self) -> Vec<RecordedRequest> {
        self.worker.take().unwrap().join().unwrap();
        Arc::try_unwrap(self.requests)
            .unwrap()
            .into_inner()
            .unwrap()
    }
}

fn read_request(mut stream: TcpStream, status: u16, body: &str) -> RecordedRequest {
    let mut bytes = Vec::new();
    let mut buffer = [0_u8; 1024];
    let header_end = loop {
        let count = stream.read(&mut buffer).unwrap();
        bytes.extend_from_slice(&buffer[..count]);
        if let Some(end) = bytes.windows(4).position(|window| window == b"\r\n\r\n") {
            break end + 4;
        }
    };
    let header_text = std::str::from_utf8(&bytes[..header_end]).unwrap();
    let mut lines = header_text.split("\r\n");
    let mut request_line = lines.next().unwrap().split_whitespace();
    let method = request_line.next().unwrap().to_owned();
    let path = request_line.next().unwrap().to_owned();
    let headers = lines
        .filter_map(|line| line.split_once(':'))
        .map(|(name, value)| (name.to_ascii_lowercase(), value.trim().to_owned()))
        .collect::<BTreeMap<_, _>>();
    let expected_body = headers
        .get("content-length")
        .and_then(|value| value.parse::<usize>().ok())
        .unwrap_or(0);
    while bytes.len() - header_end < expected_body {
        let count = stream.read(&mut buffer).unwrap();
        bytes.extend_from_slice(&buffer[..count]);
    }
    let response = format!(
        "HTTP/1.1 {status} Test\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
        body.len()
    );
    stream.write_all(response.as_bytes()).unwrap();
    stream.flush().unwrap();
    RecordedRequest {
        method,
        path,
        headers,
        body: bytes[header_end..header_end + expected_body].to_vec(),
    }
}

fn header() -> RemoteSegmentHeader {
    RemoteSegmentHeader::try_from(SegmentHeader {
        protocol_version: 1,
        vault_id: Uuid::from_u128(1),
        device_id: Uuid::from_u128(2),
        segment_id: Uuid::from_u128(3),
    })
    .unwrap()
}

#[test]
fn webdav_lists_and_gets_only_completed_segments_with_basic_authentication() {
    let completed = completed_object_name(header().header()).unwrap();
    let fixture = WebDavFixture::start(vec![
        (
            207,
            format!(
                "<d:multistatus xmlns:d=\"DAV:\"><d:response><d:href>/sync/</d:href></d:response><d:response><d:href>/sync/{completed}</d:href></d:response><d:response><d:href>/sync/{completed}.pending</d:href></d:response></d:multistatus>"
            ),
        ),
        (200, "ciphertext".to_owned()),
    ]);

    let remote = fixture.store();
    assert_eq!(remote.list_completed().unwrap(), vec![header()]);
    assert_eq!(remote.get_completed(&header()).unwrap(), b"ciphertext");

    let requests = fixture.finish();
    assert_eq!(requests[0].method, "PROPFIND");
    assert_eq!(requests[0].path, "/sync/");
    assert_eq!(requests[0].headers["depth"], "1");
    assert_eq!(
        requests[0].headers["authorization"],
        "Basic YWxpY2U6c2VjcmV0"
    );
    assert_eq!(requests[1].method, "GET");
    assert_eq!(requests[1].path, format!("/sync/{completed}"));
    assert_eq!(
        requests[1].headers["authorization"],
        "Basic YWxpY2U6c2VjcmV0"
    );
}

#[test]
fn webdav_writes_pending_before_non_overwriting_move() {
    let completed = completed_object_name(header().header()).unwrap();
    let pending = pending_object_name(header().header()).unwrap();
    let fixture = WebDavFixture::start(vec![(201, String::new()), (201, String::new())]);

    fixture
        .store()
        .put_pending_then_publish(&header(), b"ciphertext")
        .unwrap();

    let expected_destination = format!("{}{}", fixture.base_url, completed);
    let requests = fixture.finish();
    assert_eq!(requests[0].method, "PUT");
    assert_eq!(requests[0].path, format!("/sync/{pending}"));
    assert_eq!(requests[0].headers["if-none-match"], "*");
    assert_eq!(requests[0].body, b"ciphertext");
    assert_eq!(requests[1].method, "MOVE");
    assert_eq!(requests[1].path, format!("/sync/{pending}"));
    assert_eq!(requests[1].headers["destination"], expected_destination);
    assert_eq!(requests[1].headers["overwrite"], "F");
}

#[test]
fn webdav_maps_authentication_conflict_rate_limit_and_network_errors_without_details() {
    for (status, expected) in [
        (401, SyncError::Authentication),
        (409, SyncError::Conflict),
        (429, SyncError::RateLimited),
        (500, SyncError::RemoteUnavailable),
    ] {
        let fixture = WebDavFixture::start(vec![(status, "must not surface".to_owned())]);
        let error = fixture.store().probe().unwrap_err();
        assert_eq!(error, expected);
        assert!(!error.to_string().contains("must not surface"));
        let requests = fixture.finish();
        assert_eq!(requests[0].method, "PROPFIND");
        assert_eq!(requests[0].headers["depth"], "0");
    }
}

#[test]
fn webdav_records_a_sanitized_failure_status_and_operation() {
    let fixture = WebDavFixture::start(vec![(401, "must not surface".to_owned())]);
    let store = fixture.store();

    assert_eq!(store.probe(), Err(SyncError::Authentication));
    assert_eq!(store.last_error_detail(), Some("http_401".to_owned()));
    assert_eq!(
        store.last_error_operation(),
        Some("webdav_probe".to_owned())
    );

    fixture.finish();
}
