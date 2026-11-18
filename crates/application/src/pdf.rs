use crate::{calculate_vacation, VacationCalculationInput};
use chrono::{Datelike, NaiveDate, Weekday};
use lopdf::{dictionary, Dictionary, Document, Object, ObjectId, Stream};
use std::collections::HashMap;
use thiserror::Error;

#[derive(Debug, Clone)]
pub struct PdfDay {
    pub date: NaiveDate,
    pub weekday: String,
    pub counted_half_units: u32,
    pub is_aza: bool,
    pub reasons: Vec<String>,
}

#[derive(Debug, Clone)]
pub struct PdfRequestData {
    pub first_name: String,
    pub last_name: String,
    pub address: Option<String>,
    pub department: Option<String>,
    pub personnel_number: Option<String>,
    pub class_name: Option<String>,
    /// Datum des Antrags; wird von außen übergeben, damit der Export deterministisch bleibt.
    pub application_date: NaiveDate,
    pub start_date: NaiveDate,
    pub end_date: NaiveDate,
    pub start_half_day: bool,
    pub end_half_day: bool,
    pub annual_entitlement_half_units: u32,
    pub approved_half_units: u32,
    pub requested_half_units: u32,
    pub remaining_after_half_units: i64,
    pub days: Vec<PdfDay>,
    pub warnings: Vec<String>,
}

#[derive(Clone)]
pub struct PdfPersonalDetails {
    pub first_name: String,
    pub last_name: String,
    pub address: Option<String>,
    pub department: Option<String>,
    pub personnel_number: Option<String>,
    pub class_name: Option<String>,
}

#[derive(Debug, Error)]
pub enum PdfError {
    #[error("Ungültige Antragsdaten: {0}")]
    InvalidData(String),
    #[error("PDF konnte nicht erstellt werden: {0}")]
    Rendering(String),
}

fn weekday_name(day: Weekday) -> &'static str {
    match day {
        Weekday::Mon => "Montag",
        Weekday::Tue => "Dienstag",
        Weekday::Wed => "Mittwoch",
        Weekday::Thu => "Donnerstag",
        Weekday::Fri => "Freitag",
        Weekday::Sat => "Samstag",
        Weekday::Sun => "Sonntag",
    }
}

/// Tageswerte wie in der Altanwendung (Format „0.##“, deutsches Komma): 30 / 4,5.
fn format_days(half_units: i64) -> String {
    let absolute = half_units.unsigned_abs();
    format!(
        "{}{}{}",
        if half_units < 0 { "-" } else { "" },
        absolute / 2,
        if absolute.is_multiple_of(2) { "" } else { ",5" }
    )
}

pub fn prepare_request_pdf(
    input: VacationCalculationInput,
    personal: PdfPersonalDetails,
    application_date: NaiveDate,
) -> Result<PdfRequestData, PdfError> {
    if personal.first_name.trim().is_empty() || personal.last_name.trim().is_empty() {
        return Err(PdfError::InvalidData(
            "Vor- und Nachname sind erforderlich.".into(),
        ));
    }
    let start_date = NaiveDate::parse_from_str(&input.start_date, "%Y-%m-%d")
        .map_err(|error| PdfError::InvalidData(error.to_string()))?;
    let end_date = NaiveDate::parse_from_str(&input.end_date, "%Y-%m-%d")
        .map_err(|error| PdfError::InvalidData(error.to_string()))?;
    let start_half_day = input.start_half_day;
    let end_half_day = input.end_half_day;
    let result = calculate_vacation(input).map_err(PdfError::InvalidData)?;
    if !result.errors.is_empty() || result.requested_half_units == 0 {
        return Err(PdfError::InvalidData(if result.errors.is_empty() {
            "Der Antrag enthält keine Urlaubstage.".into()
        } else {
            result.errors.join(" ")
        }));
    }
    let days = result
        .days
        .into_iter()
        .map(|day| {
            Ok(PdfDay {
                date: NaiveDate::parse_from_str(&day.date, "%Y-%m-%d")
                    .map_err(|error| PdfError::InvalidData(error.to_string()))?,
                weekday: day.weekday,
                counted_half_units: day.counted_half_units,
                is_aza: day.is_aza,
                reasons: day.reasons,
            })
        })
        .collect::<Result<Vec<_>, PdfError>>()?;
    Ok(PdfRequestData {
        first_name: personal.first_name,
        last_name: personal.last_name,
        address: personal.address,
        department: personal.department,
        personnel_number: personal.personnel_number,
        class_name: personal.class_name,
        application_date,
        start_date,
        end_date,
        start_half_day,
        end_half_day,
        annual_entitlement_half_units: result.annual_entitlement_half_units,
        approved_half_units: result.already_approved_half_units,
        requested_half_units: result.requested_half_units,
        remaining_after_half_units: result.remaining_after_half_units,
        days,
        warnings: result.warnings,
    })
}

