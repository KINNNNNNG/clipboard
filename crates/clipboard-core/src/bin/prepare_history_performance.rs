use clipboard_domain::{ClipboardContent, ClipboardItem};
use clipboard_storage::Database;
use serde_json::json;
use std::{
    error::Error,
    ffi::{OsStr, OsString},
    io,
    path::PathBuf,
};
use uuid::Uuid;

const FIXTURE_VAULT_ID: Uuid = Uuid::from_u128(0x5a17);
const FIXTURE_KEY: [u8; 32] = [0x6a; 32];
const FIXTURE_ITEM_COUNT: usize = 10_000;

fn main() -> Result<(), Box<dyn Error>> {
    let data_dir = parse_data_dir(std::env::args_os().skip(1))?;
    std::fs::create_dir_all(&data_dir)?;

    {
        let database = Database::open(&data_dir.join("history.db"), &FIXTURE_KEY)?;
        for index in 0..FIXTURE_ITEM_COUNT {
            let item = ClipboardItem::new(
                Uuid::from_u128(index as u128 + 1),
                FIXTURE_VAULT_ID,
                ClipboardContent::Text(fixture_text(index)),
                format!("benchmark-source-{:02}.exe", index % 10),
                index as i64,
            );
            database.items().insert(&item)?;
        }
    }

    println!(
        "{}",
        json!({"fixture": "history_search_performance", "items": FIXTURE_ITEM_COUNT})
    );
    Ok(())
}

fn parse_data_dir(mut arguments: impl Iterator<Item = OsString>) -> Result<PathBuf, io::Error> {
    let Some(flag) = arguments.next() else {
        return Err(usage_error());
    };
    if flag.as_os_str() != OsStr::new("--data-dir") {
        return Err(usage_error());
    }
    let Some(data_dir) = arguments.next() else {
        return Err(usage_error());
    };
    if data_dir.is_empty() || arguments.next().is_some() {
        return Err(usage_error());
    }
    Ok(PathBuf::from(data_dir))
}

fn fixture_text(index: usize) -> String {
    if index % 100 == 0 {
        format!("benchmark text {index} performance-needle")
    } else {
        format!("benchmark text {index}")
    }
}

fn usage_error() -> io::Error {
    io::Error::new(io::ErrorKind::InvalidInput, "expected --data-dir <path>")
}
