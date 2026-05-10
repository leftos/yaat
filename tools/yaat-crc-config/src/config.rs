//! CRC config-directory detection and DevEnvironments.json read/merge/write.
//!
//! Mirrors src/Yaat.Client.Core/Services/CrcConfigService.cs — keep them in lockstep.

use serde::{Deserialize, Serialize};
use serde_json::Value;
use std::path::{Path, PathBuf};

const MARKER_FILE_NAME: &str = "GeneralSettings.json";
const ENVIRONMENTS_FILE_NAME: &str = "DevEnvironments.json";

/// Canonical entry list, embedded from docs/crc-environments.json at compile time.
const EMBEDDED_ENVIRONMENTS_JSON: &str = include_str!("../../../docs/crc-environments.json");

#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct CrcEnvironmentEntry {
    pub name: String,
    pub client_hub_url: String,
    pub api_base_url: String,
    #[serde(default)]
    pub is_disabled: bool,
    #[serde(default)]
    pub is_sweatbox: bool,
}

pub fn yaat_entries() -> Vec<CrcEnvironmentEntry> {
    serde_json::from_str(EMBEDDED_ENVIRONMENTS_JSON).expect("embedded crc-environments.json must parse — validated by build.rs")
}

pub fn find_crc_config_dir() -> Option<PathBuf> {
    enumerate_candidates()
        .into_iter()
        .find(|candidate| candidate.join(MARKER_FILE_NAME).is_file())
}

pub fn environments_path(config_dir: &Path) -> PathBuf {
    config_dir.join(ENVIRONMENTS_FILE_NAME)
}

/// Returns true when every entry in `wanted` already exists in
/// DevEnvironments.json with matching client/api URLs (case-insensitive on name + URLs).
/// Mirrors AreYaatEntriesPresent in CrcConfigService.cs.
pub fn are_entries_present(config_dir: &Path, wanted: &[CrcEnvironmentEntry]) -> bool {
    let json_path = environments_path(config_dir);
    if !json_path.is_file() {
        return false;
    }

    let raw = match std::fs::read_to_string(&json_path) {
        Ok(s) => s,
        Err(_) => return false,
    };

    let existing: Vec<CrcEnvironmentEntry> = match serde_json::from_str(&raw) {
        Ok(v) => v,
        Err(_) => return false,
    };

    wanted.iter().all(|expected| {
        existing.iter().find(|e| e.name.eq_ignore_ascii_case(&expected.name)).is_some_and(|e| {
            e.client_hub_url.eq_ignore_ascii_case(&expected.client_hub_url) && e.api_base_url.eq_ignore_ascii_case(&expected.api_base_url)
        })
    })
}

/// Reads DevEnvironments.json (or starts empty), upserts each entry by case-insensitive
/// name match, writes the file back. Preserves any unrelated entries already present.
/// Mirrors Configure() in CrcConfigService.cs.
pub fn upsert_entries(config_dir: &Path, wanted: &[CrcEnvironmentEntry]) -> std::io::Result<()> {
    let json_path = environments_path(config_dir);

    // Read as generic Value array so we preserve unknown fields on existing entries.
    let mut existing: Vec<Value> = if json_path.is_file() {
        let raw = std::fs::read_to_string(&json_path)?;
        serde_json::from_str(&raw).unwrap_or_default()
    } else {
        Vec::new()
    };

    for entry in wanted {
        let upsert = serde_json::to_value(entry).map_err(std::io::Error::other)?;

        if let Some(slot) = existing.iter_mut().find(|v| name_matches(v, &entry.name)) {
            // Update the four mutable fields, preserving any unrelated keys CRC may have added.
            if let Some(obj) = slot.as_object_mut() {
                obj.insert("clientHubUrl".to_string(), Value::String(entry.client_hub_url.clone()));
                obj.insert("apiBaseUrl".to_string(), Value::String(entry.api_base_url.clone()));
                obj.insert("isDisabled".to_string(), Value::Bool(entry.is_disabled));
                obj.insert("isSweatbox".to_string(), Value::Bool(entry.is_sweatbox));
            } else {
                *slot = upsert;
            }
        } else {
            existing.push(upsert);
        }
    }

    let serialized = serde_json::to_string_pretty(&existing).map_err(std::io::Error::other)?;
    std::fs::write(&json_path, serialized)?;
    Ok(())
}