pub fn render_request_pdf(request: &PdfRequestData) -> Result<Vec<u8>, PdfError> {
    if request.first_name.trim().is_empty()
        || request.last_name.trim().is_empty()
        || request.start_date > request.end_date
        || request.start_date.year() != request.end_date.year()
        || request.annual_entitlement_half_units == 0
        || request.requested_half_units == 0
        || request.remaining_after_half_units < 0
        || request.remaining_after_half_units
            != i64::from(request.annual_entitlement_half_units)
                - i64::from(request.approved_half_units)
                - i64::from(request.requested_half_units)
        || (request.start_date == request.end_date
            && request.start_half_day
            && request.end_half_day)
    {
        return Err(PdfError::InvalidData(
            "Name, Zeitraum oder Urlaubskonto prüfen.".into(),
        ));
    }
    let expected_days = (request.end_date - request.start_date).num_days() + 1;
    let mut total = 0u64;
    if request.days.len() != expected_days as usize {
        return Err(PdfError::InvalidData(
            "Die Tagesaufschlüsselung ist unvollständig.".into(),
        ));
    }
    for (index, day) in request.days.iter().enumerate() {
        let expected = request
            .start_date
            .checked_add_signed(chrono::Duration::days(index as i64))
            .ok_or_else(|| PdfError::InvalidData("Ungültiges Tagesdatum.".into()))?;
        if day.date != expected
            || day.weekday != weekday_name(day.date.weekday())
            || day.counted_half_units > 2
            || (day.is_aza && day.counted_half_units != 0)
        {
            return Err(PdfError::InvalidData(
                "Die Tagesaufschlüsselung ist widersprüchlich.".into(),
            ));
        }
        total += u64::from(day.counted_half_units);
    }
    if total != u64::from(request.requested_half_units) {
        return Err(PdfError::InvalidData(
            "Die Tagessumme stimmt nicht mit dem Antrag überein.".into(),
        ));
    }
    let aza_dates: Vec<String> = request
        .days
        .iter()
        .filter(|day| day.is_aza)
        .map(|day| day.date.format("%d.%m.").to_string())
        .collect();
    let half_days = i64::from(request.start_half_day) + i64::from(request.end_half_day);
    let text = |value: &Option<String>| value.as_deref().unwrap_or("").trim().to_owned();
    let values: [(usize, String); 13] = [
        (0, request.last_name.trim().to_owned()),
        (1, request.first_name.trim().to_owned()),
        (2, text(&request.address)),
        (3, text(&request.department)),
        (4, text(&request.personnel_number)),
        (5, request.start_date.format("%d.%m.%Y").to_string()),
        (6, request.end_date.format("%d.%m.%Y").to_string()),
        (7, request.application_date.format("%d.%m.%Y").to_string()),
        (
            8,
            format_days(i64::from(request.annual_entitlement_half_units)),
        ),
        (9, format_days(i64::from(request.approved_half_units))),
        (10, format_days(i64::from(request.requested_half_units))),
        (11, format_days(request.remaining_after_half_units)),
        (12, format_days(half_days)),
    ];

    let mut document = Document::load_mem(TEMPLATE).map_err(template_error)?;
    let pages = document.get_pages();
    let page_id = match (pages.len(), pages.get(&1)) {
        (1, Some(id)) => *id,
        _ => return Err(unexpected_template("genau eine Seite erwartet")),
    };
    let rectangles = widget_rectangles(&document, page_id)?;
    let rectangle = |name: &str| {
        rectangles
            .get(name)
            .copied()
            .ok_or_else(|| unexpected_template(&format!("Formularfeld „{name}“ fehlt")))
    };

    let mut content = b"Q\nq\n".to_vec();
    for (index, value) in &values {
        for name in [FORM_FIELDS[*index].0, FORM_FIELDS[*index].1] {
            let area = rectangle(name)?;
            let size = fit_font_size(value, area[2] - area[0] - 2.0 * FIELD_PADDING)?;
            draw_text(&mut content, value, area, size)?;
        }
    }
    let aza_areas = [
        [rectangle(AZA_FIELDS[0].0)?, rectangle(AZA_FIELDS[0].1)?],
        [rectangle(AZA_FIELDS[1].0)?, rectangle(AZA_FIELDS[1].1)?],
    ];
    let line_width = |areas: &[[f32; 4]; 2]| {
        areas
            .iter()
            .map(|area| area[2] - area[0] - 2.0 * FIELD_PADDING)
            .fold(f32::INFINITY, f32::min)
    };
    let (aza_lines, aza_size) = layout_aza(
        &aza_dates,
        [line_width(&aza_areas[0]), line_width(&aza_areas[1])],
    )?;
    for (line, areas) in aza_lines.iter().zip(aza_areas) {
        for area in areas {
            draw_text(&mut content, line, area, aza_size)?;
        }
    }
    content.extend_from_slice(b"Q\n");

    flatten_form(&mut document, page_id, content).map_err(template_error)?;
    let mut bytes = Vec::new();
    document.save_to(&mut bytes).map_err(|error| {
        PdfError::Rendering(format!("Das PDF konnte nicht geschrieben werden: {error}"))
    })?;
    Ok(bytes)
}

