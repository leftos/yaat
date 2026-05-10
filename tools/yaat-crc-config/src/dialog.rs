//! Cross-platform native dialogs.
//!
//! Windows: MessageBoxW via windows-sys.
//! macOS:   shell out to `osascript` (AppleScript is built into every Mac).
//! Linux:   try zenity, then kdialog, then console fallback.

const TITLE: &str = "YAAT CRC Configuration";

#[cfg(windows)]
mod platform {
    use std::iter::once;
    use windows_sys::Win32::UI::WindowsAndMessaging::{MessageBoxW, IDYES, MB_ICONINFORMATION, MB_ICONQUESTION, MB_OK, MB_YESNO};

    fn wide(s: &str) -> Vec<u16> {
        s.encode_utf16().chain(once(0)).collect()
    }

    pub fn info(message: &str) {
        let title_w = wide(super::TITLE);
        let msg_w = wide(message);
        unsafe {
            MessageBoxW(std::ptr::null_mut(), msg_w.as_ptr(), title_w.as_ptr(), MB_OK | MB_ICONINFORMATION);
        }
    }

    pub fn confirm(message: &str) -> bool {
        let title_w = wide(super::TITLE);
        let msg_w = wide(message);
        let result = unsafe { MessageBoxW(std::ptr::null_mut(), msg_w.as_ptr(), title_w.as_ptr(), MB_YESNO | MB_ICONQUESTION) };
        result == IDYES
    }
}

#[cfg(target_os = "macos")]
mod platform {
    use std::process::Command;

    fn applescript_escape(s: &str) -> String {
        s.replace('\\', "\\\\").replace('"', "\\\"")
    }

    pub fn info(message: &str) {
        let script = format!(
            "display dialog \"{}\" with title \"{}\" buttons {{\"OK\"}} default button \"OK\" with icon note",
            applescript_escape(message),
            applescript_escape(super::TITLE)
        );
        let _ = Command::new("osascript").arg("-e").arg(script).status();
    }

    pub fn confirm(message: &str) -> bool {
        let script = format!(
            "try\nset r to button returned of (display dialog \"{}\" with title \"{}\" buttons {{\"Cancel\", \"Yes\"}} default button \"Yes\" with icon caution)\nif r is \"Yes\" then return \"yes\"\nreturn \"no\"\non error\nreturn \"no\"\nend try",
            applescript_escape(message),
            applescript_escape(super::TITLE)
        );
        match Command::new("osascript").arg("-e").arg(script).output() {
            Ok(out) => String::from_utf8_lossy(&out.stdout).trim() == "yes",
            Err(_) => false,
        }
    }
}

#[cfg(target_os = "linux")]
mod platform {
    use std::process::{Command, Stdio};

    fn try_run(cmd: &str, args: &[&str]) -> Option<bool> {
        match Command::new(cmd)
            .args(args)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .status()
        {
            Ok(s) => Some(s.success()),
            Err(_) => None,
        }
    }

    pub fn info(message: &str) {
        if try_run("zenity", &["--info", "--title", super::TITLE, "--text", message, "--no-wrap"]).is_some() {
            return;
        }
        if try_run("kdialog", &["--title", super::TITLE, "--msgbox", message]).is_some() {
            return;
        }
        eprintln!("[{}] {}", super::TITLE, message);
    }

    pub fn confirm(message: &str) -> bool {
        if let Some(ok) = try_run("zenity", &["--question", "--title", super::TITLE, "--text", message]) {
            return ok;
        }
        if let Some(ok) = try_run("kdialog", &["--title", super::TITLE, "--yesno", message]) {
            return ok;
        }
        // Console fallback.
        eprintln!("[{}] {} [y/N]", super::TITLE, message);
        let mut answer = String::new();
        if std::io::stdin().read_line(&mut answer).is_err() {
            return false;
        }
        matches!(answer.trim().to_ascii_lowercase().as_str(), "y" | "yes")
    }
}

pub fn info(message: &str) {
    platform::info(message);
}

pub fn confirm(message: &str) -> bool {
    platform::confirm(message)
}