fn name_matches(v: &Value, name: &str) -> bool {
    v.get("name").and_then(|n| n.as_str()).is_some_and(|n| n.eq_ignore_ascii_case(name))
}

#[cfg(windows)]
fn enumerate_candidates() -> Vec<PathBuf> {
    let mut out = Vec::new();
    if let Some(dir) = registry::install_dir() {
        out.push(dir);
    }
    if let Some(local) = std::env::var_os("LOCALAPPDATA") {
        let path = PathBuf::from(local).join("CRC");
        out.push(path);
    }
    out
}

#[cfg(target_os = "macos")]
fn enumerate_candidates() -> Vec<PathBuf> {
    home_dir()
        .map(|h| vec![h.join("Library").join("Application Support").join("CRC")])
        .unwrap_or_default()
}

#[cfg(target_os = "linux")]
fn enumerate_candidates() -> Vec<PathBuf> {
    home_dir().map(|h| vec![h.join(".config").join("CRC")]).unwrap_or_default()
}

#[cfg(unix)]
fn home_dir() -> Option<PathBuf> {
    std::env::var_os("HOME").map(PathBuf::from)
}

#[cfg(windows)]
mod registry {
    use std::path::PathBuf;
    use windows_sys::Win32::Foundation::ERROR_SUCCESS;
    use windows_sys::Win32::System::Registry::{
        RegCloseKey, RegOpenKeyExW, RegQueryValueExW, HKEY, HKEY_CURRENT_USER, KEY_READ, REG_EXPAND_SZ, REG_SZ,
    };

    pub fn install_dir() -> Option<PathBuf> {
        const SUBKEY: &str = "Software\\CRC";
        const VALUE: &str = "Install_Dir";

        let subkey_w = wide(SUBKEY);
        let value_w = wide(VALUE);

        unsafe {
            let mut hkey: HKEY = std::ptr::null_mut();
            let status = RegOpenKeyExW(HKEY_CURRENT_USER, subkey_w.as_ptr(), 0, KEY_READ, &mut hkey);
            if status != ERROR_SUCCESS {
                return None;
            }

            let mut data_type: u32 = 0;
            let mut data_size: u32 = 0;
            let status = RegQueryValueExW(
                hkey,
                value_w.as_ptr(),
                std::ptr::null_mut(),
                &mut data_type,
                std::ptr::null_mut(),
                &mut data_size,
            );
            if status != ERROR_SUCCESS || (data_type != REG_SZ && data_type != REG_EXPAND_SZ) || data_size == 0 {
                RegCloseKey(hkey);
                return None;
            }

            // data_size is bytes; allocate u16 buffer rounded up.
            let mut buf: Vec<u16> = vec![0; data_size.div_ceil(2) as usize];
            let mut size_again = data_size;
            let status = RegQueryValueExW(
                hkey,
                value_w.as_ptr(),
                std::ptr::null_mut(),
                std::ptr::null_mut(),
                buf.as_mut_ptr().cast(),
                &mut size_again,
            );
            RegCloseKey(hkey);
            if status != ERROR_SUCCESS {
                return None;
            }

            // Trim trailing NULs.
            let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
            let s = String::from_utf16_lossy(&buf[..len]);
            if s.is_empty() {
                None
            } else {
                Some(PathBuf::from(s))
            }
        }
    }

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(std::iter::once(0)).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn embedded_entries_match_canonical_list() {
        let entries = yaat_entries();
        assert_eq!(entries.len(), 2);

        let prod = entries.iter().find(|e| e.name == "YAAT1").expect("YAAT1 present");
        assert_eq!(prod.client_hub_url, "https://yaat1.leftos.dev/hubs/client");
        assert_eq!(prod.api_base_url, "https://yaat1.leftos.dev");
        assert!(!prod.is_disabled);
        assert!(!prod.is_sweatbox);

        let local = entries.iter().find(|e| e.name == "YAAT Local").expect("YAAT Local present");
        assert_eq!(local.client_hub_url, "http://localhost:5000/hubs/client");
        assert_eq!(local.api_base_url, "http://localhost:5000");
    }

