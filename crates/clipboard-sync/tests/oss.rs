use std::{
    collections::BTreeMap,
    io::{Read, Write},
    net::{TcpListener, TcpStream},
    sync::{Arc, Mutex},
    thread::{self, JoinHandle},
};

use clipboard_sync::{
    OssConfig, OssStore, RemoteSegmentHeader, RemoteStore, SegmentHeader, SyncError,
    completed_object_name, pending_object_name,
};
use uuid::Uuid;

#[derive(Debug)]
struct RecordedRequest {
    method: String,
    path_and_query: String,
    headers: BTreeMap<String, String>,
    body: Vec<u8>,
}

struct OssFixture {
    endpoint: String,
    requests: Arc<Mutex<Vec<RecordedRequest>>>,
    worker: Option<JoinHandle<()>>,
}

impl OssFixture {
    fn start(responses: Vec<(u16, String)>) -> Self {
        let listener = TcpListener::bind("127.0.0.1:0").unwrap();
        let endpoint = format!("http://{}", listener.local_addr().unwrap());
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
            endpoint,
            requests,
            worker: Some(worker),
        }
    }

    fn store(&self) -> OssStore {
        OssStore::new(OssConfig::new(
            &self.endpoint,
            "cn-hangzhou",
            "bucket",
            "encrypted/segments",
            "AKIDEXAMPLE",
            "secret",
        ))
        .unwrap()
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
    let text = std::str::from_utf8(&bytes[..header_end]).unwrap();
    let mut lines = text.split("\r\n");
    let mut request_line = lines.next().unwrap().split_whitespace();
    let method = request_line.next().unwrap().to_owned();
    let path_and_query = request_line.next().unwrap().to_owned();
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
        path_and_query,
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
fn oss_lists_gets_and_signs_requests_without_exposing_credentials() {
    let completed = completed_object_name(header().header()).unwrap();
    let fixture = OssFixture::start(vec![
        (
            200,
            format!(
                "<ListBucketResult><Contents><Key>encrypted/segments/{completed}</Key></Contents><Contents><Key>encrypted/segments/{completed}.pending</Key></Contents></ListBucketResult>"
            ),
        ),
        (200, "ciphertext".to_owned()),
    ]);

    let remote = fixture.store();
    assert_eq!(remote.list_completed().unwrap(), vec![header()]);
    assert_eq!(remote.get_completed(&header()).unwrap(), b"ciphertext");

    let requests = fixture.finish();
    assert!(
        requests[0]
            .path_and_query
            .starts_with("/bucket/?list-type=2&prefix=encrypted%2Fsegments%2F")
    );
    assert_eq!(
        requests[1].path_and_query,
        format!("/bucket/encrypted/segments/{completed}")
    );
    for request in requests {
        let authorization = &request.headers["authorization"];
        assert!(authorization.starts_with("OSS4-HMAC-SHA256 "));
        assert!(!authorization.contains("secret"));
        assert!(!authorization.contains("bucket"));
        assert!(request.headers.contains_key("x-oss-date"));
        assert!(request.headers.contains_key("x-oss-content-sha256"));
    }
}

#[test]
fn oss_publishes_by_copying_completed_object_then_deleting_pending() {
    let completed = completed_object_name(header().header()).unwrap();
    let pending = pending_object_name(header().header()).unwrap();
    let fixture = OssFixture::start(vec![
        (200, String::new()),
        (200, String::new()),
        (204, String::new()),
    ]);

    fixture
        .store()
        .put_pending_then_publish(&header(), b"ciphertext")
        .unwrap();

    let requests = fixture.finish();
    assert_eq!(requests[0].method, "PUT");
    assert_eq!(
        requests[0].path_and_query,
        format!("/bucket/encrypted/segments/{pending}")
    );
    assert_eq!(requests[0].headers["if-none-match"], "*");
    assert_eq!(requests[0].body, b"ciphertext");
    assert_eq!(requests[1].method, "PUT");
    assert_eq!(
        requests[1].path_and_query,
        format!("/bucket/encrypted/segments/{completed}")
    );
    assert_eq!(
        requests[1].headers["x-oss-copy-source"],
        format!("/bucket/encrypted/segments/{pending}")
    );
    assert_eq!(requests[2].method, "DELETE");
    assert_eq!(
        requests[2].path_and_query,
        format!("/bucket/encrypted/segments/{pending}")
    );
}

#[test]
fn oss_maps_sensitive_remote_failures_to_fixed_categories() {
    for (status, expected) in [
        (403, SyncError::Authentication),
        (412, SyncError::Conflict),
        (429, SyncError::RateLimited),
        (503, SyncError::RemoteUnavailable),
    ] {
        let fixture = OssFixture::start(vec![(status, "must not surface".to_owned())]);
        let error = fixture.store().probe().unwrap_err();
        assert_eq!(error, expected);
        assert!(!error.to_string().contains("must not surface"));
        fixture.finish();
    }
}
