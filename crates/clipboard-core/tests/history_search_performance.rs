use clipboard_core::{CoreCommand, CoreService, SearchFilters, SearchRequest};
use clipboard_search::SearchMode;
use serde_json::json;
use std::{
    path::{Path, PathBuf},
    process::Command,
    time::{Duration, Instant},
};
use tempfile::tempdir;
use uuid::Uuid;

const FIXTURE_VAULT_ID: Uuid = Uuid::from_u128(0x5a17);
const FIXTURE_KEY: [u8; 32] = [0x6a; 32];
const FIXTURE_ITEM_COUNT: usize = 10_000;
const PERFORMANCE_SAMPLES: usize = 30;
const EMPTY_QUERY_SAMPLES: usize = 3;
const P95_THRESHOLD: Duration = Duration::from_millis(200);
const FIXTURE_BINARY: Option<&str> = option_env!("CARGO_BIN_EXE_prepare_history_performance");

#[test]
#[ignore = "performance gate; run explicitly with --ignored"]
fn history_search_p95_stays_within_threshold() {
    let directory = tempdir().expect("create temporary history directory");
    assert_fixture_command_rejects_invalid_arguments();
    prepare_fixture(directory.path());

    measure_scenario(
        directory.path(),
        "substring",
        PERFORMANCE_SAMPLES,
        100,
        Some(P95_THRESHOLD),
        || SearchRequest {
            pattern: "performance-needle".into(),
            mode: SearchMode::Substring,
            filters: SearchFilters::default(),
        },
    );
    measure_scenario(
        directory.path(),
        "combined_filters",
        PERFORMANCE_SAMPLES,
        10,
        Some(P95_THRESHOLD),
        || SearchRequest {
            pattern: "performance-needle".into(),
            mode: SearchMode::Substring,
            filters: SearchFilters {
                created_after_ms: Some(0),
                created_before_ms: Some(900),
                source_apps: vec!["benchmark-source-00.exe".into()],
                kinds: vec!["text".into()],
            },
        },
    );
    measure_scenario(
        directory.path(),
        "empty_query",
        EMPTY_QUERY_SAMPLES,
        FIXTURE_ITEM_COUNT,
        None,
        || SearchRequest {
            pattern: String::new(),
            mode: SearchMode::Substring,
            filters: SearchFilters::default(),
        },
    );
}

fn assert_fixture_command_rejects_invalid_arguments() {
    let binary = fixture_binary();
    for arguments in [
        Vec::<String>::new(),
        vec!["--unknown".into()],
        vec!["--data-dir".into()],
        vec!["--data-dir".into(), "fixture".into(), "--unknown".into()],
    ] {
        let output = Command::new(&binary)
            .args(arguments)
            .output()
            .expect("run fixture command with invalid arguments");
        assert!(
            !output.status.success(),
            "fixture command must reject invalid arguments"
        );
        assert!(
            output.stdout.is_empty(),
            "fixture command must not report a successful fixture for invalid arguments"
        );
    }
}

fn prepare_fixture(data_dir: &Path) {
    let output = Command::new(fixture_binary())
        .arg("--data-dir")
        .arg(data_dir)
        .output()
        .expect("run history performance fixture command");
    assert!(
        output.status.success(),
        "fixture command failed: {}",
        String::from_utf8_lossy(&output.stderr)
    );

    let stdout = String::from_utf8(output.stdout).expect("fixture output must be UTF-8");
    assert_eq!(
        stdout.lines().count(),
        1,
        "fixture command must report exactly one line"
    );
    assert_eq!(
        serde_json::from_str::<serde_json::Value>(stdout.trim_end())
            .expect("fixture output must be JSON"),
        json!({"fixture": "history_search_performance", "items": FIXTURE_ITEM_COUNT})
    );
}

fn fixture_binary() -> PathBuf {
    FIXTURE_BINARY
        .map(PathBuf::from)
        .expect("Cargo must provide the history performance fixture binary")
}

fn measure_scenario<F>(
    data_dir: &Path,
    scenario: &str,
    sample_count: usize,
    expected_results: usize,
    threshold: Option<Duration>,
    make_request: F,
) where
    F: Fn() -> SearchRequest,
{
    let mut samples = Vec::with_capacity(sample_count);
    for _ in 0..sample_count {
        let mut service = CoreService::open(data_dir, FIXTURE_VAULT_ID, &FIXTURE_KEY)
            .expect("open core service before the timed search");
        let command = CoreCommand::Search(make_request());

        let started = Instant::now();
        let response = service.execute(command);
        let elapsed = started.elapsed();

        assert_eq!(
            response.expect("execute timed search").search_items().len(),
            expected_results,
            "{scenario} search result count"
        );
        samples.push(elapsed);
    }

    let p50 = nearest_rank(&samples, 50);
    let p95 = nearest_rank(&samples, 95);
    let passed = threshold.is_none_or(|limit| p95 <= limit);
    println!(
        "{}",
        json!({
            "scenario": scenario,
            "samples": samples.len(),
            "results": expected_results,
            "p50_ms": duration_ms(p50),
            "p95_ms": duration_ms(p95),
            "threshold_ms": threshold.map(duration_ms),
            "passed": passed,
        })
    );
    if let Some(limit) = threshold {
        assert!(
            p95 <= limit,
            "{scenario} P95 {} ms exceeded the {} ms threshold",
            duration_ms(p95),
            duration_ms(limit)
        );
    }
}

fn nearest_rank(samples: &[Duration], percentile: usize) -> Duration {
    assert!(
        !samples.is_empty(),
        "nearest-rank needs at least one sample"
    );
    let mut sorted = samples.to_vec();
    sorted.sort_unstable();
    let rank = (sorted.len() * percentile).div_ceil(100) - 1;
    sorted[rank]
}

fn duration_ms(duration: Duration) -> u128 {
    duration.as_millis()
}