/// Original-Formular der Altanwendung (`Urlaubstool.Infrastructure/Pdf/Template.pdf`).
/// Unverändert übernommen (SHA-256 b09b28f4…8626c). Die Endung `.bin` verhindert, dass
/// PDF-Optimierer auf dem Entwicklungsrechner die Datei umschreiben und die Formularfelder entfernen.
const TEMPLATE: &[u8] = include_bytes!("../assets/Urlaubsantrag_Vorlage.pdf.bin");
const FONT_RESOURCE: &str = "UtHelv";
/// Schriftgröße der Altanwendung; zu lange Werte werden bis zur Mindestgröße verkleinert.
const FONT_SIZE: f32 = 11.0;
const MIN_FONT_SIZE: f32 = 6.0;
const FIELD_PADDING: f32 = 2.0;

/// Feldnamen der Vorlage als (Original, Kopie); Zuordnung wie in
/// `PdfTemplateFormFillExportService` der Altanwendung.
const FORM_FIELDS: [(&str, &str); 13] = [
    ("Nachname", "Text Box 1"),
    ("Vorname", "Text Box 2"),
    ("Adresse", "Text Box 3"),
    ("Abteilung", "Text Box 4"),
    ("Personalnummer", "Text Box 5"),
    ("Urlaub_von", "Text Box 8"),
    ("Urlaub_bis", "Text Box 22"),
    ("Datum", "Text Box 21"),
    ("Text Box 6", "Text Box 14"),
    ("Text Box 7", "Text Box 15"),
    ("Text Box 9", "Text Box 16"),
    ("Text Box 10", "Text Box 17"),
    ("Text Box 11", "Text Box 18"),
];
/// Die beiden Zeilen „AZA-Tage im Zeitraum“ als (Original, Kopie).
const AZA_FIELDS: [(&str, &str); 2] = [
    ("Text Box 13", "Text Box 20"),
    ("Text Box 12", "Text Box 19"),
];

fn template_error(error: lopdf::Error) -> PdfError {
    PdfError::Rendering(format!(
        "Die PDF-Vorlage konnte nicht verarbeitet werden: {error}"
    ))
}

fn unexpected_template(detail: &str) -> PdfError {
    PdfError::Rendering(format!(
        "Die PDF-Vorlage ist unerwartet aufgebaut: {detail}."
    ))
}