    #[test]
    fn are_entries_present_returns_true_when_all_match() {
        let dir = tempdir();
        let entries = yaat_entries();
        upsert_entries(&dir, &entries).unwrap();
        assert!(are_entries_present(&dir, &entries));
    }

    #[test]
    fn are_entries_present_returns_false_when_url_drifts() {
        let dir = tempdir();
        let entries = yaat_entries();
        upsert_entries(&dir, &entries).unwrap();

        // Manually rewrite YAAT1's clientHubUrl to a different value.
        let path = environments_path(&dir);
        let mut existing: Vec<Value> = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        existing[0]
            .as_object_mut()
            .unwrap()
            .insert("clientHubUrl".into(), Value::String("https://changed/hubs/client".into()));
        std::fs::write(&path, serde_json::to_string_pretty(&existing).unwrap()).unwrap();

        assert!(!are_entries_present(&dir, &entries));
    }

    #[test]
    fn upsert_preserves_unrelated_entries() {
        let dir = tempdir();
        let path = environments_path(&dir);

        let initial = serde_json::json!([{
            "name": "SomeOtherEnv",
            "clientHubUrl": "https://other/hubs/client",
            "apiBaseUrl": "https://other",
            "isDisabled": false,
            "isSweatbox": false,
            "extraFieldFromCrc": "preserve me"
        }]);
        std::fs::write(&path, serde_json::to_string_pretty(&initial).unwrap()).unwrap();

        upsert_entries(&dir, &yaat_entries()).unwrap();

        let after: Vec<Value> = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        assert_eq!(after.len(), 3);
        assert_eq!(after[0]["name"], "SomeOtherEnv");
        assert_eq!(after[0]["extraFieldFromCrc"], "preserve me");
        assert!(after.iter().any(|e| e["name"] == "YAAT1"));
        assert!(after.iter().any(|e| e["name"] == "YAAT Local"));
    }

    #[test]
    fn upsert_updates_existing_entry_in_place() {
        let dir = tempdir();
        let path = environments_path(&dir);

        // Pre-seed with a stale YAAT1 URL plus an unknown field that should be preserved.
        let stale = serde_json::json!([{
            "name": "YAAT1",
            "clientHubUrl": "https://stale/hubs/client",
            "apiBaseUrl": "https://stale",
            "isDisabled": true,
            "isSweatbox": true,
            "userNote": "keep me"
        }]);
        std::fs::write(&path, serde_json::to_string_pretty(&stale).unwrap()).unwrap();

        upsert_entries(&dir, &yaat_entries()).unwrap();

        let after: Vec<Value> = serde_json::from_str(&std::fs::read_to_string(&path).unwrap()).unwrap();
        let yaat1 = after.iter().find(|e| e["name"] == "YAAT1").unwrap();
        assert_eq!(yaat1["clientHubUrl"], "https://yaat1.leftos.dev/hubs/client");
        assert_eq!(yaat1["apiBaseUrl"], "https://yaat1.leftos.dev");
        assert_eq!(yaat1["isDisabled"], false);
        assert_eq!(yaat1["isSweatbox"], false);
        assert_eq!(yaat1["userNote"], "keep me");
    }

    fn tempdir() -> PathBuf {
        let mut p = std::env::temp_dir();
        p.push(format!("yaat-crc-test-{}", std::process::id()));
        p.push(format!(
            "{}",
            std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_nanos()
        ));
        std::fs::create_dir_all(&p).unwrap();
        p
    }
}
