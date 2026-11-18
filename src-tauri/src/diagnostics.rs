//! Lokales Diagnoseprotokoll (REQ-DIAG-001, REQ-DIAG-004).
//!
//! Jede Zeile ist ein JSON-Objekt mit Zeitpunkt, Level, Kontext und Meldung.
//! Es werden nur Fehlermeldungen und technische Ereignisse protokolliert, nie
//! Eingabedaten (Namen, Adressen, Antragsdaten). Pfade im Benutzerordner werden
//! auf `~` gekürzt, damit der Benutzername nicht im Protokoll steht.

use std::fs::{self, OpenOptions};
use std::io::Write;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};

use chrono::{DateTime, SecondsFormat, Utc};
use serde::{Deserialize, Serialize};

pub const LOG_FILE_NAME: &str = "urlaubstool.log";
/// Ab dieser Größe wird die Datei nach `urlaubstool.log.1` verschoben.
pub const DEFAULT_MAX_BYTES: u64 = 512 * 1024;
const MAX_MESSAGE_CHARS: usize = 2000;
const MAX_CONTEXT_CHARS: usize = 64;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum Level {
    Info,
    Warn,
    Error,
}

#[derive(Debug, Clone, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct LogEntry {
    pub time_utc: String,
    pub level: Level,
    pub context: String,
    pub message: String,
}

pub struct DiagnosticLog {
    path: PathBuf,
    home: Option<String>,
    max_bytes: u64,
    lock: Mutex<()>,
}

impl DiagnosticLog {
    pub fn new(directory: &Path, home: Option<String>, max_bytes: u64) -> Self {
        Self {
            path: directory.join(LOG_FILE_NAME),
            home: home.filter(|value| value.len() > 1),
            max_bytes,
            lock: Mutex::new(()),
        }
    }

    pub fn path(&self) -> &Path {
        &self.path
    }

    fn rotated_path(&self) -> PathBuf {
        self.path.with_extension("log.1")
    }

    /// Kürzt Pfade im Benutzerordner, Steuerzeichen und überlange Texte.
    pub fn redact(&self, text: &str) -> String {
        let mut value = text.to_owned();
        if let Some(home) = &self.home {
            value = value.replace(home.as_str(), "~");
        }
        let cleaned: String = value
            .chars()
            .map(|ch| if ch.is_control() { ' ' } else { ch })
            .collect();
        let mut shortened: String = cleaned.chars().take(MAX_MESSAGE_CHARS).collect();
        if cleaned.chars().count() > MAX_MESSAGE_CHARS {
            shortened.push('…');
        }
        shortened
    }

    /// Schreibt einen Eintrag. Fehler beim Protokollieren werden verschluckt:
    /// Das Protokoll darf die eigentliche Funktion nie stören.
    pub fn write(&self, level: Level, context: &str, message: &str, now: DateTime<Utc>) {
        let Ok(_guard) = self.lock.lock() else {
            return;
        };
        let entry = LogEntry {
            time_utc: now.to_rfc3339_opts(SecondsFormat::Secs, true),
            level,
            context: sanitize_context(context),
            message: self.redact(message),
        };
        let Ok(line) = serde_json::to_string(&entry) else {
            return;
        };
        if let Some(parent) = self.path.parent() {
            let _ = fs::create_dir_all(parent);
        }
        if fs::metadata(&self.path).is_ok_and(|meta| meta.len() >= self.max_bytes) {
            let _ = fs::rename(&self.path, self.rotated_path());
        }
        if let Ok(mut file) = OpenOptions::new()
            .create(true)
            .append(true)
            .open(&self.path)
        {
            let _ = writeln!(file, "{line}");
        }
    }

    /// Jüngste Einträge (neueste zuerst), optional nur ab einem Level.
    pub fn recent(&self, limit: usize, minimum: Level) -> Vec<LogEntry> {
        let rank = |level: Level| match level {
            Level::Info => 0,
            Level::Warn => 1,
            Level::Error => 2,
        };
        let mut entries = Vec::new();
        for path in [self.rotated_path(), self.path.clone()] {
            if let Ok(text) = fs::read_to_string(&path) {
                entries.extend(
                    text.lines()
                        .filter_map(|line| serde_json::from_str::<LogEntry>(line).ok())
                        .filter(|entry| rank(entry.level) >= rank(minimum)),
                );
            }
        }
        entries.reverse();
        entries.truncate(limit);
        entries
    }
}