/// Zeichenbreite der Standardschrift Helvetica in Tausendstel der Schriftgröße.
fn helvetica_width(character: char) -> u16 {
    match character {
        ' ' | '!' | ',' | '.' | '/' | ':' | ';' | 'I' | '[' | '\\' | ']' | 'f' | 't' => 278,
        'i' | 'j' | 'l' => 222,
        '\'' => 191,
        '|' => 260,
        '(' | ')' | '-' | '`' | 'r' => 333,
        '{' | '}' => 334,
        '"' => 355,
        '*' => 389,
        '^' => 469,
        'J' | 'c' | 'k' | 's' | 'v' | 'x' | 'y' | 'z' => 500,
        '+' | '<' | '=' | '>' | '~' => 584,
        'F' | 'T' | 'Z' | 'ß' => 611,
        'A' | 'B' | 'E' | 'K' | 'P' | 'S' | 'V' | 'X' | 'Y' | '&' | 'Ä' => 667,
        'C' | 'D' | 'H' | 'N' | 'R' | 'U' | 'w' | 'Ü' => 722,
        'G' | 'O' | 'Q' | 'Ö' => 778,
        'M' | 'm' => 833,
        '%' => 889,
        'W' => 944,
        '@' => 1015,
        // Ziffern, übrige Kleinbuchstaben und alle anderen Zeichen; für nicht
        // aufgeführte breite Zeichen sichert der Mindestwert die Prüfung nicht ab,
        // deshalb gilt für sie die Breite des breitesten Buchstabens.
        '0'..='9'
        | '#'
        | '$'
        | '?'
        | '_'
        | 'L'
        | 'a'
        | 'b'
        | 'd'
        | 'e'
        | 'g'
        | 'h'
        | 'n'
        | 'o'
        | 'p'
        | 'q'
        | 'u'
        | 'ä'
        | 'ö'
        | 'ü' => 556,
        _ => 1015,
    }
}

fn text_width(text: &str, font_size: f32) -> f32 {
    let units: u32 = text.chars().map(|c| u32::from(helvetica_width(c))).sum();
    units as f32 / 1000.0 * font_size
}

/// Größte Schriftgröße zwischen Standard und Mindestgröße, mit der der Text in das Feld passt.
fn fit_font_size(text: &str, available_width: f32) -> Result<f32, PdfError> {
    let mut size = FONT_SIZE;
    while size >= MIN_FONT_SIZE {
        if text_width(text, size) <= available_width {
            return Ok(size);
        }
        size -= 0.5;
    }
    Err(PdfError::InvalidData(format!(
        "„{text}“ ist zu lang für das Formularfeld."
    )))
}

/// Verteilt die AZA-Tage auf die beiden Formularzeilen, ohne einen Tag wegzulassen.
fn layout_aza(dates: &[String], widths: [f32; 2]) -> Result<([String; 2], f32), PdfError> {
    if dates.is_empty() {
        return Ok(([String::new(), String::new()], FONT_SIZE));
    }
    let mut size = FONT_SIZE;
    while size >= MIN_FONT_SIZE {
        let mut lines = [String::new(), String::new()];
        let mut line = 0;
        let mut fits = true;
        for (index, date) in dates.iter().enumerate() {
            let last = index + 1 == dates.len();
            loop {
                let mut candidate = lines[line].clone();
                if !candidate.is_empty() {
                    candidate.push(' ');
                }
                candidate.push_str(date);
                if !last {
                    candidate.push(',');
                }
                if text_width(&candidate, size) <= widths[line] {
                    lines[line] = candidate;
                    break;
                }
                if line == 0 && !lines[0].is_empty() {
                    line = 1;
                } else {
                    fits = false;
                    break;
                }
            }
            if !fits {
                break;
            }
        }
        if fits {
            return Ok((lines, size));
        }
        size -= 0.5;
    }
    Err(PdfError::InvalidData(format!(
        "{} AZA-Tage passen nicht in die zwei Formularzeilen. Bitte den Antrag aufteilen.",
        dates.len()
    )))
}

fn win_ansi(character: char) -> Option<u8> {
    match character {
        ' '..='~' | '\u{a0}'..='\u{ff}' => Some(character as u8),
        '€' => Some(0x80),
        '‚' => Some(0x82),
        '„' => Some(0x84),
        '…' => Some(0x85),
        'Š' => Some(0x8a),
        'Œ' => Some(0x8c),
        'Ž' => Some(0x8e),
        '‘' => Some(0x91),
        '’' => Some(0x92),
        '“' => Some(0x93),
        '”' => Some(0x94),
        '–' => Some(0x96),
        '—' => Some(0x97),
        'š' => Some(0x9a),
        'œ' => Some(0x9c),
        'ž' => Some(0x9e),
        'Ÿ' => Some(0x9f),
        _ => None,
    }
}

