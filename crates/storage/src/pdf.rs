use std::io::{self, Write};
use std::path::Path;

use urlaubstool_application::{render_request_pdf, PdfRequestData};

use crate::database::{remove_failed_output, reserve_new_file, StorageError};

pub fn export_pdf_to_new(
    request: &PdfRequestData,
    destination: impl AsRef<Path>,
) -> Result<(), StorageError> {
    let bytes = render_request_pdf(request)?;
    let path = destination.as_ref();
    let mut file = reserve_new_file(path)?;
    let result = file.write_all(&bytes).and_then(|_| file.sync_all());
    drop(file);
    let result = result.and_then(|_| {
        if std::fs::read(path)? == bytes {
            Ok(())
        } else {
            Err(io::Error::other(
                "Die PDF-Datei konnte nicht vollständig geprüft werden.",
            ))
        }
    });
    if let Err(source) = result {
        remove_failed_output(path)?;
        return Err(StorageError::Io {
            path: path.to_path_buf(),
            source,
        });
    }
    Ok(())
}
