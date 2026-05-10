//! Standalone tool that adds YAAT server entries to CRC's DevEnvironments.json.
//!
//! Mirrors the `Tools → Configure CRC Environments` flow in the full YAAT client.
//! Distributed as a single small binary so students can configure CRC without
//! installing the full client.

#![cfg_attr(all(windows, not(debug_assertions)), windows_subsystem = "windows")]

mod config;
mod dialog;

use std::process::ExitCode;

fn main() -> ExitCode {
    let wanted = config::yaat_entries();

    let Some(config_dir) = config::find_crc_config_dir() else {
        dialog::info("CRC is not installed on this computer (or has not been launched yet).\n\nInstall and launch CRC at least once, then run this tool again.");
        return ExitCode::from(1);
    };

    if config::are_entries_present(&config_dir, &wanted) {
        dialog::info("CRC already has YAAT server environments configured.\n\nNo changes made.");
        return ExitCode::from(2);
    }

    let entries_summary = wanted
        .iter()
        .map(|e| format!("  • {}  →  {}", e.name, e.api_base_url))
        .collect::<Vec<_>>()
        .join("\n");

    let prompt = format!(
        "CRC config directory found at:\n{}\n\nThis will add the following entries to DevEnvironments.json:\n\n{}\n\nProceed?",
        config_dir.display(),
        entries_summary
    );

    if !dialog::confirm(&prompt) {
        return ExitCode::from(0);
    }

    if let Err(err) = config::upsert_entries(&config_dir, &wanted) {
        dialog::info(&format!(
            "Failed to update CRC DevEnvironments.json:\n\n{}\n\nFile: {}",
            err,
            config::environments_path(&config_dir).display()
        ));
        return ExitCode::from(3);
    }

    dialog::info("YAAT server environments added to CRC.\n\nRestart CRC to pick up the changes.");
    ExitCode::from(0)
}