/// Schreibt einen einzeiligen Text linksbündig und vertikal mittig in ein Formularfeld.
fn draw_text(
    content: &mut Vec<u8>,
    text: &str,
    area: [f32; 4],
    font_size: f32,
) -> Result<(), PdfError> {
    if text.is_empty() {
        return Ok(());
    }
    let mut literal = Vec::with_capacity(text.len() + 2);
    for character in text.chars() {
        let byte = win_ansi(character).ok_or_else(|| {
            PdfError::InvalidData(format!(
                "Das Zeichen „{character}“ wird von der Formularschrift nicht unterstützt."
            ))
        })?;
        match byte {
            b'(' | b')' | b'\\' => literal.extend_from_slice(&[b'\\', byte]),
            0x20..=0x7e => literal.push(byte),
            _ => literal.extend_from_slice(format!("\\{byte:03o}").as_bytes()),
        }
    }
    let x = area[0] + FIELD_PADDING;
    // Versalhöhe von Helvetica: 0,718 der Schriftgröße.
    let y = area[1] + (area[3] - area[1] - 0.718 * font_size) / 2.0;
    content.extend_from_slice(
        format!("BT /{FONT_RESOURCE} {font_size:.1} Tf 0 g 1 0 0 1 {x:.2} {y:.2} Tm (").as_bytes(),
    );
    content.extend_from_slice(&literal);
    content.extend_from_slice(b") Tj ET\n");
    Ok(())
}

fn resolve<'a>(document: &'a Document, object: &'a Object) -> Result<&'a Object, lopdf::Error> {
    match object {
        Object::Reference(id) => document.get_object(*id),
        other => Ok(other),
    }
}

/// Liest Name und Rechteck aller Formularfelder der Seite.
fn widget_rectangles(
    document: &Document,
    page_id: ObjectId,
) -> Result<HashMap<String, [f32; 4]>, PdfError> {
    let read = || -> Result<HashMap<String, [f32; 4]>, lopdf::Error> {
        let page = document.get_dictionary(page_id)?;
        let annotations = resolve(document, page.get(b"Annots")?)?.as_array()?;
        let mut rectangles = HashMap::new();
        for annotation in annotations {
            let widget = resolve(document, annotation)?.as_dict()?;
            let (Ok(name), Ok(rectangle)) = (widget.get(b"T"), widget.get(b"Rect")) else {
                continue;
            };
            let name = String::from_utf8_lossy(name.as_str()?).into_owned();
            let numbers = resolve(document, rectangle)?.as_array()?;
            if numbers.len() != 4 {
                continue;
            }
            let mut area = [0.0; 4];
            for (slot, number) in area.iter_mut().zip(numbers) {
                *slot = number.as_float()?;
            }
            rectangles.insert(
                name,
                [
                    area[0].min(area[2]),
                    area[1].min(area[3]),
                    area[0].max(area[2]),
                    area[1].max(area[3]),
                ],
            );
        }
        Ok(rectangles)
    };
    read().map_err(template_error)
}