/// Kontext ist ein technischer Bezeichner (z. B. Befehlsname), keine Freitext-Eingabe.
pub fn sanitize_context(context: &str) -> String {
    let cleaned: String = context
        .chars()
        .filter(|ch| ch.is_ascii_alphanumeric() || matches!(ch, '_' | '-' | '.'))
        .take(MAX_CONTEXT_CHARS)
        .collect();
    if cleaned.is_empty() {
        "unbekannt".into()
    } else {
        cleaned
    }
}

static GLOBAL: OnceLock<DiagnosticLog> = OnceLock::new();

pub fn install(log: DiagnosticLog) {
    let _ = GLOBAL.set(log);
}

pub fn global() -> Option<&'static DiagnosticLog> {
    GLOBAL.get()
}

pub fn record(level: Level, context: &str, message: &str) {
    if let Some(log) = global() {
        log.write(level, context, message, Utc::now());
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn directory(label: &str) -> PathBuf {
        let dir =
            std::env::temp_dir().join(format!("urlaubstool-diag-{label}-{}", std::process::id()));
        let _ = fs::remove_dir_all(&dir);
        dir
    }

    fn at(minute: u32) -> DateTime<Utc> {
        DateTime::parse_from_rfc3339(&format!("2026-11-12T08:{minute:02}:00Z"))
            .unwrap()
            .with_timezone(&Utc)
    }

    #[test]
    fn req_diag_001_entries_are_structured_and_redacted() {
        let dir = directory("structured");
        let log = DiagnosticLog::new(&dir, Some("/Users/beispiel".into()), DEFAULT_MAX_BYTES);
        log.write(
            Level::Error,
            "export_request_pdf",
            "PDF unter /Users/beispiel/Desktop/a.pdf\nnicht gespeichert",
            at(1),
        );
        log.write(Level::Info, "start <script>", "App gestartet", at(2));
        let text = fs::read_to_string(log.path()).unwrap();
        assert!(!text.contains("/Users/beispiel"));
        assert_eq!(text.lines().count(), 2);
        let entries = log.recent(10, Level::Info);
        assert_eq!(entries[0].context, "startscript");
        assert_eq!(entries[1].level, Level::Error);
        assert_eq!(
            entries[1].message,
            "PDF unter ~/Desktop/a.pdf nicht gespeichert"
        );
        assert_eq!(entries[1].time_utc, "2026-11-12T08:01:00Z");
        assert_eq!(log.recent(10, Level::Error).len(), 1);
        assert_eq!(sanitize_context(""), "unbekannt");
        let long = "x".repeat(5000);
        assert_eq!(log.redact(&long).chars().count(), MAX_MESSAGE_CHARS + 1);
        let _ = fs::remove_dir_all(&dir);
    }

    #[test]
    fn req_diag_004_log_rotates_and_keeps_recent_entries() {
        let dir = directory("rotate");
        let log = DiagnosticLog::new(&dir, None, 400);
        for minute in 0..20 {
            log.write(
                Level::Error,
                "test",
                &format!("Fehler {minute}"),
                at(minute),
            );
        }
        assert!(fs::metadata(log.path()).unwrap().len() < 800);
        assert!(log.rotated_path().exists());
        let recent = log.recent(3, Level::Error);
        assert_eq!(recent[0].message, "Fehler 19");
        assert_eq!(recent.len(), 3);
        // Defekte Zeilen werden beim Lesen übersprungen.
        fs::write(log.path(), "kein json\n").unwrap();
        assert!(log
            .recent(50, Level::Info)
            .iter()
            .all(|e| e.message.starts_with("Fehler")));
        let _ = fs::remove_dir_all(&dir);
    }
}
