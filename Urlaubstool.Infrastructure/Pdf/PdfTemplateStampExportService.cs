using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using iText.Kernel.Pdf;
using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Layout;
using iText.Layout.Element;
using iText.Layout.Properties;
using iText.IO.Font.Constants;

namespace Urlaubstool.Infrastructure.Pdf;

/// <summary>
/// PDF export service that stamps text onto Template.pdf at precise coordinates.
/// Uses iText7 for PDF manipulation and text rendering.
/// Fills both Original and Kopie sections with identical data.
/// NO dependency on AcroForm fields - pure coordinate-based text stamping.
/// </summary>
public class PdfTemplateStampExportService
{
    private readonly string _exportDirectory;

    public PdfTemplateStampExportService(string exportDirectory)
    {
        _exportDirectory = exportDirectory ?? throw new ArgumentNullException(nameof(exportDirectory));
    }

    /// <summary>
    /// Creates a filled PDF by stamping field values onto the embedded template.
    /// Generates versioned output filename: Urlaubsantrag_YYYY-MM-DD_bis_YYYY-MM-DD_v1.pdf
    /// If file exists, increments version (v2, v3, etc.).
    /// Returns the full path to the created PDF file.
    /// </summary>
    /// <param name="values">Resolved field values (from PlaceholderResolver)</param>
    /// <param name="startDate">Vacation start date (for filename)</param>
    /// <param name="endDate">Vacation end date (for filename)</param>
    public async Task<string> CreateStampedPdfAsync(TemplateFieldValues values, DateTime startDate, DateTime endDate)
    {
        if (values == null)
        {
            throw new ArgumentNullException(nameof(values), "Feldwerte dürfen nicht leer sein.");
        }

        // Ensure export directory exists
        try
        {
            Directory.CreateDirectory(_exportDirectory);
        }
        catch (Exception ex)
        {
            throw new IOException($"Export-Ordner konnte nicht erstellt werden: {_exportDirectory}", ex);
        }

        // Generate versioned output filename
        var baseName = $"Urlaubsantrag_{startDate:yyyy-MM-dd}_bis_{endDate:yyyy-MM-dd}";
        string outputPath;
        try
        {
            outputPath = GetVersionedFilePath(baseName);
        }
        catch (Exception ex)
        {
            throw new IOException($"Fehler beim Generieren des Dateinamens im Ordner: {_exportDirectory}", ex);
        }

        // Load embedded template PDF
        Stream templateStream;
        try
        {
            templateStream = LoadEmbeddedTemplate();
        }
        catch (Exception ex)
        {
            throw new FileNotFoundException("PDF-Template 'Template.pdf' konnte nicht aus den App-Ressourcen geladen werden. Möglicherweise ist die Installation beschädigt.", ex);
        }

        using (templateStream)
        {
            using var memoryStream = new MemoryStream();
            
            // Copy template to memory stream (iText modifies in place)
            try
            {
                await templateStream.CopyToAsync(memoryStream);
                memoryStream.Position = 0;
            }
            catch (Exception ex)
            {
                throw new IOException("Fehler beim Laden des PDF-Templates aus den Ressourcen.", ex);
            }

            // Open PDF for editing
            PdfReader pdfReader;
            PdfWriter pdfWriter;
            PdfDocument pdfDoc;
            try
            {
                pdfReader = new PdfReader(memoryStream);
                pdfWriter = new PdfWriter(outputPath);
                pdfDoc = new PdfDocument(pdfReader, pdfWriter);
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new UnauthorizedAccessException($"Keine Schreibberechtigung für die Datei: {outputPath}", ex);
            }
            catch (IOException ex)
            {
                throw new IOException($"Fehler beim Erstellen der PDF-Datei: {outputPath}\nStellen Sie sicher, dass die Datei nicht bereits geöffnet ist.", ex);
            }
            catch (Exception ex)
            {
                throw new Exception($"Fehler beim Öffnen des PDF-Dokuments: {ex.Message}", ex);
            }

            using (pdfReader)
            using (pdfWriter)
            using (pdfDoc)
            {
                // Get page 1 (single-page template)
                PdfPage page;
                try
                {
                    page = pdfDoc.GetPage(TemplateLayout.PageNumber);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Fehler: Template-PDF hat keine Seite {TemplateLayout.PageNumber}. Template möglicherweise beschädigt.", ex);
                }

                var document = new Document(pdfDoc);

                // Load standard Helvetica font (built into PDF, no embedding needed)
                PdfFont font;
                try
                {
                    font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);
                }
                catch (Exception ex)
                {
                    throw new Exception("Fehler beim Laden der Schriftart für das PDF.", ex);
                }

                // Stamp all fields onto Original section (top half)
                try
                {
                    StampOriginalSection(document, page, font, values);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Fehler beim Ausfüllen des Original-Abschnitts: {ex.Message}", ex);
                }

                // Stamp all fields onto Kopie section (bottom half) - identical data
                try
                {
                    StampKopieSection(document, page, font, values);
                }
                catch (Exception ex)
                {
                    throw new Exception($"Fehler beim Ausfüllen des Kopie-Abschnitts: {ex.Message}", ex);
                }

                try
                {
                    document.Close();
                }
                catch (Exception ex)
                {
                    throw new IOException($"Fehler beim Speichern der PDF-Datei: {outputPath}", ex);
                }
            }
        }

