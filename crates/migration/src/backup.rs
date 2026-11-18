use std::fs::{self, File, OpenOptions};
use std::io::{self, Read, Write};
use std::path::{Path, PathBuf};

use sha2::{Digest, Sha256};
use thiserror::Error;

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SourceBackup {
    pub source: PathBuf,
    pub destination: PathBuf,
    pub bytes_copied: u64,
    pub sha256: String,
}

#[derive(Debug, Error)]
pub enum SourceBackupError {
    #[error("Die Quelldatei konnte nicht gelesen werden: {path}: {source}")]
    SourceIo {
        path: PathBuf,
        #[source]
        source: io::Error,
    },
    #[error("Die Sicherungsdatei konnte nicht geschrieben werden: {path}: {source}")]
    DestinationIo {
        path: PathBuf,
        #[source]
        source: io::Error,
    },
    #[error("Die Quelle wurde während der Sicherung verändert: {0}")]
    SourceChanged(PathBuf),
    #[error(
        "Die Sicherungsdatei konnte nach einem Fehler nicht entfernt werden: {path}: {source}"
    )]
    CleanupFailed {
        path: PathBuf,
        #[source]
        source: io::Error,
    },
}

pub fn backup_source_to_new(
    source_path: impl AsRef<Path>,
    destination_path: impl AsRef<Path>,
) -> Result<SourceBackup, SourceBackupError> {
    let source_path = source_path.as_ref();
    let destination_path = destination_path.as_ref();
    let mut source = File::open(source_path).map_err(|source| SourceBackupError::SourceIo {
        path: source_path.to_path_buf(),
        source,
    })?;
    let before = source
        .metadata()
        .map_err(|source| SourceBackupError::SourceIo {
            path: source_path.to_path_buf(),
            source,
        })?;

    if let Some(parent) = destination_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
    {
        fs::create_dir_all(parent).map_err(|source| SourceBackupError::DestinationIo {
            path: parent.to_path_buf(),
            source,
        })?;
    }

    let mut destination = OpenOptions::new()
        .write(true)
        .create_new(true)
        .open(destination_path)
        .map_err(|source| SourceBackupError::DestinationIo {
            path: destination_path.to_path_buf(),
            source,
        })?;

    let result = copy_and_hash(&mut source, &mut destination, source_path, destination_path);
    drop(destination);
    let after = source
        .metadata()
        .map_err(|source| SourceBackupError::SourceIo {
            path: source_path.to_path_buf(),
            source,
        })?;

    let copied = match result {
        Ok(copied) => copied,
        Err(error) => {
            remove_partial(destination_path)?;
            return Err(error);
        }
    };

    if before.len() != after.len() || before.modified().ok() != after.modified().ok() {
        remove_partial(destination_path)?;
        return Err(SourceBackupError::SourceChanged(source_path.to_path_buf()));
    }

    Ok(SourceBackup {
        source: source_path.to_path_buf(),
        destination: destination_path.to_path_buf(),
        bytes_copied: copied.0,
        sha256: copied.1,
    })
}

fn copy_and_hash(
    source: &mut File,
    destination: &mut File,
    source_path: &Path,
    destination_path: &Path,
) -> Result<(u64, String), SourceBackupError> {
    let mut hasher = Sha256::new();
    let mut bytes_copied = 0_u64;
    let mut buffer = [0_u8; 64 * 1024];

    loop {
        let count = source
            .read(&mut buffer)
            .map_err(|source| SourceBackupError::SourceIo {
                path: source_path.to_path_buf(),
                source,
            })?;
        if count == 0 {
            break;
        }
        destination.write_all(&buffer[..count]).map_err(|source| {
            SourceBackupError::DestinationIo {
                path: destination_path.to_path_buf(),
                source,
            }
        })?;
        hasher.update(&buffer[..count]);
        bytes_copied += count as u64;
    }

    destination
        .sync_all()
        .map_err(|source| SourceBackupError::DestinationIo {
            path: destination_path.to_path_buf(),
            source,
        })?;
    let sha256 = hasher
        .finalize()
        .iter()
        .map(|byte| format!("{byte:02x}"))
        .collect();
    Ok((bytes_copied, sha256))
}

fn remove_partial(path: &Path) -> Result<(), SourceBackupError> {
    fs::remove_file(path).map_err(|source| SourceBackupError::CleanupFailed {
        path: path.to_path_buf(),
        source,
    })
}

#[cfg(test)]
mod tests {
    use std::sync::atomic::{AtomicU64, Ordering};

    use super::*;

    static NEXT_TEMP_ID: AtomicU64 = AtomicU64::new(1);

    struct TempDirectory(PathBuf);

    impl TempDirectory {
        fn new() -> Self {
            let id = NEXT_TEMP_ID.fetch_add(1, Ordering::Relaxed);
            let path = std::env::temp_dir().join(format!(
                "urlaubstool-migration-backup-{}-{id}",
                std::process::id()
            ));
            fs::create_dir_all(&path).expect("create temporary test directory");
            Self(path)
        }
    }

    impl Drop for TempDirectory {
        fn drop(&mut self) {
            let _ = fs::remove_dir_all(&self.0);
        }
    }

    #[test]
    fn req_mig_002_backup_copies_source_without_changing_it() {
        let directory = TempDirectory::new();
        let source = directory.0.join("settings.json");
        let destination = directory.0.join("settings.backup.json");
        let bytes = br#"{"Version":2,"Name":"Ada Beispiel"}"#;
        fs::write(&source, bytes).expect("write source");

        let backup = backup_source_to_new(&source, &destination).expect("create backup");

        assert_eq!(fs::read(&source).expect("source remains"), bytes);
        assert_eq!(fs::read(&destination).expect("backup bytes"), bytes);
        assert_eq!(backup.bytes_copied, bytes.len() as u64);
        assert_eq!(backup.sha256.len(), 64);
    }

    #[test]
    fn req_mig_002_backup_refuses_to_overwrite_an_existing_file() {
        let directory = TempDirectory::new();
        let source = directory.0.join("settings.json");
        let destination = directory.0.join("settings.backup.json");
        fs::write(&source, b"source").expect("write source");
        fs::write(&destination, b"existing backup").expect("write backup sentinel");

        assert!(backup_source_to_new(&source, &destination).is_err());
        assert_eq!(
            fs::read(destination).expect("backup remains"),
            b"existing backup"
        );
        assert_eq!(fs::read(source).expect("source remains"), b"source");
    }
}
