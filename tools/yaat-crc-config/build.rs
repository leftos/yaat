// Validate the canonical crc-environments.json at build time.
// The same file is embedded via include_str! in main.rs and is consumed by the
// C# CrcConfigService and Setup-CrcEnvironment.ps1 — a typo here should fail
// the build immediately rather than at runtime.

use std::path::PathBuf;

fn main() {
    let manifest_dir = std::env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR is set by cargo");
    let json_path: PathBuf = [&manifest_dir, "..", "..", "docs", "crc-environments.json"].iter().collect();

    println!("cargo:rerun-if-changed={}", json_path.display());

    let raw = std::fs::read_to_string(&json_path).unwrap_or_else(|e| panic!("failed to read {}: {e}", json_path.display()));

    let entries: Vec<serde_json::Value> = serde_json::from_str(&raw).unwrap_or_else(|e| panic!("failed to parse {}: {e}", json_path.display()));

    if entries.is_empty() {
        panic!("{} contained no entries", json_path.display());
    }

    let required_keys = ["name", "clientHubUrl", "apiBaseUrl", "isDisabled", "isSweatbox"];
    for (i, entry) in entries.iter().enumerate() {
        let obj = entry.as_object().unwrap_or_else(|| panic!("entry [{i}] is not an object"));
        for key in required_keys {
            if !obj.contains_key(key) {
                panic!("entry [{i}] missing required key '{key}'");
            }
        }
    }
}