        return outputPath;
    }

    /// <summary>
    /// Stamps all field values onto the Original section (top half of page).
    /// Uses TemplateLayout.Original field definitions for positioning.
    /// </summary>
    private void StampOriginalSection(Document document, PdfPage page, PdfFont font, TemplateFieldValues values)
    {
        // Personal Information
        StampField(document, page, font, TemplateLayout.Original.Name, values.Name);
        StampField(document, page, font, TemplateLayout.Original.Vorname, values.Vorname);
        StampField(document, page, font, TemplateLayout.Original.Adresse, values.Adresse);
        StampField(document, page, font, TemplateLayout.Original.Abteilung, values.Abteilung);
        StampField(document, page, font, TemplateLayout.Original.Personalnummer, values.Personalnummer);

        // Request Date Range
        StampField(document, page, font, TemplateLayout.Original.StartDatum, values.StartDatum);
        StampField(document, page, font, TemplateLayout.Original.Enddatum, values.Enddatum);

        // Vacation Calculations
        StampField(document, page, font, TemplateLayout.Original.GesamtVerfuegbarerUrlaub, values.GesamtVerfuegbarerUrlaub);
        StampField(document, page, font, TemplateLayout.Original.BereitsErhaltenerUrlaub, values.BereitsErhaltenerUrlaub);
        StampField(document, page, font, TemplateLayout.Original.MitDiesemAntragBeantragt, values.MitDiesemAntragBeantragt);
        StampField(document, page, font, TemplateLayout.Original.Resturlaub, values.Resturlaub);
        StampField(document, page, font, TemplateLayout.Original.AnzahlHalbtage, values.AnzahlHalbtage);

        // AZA-Tage multiline section
        StampMultilineField(document, page, font, TemplateLayout.Original.AzaTage, values.AzaTage);

        // Signature and Date
        StampField(document, page, font, TemplateLayout.Original.AntragsDatum, values.AntragsDatum);
        StampField(document, page, font, TemplateLayout.Original.UnterschriftPlatzhalter, values.UnterschriftPlatzhalter);

        // Administrative fields (typically blank)
        StampField(document, page, font, TemplateLayout.Original.Genehmigt, values.Genehmigt);
        StampField(document, page, font, TemplateLayout.Original.Bearbeitet, values.Bearbeitet);
        StampField(document, page, font, TemplateLayout.Original.Personalabteilung, values.Personalabteilung);
        StampField(document, page, font, TemplateLayout.Original.AblehnungGrund, values.AblehnungGrund);
    }

    /// <summary>
    /// Stamps all field values onto the Kopie section (bottom half of page).
    /// Uses TemplateLayout.Kopie field definitions (vertically shifted from Original).
    /// Data is identical to Original section.
    /// </summary>
    private void StampKopieSection(Document document, PdfPage page, PdfFont font, TemplateFieldValues values)
    {
        // Personal Information
        StampField(document, page, font, TemplateLayout.Kopie.Name, values.Name);
        StampField(document, page, font, TemplateLayout.Kopie.Vorname, values.Vorname);
        StampField(document, page, font, TemplateLayout.Kopie.Adresse, values.Adresse);
        StampField(document, page, font, TemplateLayout.Kopie.Abteilung, values.Abteilung);
        StampField(document, page, font, TemplateLayout.Kopie.Personalnummer, values.Personalnummer);

        // Request Date Range
        StampField(document, page, font, TemplateLayout.Kopie.StartDatum, values.StartDatum);
        StampField(document, page, font, TemplateLayout.Kopie.Enddatum, values.Enddatum);

        // Vacation Calculations
        StampField(document, page, font, TemplateLayout.Kopie.GesamtVerfuegbarerUrlaub, values.GesamtVerfuegbarerUrlaub);
        StampField(document, page, font, TemplateLayout.Kopie.BereitsErhaltenerUrlaub, values.BereitsErhaltenerUrlaub);
        StampField(document, page, font, TemplateLayout.Kopie.MitDiesemAntragBeantragt, values.MitDiesemAntragBeantragt);
        StampField(document, page, font, TemplateLayout.Kopie.Resturlaub, values.Resturlaub);
        StampField(document, page, font, TemplateLayout.Kopie.AnzahlHalbtage, values.AnzahlHalbtage);

        // AZA-Tage multiline section
        StampMultilineField(document, page, font, TemplateLayout.Kopie.AzaTage, values.AzaTage);

        // Signature and Date
        StampField(document, page, font, TemplateLayout.Kopie.AntragsDatum, values.AntragsDatum);
        StampField(document, page, font, TemplateLayout.Kopie.UnterschriftPlatzhalter, values.UnterschriftPlatzhalter);

        // Administrative fields (typically blank)
        StampField(document, page, font, TemplateLayout.Kopie.Genehmigt, values.Genehmigt);
        StampField(document, page, font, TemplateLayout.Kopie.Bearbeitet, values.Bearbeitet);
        StampField(document, page, font, TemplateLayout.Kopie.Personalabteilung, values.Personalabteilung);
        StampField(document, page, font, TemplateLayout.Kopie.AblehnungGrund, values.AblehnungGrund);
    }

    /// <summary>
    /// Stamps a single-line text field at the specified layout position.
    /// Handles text alignment (Left/Right/Center) and automatic font shrinking if text exceeds max width.
    /// If text is too wide even at minimum font size, adds ellipsis (...).
    /// </summary>
    private void StampField(Document document, PdfPage page, PdfFont font, TemplateLayout.FieldLayout layout, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return; // Skip empty fields
        }

        // Calculate effective font size (shrink if text too wide)
        var fontSize = CalculateFittingFontSize(font, text, layout.MaxWidth, layout.FontSize);
        
        // If text still too wide at minimum size, truncate with ellipsis
        var displayText = text;
        var textWidth = CalculateTextWidth(font, displayText, fontSize);
        if (textWidth > layout.MaxWidth)
        {
            displayText = TruncateWithEllipsis(font, text, layout.MaxWidth, fontSize);
        }

        // Create text paragraph with specified alignment
        var paragraph = new Paragraph(displayText)
            .SetFont(font)
            .SetFontSize(fontSize)
            .SetMargin(0)
            .SetPadding(0);

        // Calculate X position based on alignment
        float x = layout.Alignment switch
        {
            TemplateLayout.TextAlignment.Left => layout.X,
            TemplateLayout.TextAlignment.Right => layout.X - CalculateTextWidth(font, displayText, fontSize),
            TemplateLayout.TextAlignment.Center => layout.X - (CalculateTextWidth(font, displayText, fontSize) / 2),
            _ => layout.X
        };

        // Position and render text at baseline Y coordinate
        paragraph.SetFixedPosition(x, layout.Y, layout.MaxWidth);
        document.Add(paragraph);
    }

    /// <summary>
    /// Stamps a multiline text field (e.g., AZA-Tage section).
    /// Splits text by newline and renders up to MaxLines lines.
    /// Each line is clipped to MaxWidth with ellipsis if necessary.
    /// </summary>
    private void StampMultilineField(Document document, PdfPage page, PdfFont font, TemplateLayout.MultilineFieldLayout layout, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return; // Skip empty multiline fields
        }

        var lines = text.Split('\n');
        var currentY = layout.TopY;

        for (int i = 0; i < Math.Min(lines.Length, layout.MaxLines); i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                currentY -= layout.LineHeight; // Skip blank lines but advance Y
                continue;
            }

            // Truncate line if too wide
            var displayLine = line;
            var lineWidth = CalculateTextWidth(font, displayLine, layout.FontSize);
            if (lineWidth > layout.MaxWidth)
            {
                displayLine = TruncateWithEllipsis(font, line, layout.MaxWidth, layout.FontSize);
            }

            // Create and position paragraph for this line
            var paragraph = new Paragraph(displayLine)
                .SetFont(font)
                .SetFontSize(layout.FontSize)
                .SetMargin(0)
                .SetPadding(0);

            float x = layout.Alignment switch
            {
                TemplateLayout.TextAlignment.Left => layout.X,
                TemplateLayout.TextAlignment.Right => layout.X + layout.MaxWidth - CalculateTextWidth(font, displayLine, layout.FontSize),
                TemplateLayout.TextAlignment.Center => layout.X + (layout.MaxWidth - CalculateTextWidth(font, displayLine, layout.FontSize)) / 2,
                _ => layout.X
            };

            paragraph.SetFixedPosition(x, currentY, layout.MaxWidth);
            document.Add(paragraph);

            currentY -= layout.LineHeight; // Move down for next line
        }
    }

    /// <summary>
    /// Calculates the optimal font size to fit text within max width.
    /// Shrinks font from initial size down to minimum threshold.
    /// Returns the largest font size that fits, or minimum size if text still too wide.
    /// </summary>
    private float CalculateFittingFontSize(PdfFont font, string text, float maxWidth, float initialFontSize)
    {
        var fontSize = initialFontSize;
        
        while (fontSize > TemplateLayout.MinimumFontSize)
        {
            var width = CalculateTextWidth(font, text, fontSize);
            if (width <= maxWidth)
            {
                return fontSize;
            }
            fontSize -= 0.5f; // Shrink in 0.5pt increments
        }

        return TemplateLayout.MinimumFontSize;
    }

    /// <summary>
    /// Calculates the rendered width of text at a given font size.
    /// Uses iText's font metrics for accurate measurement.
    /// </summary>
    private float CalculateTextWidth(PdfFont font, string text, float fontSize)
    {
        // iText calculates width in 1000ths of font size units
        var width = font.GetWidth(text, fontSize);
        return width;
    }

    /// <summary>
    /// Truncates text to fit within max width and appends ellipsis (...).
    /// Binary search approach to find longest fitting prefix.
    /// </summary>
    private string TruncateWithEllipsis(PdfFont font, string text, float maxWidth, float fontSize)
    {
        const string ellipsis = "...";
        var ellipsisWidth = CalculateTextWidth(font, ellipsis, fontSize);
        var availableWidth = maxWidth - ellipsisWidth;

        if (availableWidth <= 0)
        {
            return ellipsis; // Not enough space, just show ellipsis
        }

        // Binary search for longest fitting prefix
        int left = 0;
        int right = text.Length;
        int bestFit = 0;

        while (left <= right)
        {
            int mid = (left + right) / 2;
            var candidate = text.Substring(0, mid);
            var width = CalculateTextWidth(font, candidate, fontSize);

            if (width <= availableWidth)
            {
                bestFit = mid;
                left = mid + 1;
            }
            else
            {
                right = mid - 1;
            }
        }

        return text.Substring(0, bestFit) + ellipsis;
    }

    /// <summary>
    /// Loads the embedded Template.pdf from the assembly resources.
    /// Throws FileNotFoundException if template is missing.
    /// </summary>
    private Stream LoadEmbeddedTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = "Urlaubstool.Infrastructure.Pdf.Template.pdf";
        
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Embedded PDF template not found: {resourceName}. " +
                                          "Ensure Template.pdf is marked as EmbeddedResource in .csproj");
        }

        return stream;
    }

    /// <summary>
    /// Generates a versioned file path in the export directory.
    /// If file exists, increments version number (v1 → v2 → v3...).
    /// Format: {baseName}_v{version}.pdf
    /// </summary>
    private string GetVersionedFilePath(string baseName)
    {
        int version = 1;
        string filePath;

        do
        {
            filePath = System.IO.Path.Combine(_exportDirectory, $"{baseName}_v{version}.pdf");
            version++;
        }
        while (File.Exists(filePath));

        return filePath;
    }
}