/// Legt den Text fest auf die Seite und entfernt das Formular, damit das
/// exportierte PDF nicht mehr veränderbar ist (wie das „Flatten“ der Altanwendung).
fn flatten_form(
    document: &mut Document,
    page_id: ObjectId,
    text_content: Vec<u8>,
) -> Result<(), lopdf::Error> {
    let font_id = document.add_object(dictionary! {
        "Type" => "Font",
        "Subtype" => "Type1",
        "BaseFont" => "Helvetica",
        "Encoding" => "WinAnsiEncoding",
    });
    let resources_id = document
        .get_dictionary(page_id)?
        .get(b"Resources")?
        .as_reference()
        .ok();
    let fonts_id = {
        let resources = match resources_id {
            Some(id) => document.get_dictionary(id)?,
            None => document
                .get_dictionary(page_id)?
                .get(b"Resources")?
                .as_dict()?,
        };
        resources
            .get(b"Font")
            .ok()
            .and_then(|fonts| fonts.as_reference().ok())
    };
    if let Some(id) = fonts_id {
        document
            .get_dictionary_mut(id)?
            .set(FONT_RESOURCE, Object::Reference(font_id));
    } else {
        let resources = match resources_id {
            Some(id) => document.get_dictionary_mut(id)?,
            None => document
                .get_dictionary_mut(page_id)?
                .get_mut(b"Resources")?
                .as_dict_mut()?,
        };
        if !resources.has(b"Font") {
            resources.set("Font", Dictionary::new());
        }
        resources
            .get_mut(b"Font")?
            .as_dict_mut()?
            .set(FONT_RESOURCE, Object::Reference(font_id));
    }

    // Der vorhandene Seiteninhalt wird in q … Q eingeschlossen, damit der neue
    // Text im unveränderten Koordinatensystem der Seite steht.
    let open_id = document.add_object(Stream::new(Dictionary::new(), b"q\n".to_vec()));
    let text_id = document.add_object(Stream::new(Dictionary::new(), text_content));
    let page = document.get_dictionary_mut(page_id)?;
    let mut contents = vec![Object::Reference(open_id)];
    match page.get(b"Contents")?.clone() {
        Object::Array(existing) => contents.extend(existing),
        existing @ Object::Reference(_) => contents.push(existing),
        _ => return Err(lopdf::Error::DictKey("Contents".into())),
    }
    contents.push(Object::Reference(text_id));
    page.set("Contents", Object::Array(contents));
    page.remove(b"Annots");
    document.catalog_mut()?.remove(b"AcroForm");
    document.prune_objects();
    Ok(())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn req_pdf_002_values_use_the_legacy_number_format() {
        assert_eq!(format_days(60), "30");
        assert_eq!(format_days(9), "4,5");
        assert_eq!(format_days(1), "0,5");
        assert_eq!(format_days(0), "0");
        assert_eq!(format_days(-3), "-1,5");
    }

    #[test]
    fn req_pdf_002_long_values_shrink_and_never_overflow_the_field() {
        assert_eq!(fit_font_size("Müller", 250.0).unwrap(), FONT_SIZE);
        let long = "Abteilung für besonders lange Bezeichnungen und Sonderaufgaben";
        let size = fit_font_size(long, 250.0).unwrap();
        assert!((MIN_FONT_SIZE..FONT_SIZE).contains(&size));
        assert!(text_width(long, size) <= 250.0);
        assert!(text_width(long, size + 0.5) > 250.0);
        assert!(fit_font_size(&long.repeat(3), 250.0).is_err());
        assert_eq!(text_width("30,5", 10.0), (556.0 * 3.0 + 278.0) / 100.0);
    }

    #[test]
    fn req_aza_003_aza_days_fill_two_lines_completely_or_are_refused() {
        let dates = |count: usize| -> Vec<String> {
            (1..=count).map(|day| format!("{day:02}.03.")).collect()
        };
        let widths = [107.7, 107.7];
        let (lines, size) = layout_aza(&dates(1), widths).unwrap();
        assert_eq!(lines, ["01.03.".to_string(), String::new()]);
        assert_eq!(size, FONT_SIZE);
        let (lines, size) = layout_aza(&dates(5), widths).unwrap();
        let joined = format!("{} {}", lines[0], lines[1]);
        for date in dates(5) {
            assert!(joined.contains(&date), "{date} fehlt in {joined}");
        }
        assert!(lines[0].ends_with(','));
        assert!(lines.iter().all(|line| text_width(line, size) <= 107.7));
        assert!(layout_aza(&dates(40), widths).is_err());
        assert_eq!(
            layout_aza(&[], widths).unwrap().0,
            [String::new(), String::new()]
        );
    }

    #[test]
    fn req_pdf_002_only_characters_of_the_form_font_are_accepted() {
        let mut content = Vec::new();
        draw_text(
            &mut content,
            "Größe (A) \\ €",
            [0.0, 0.0, 100.0, 14.0],
            11.0,
        )
        .unwrap();
        let text = String::from_utf8_lossy(&content).into_owned();
        assert!(text.contains("Gr\\366\\337e \\(A\\) \\\\ \\200"), "{text}");
        assert!(draw_text(&mut content, "𠜎", [0.0, 0.0, 100.0, 14.0], 11.0).is_err());
        assert!(draw_text(&mut content, "Zeile\num", [0.0, 0.0, 100.0, 14.0], 11.0).is_err());
    }
}
