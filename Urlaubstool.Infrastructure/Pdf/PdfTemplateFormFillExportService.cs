using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using iText.Forms;
using iText.Forms.Fields;
using iText.IO.Font.Constants;
using iText.Kernel.Font;
using iText.Kernel.Pdf;

namespace Urlaubstool.Infrastructure.Pdf;

/// <summary>
/// PDF export service that fills AcroForm fields (no coordinate stamping).
/// Populates both Original and Kopie sections using field mappings matching Template.pdf.
/// </summary>
public sealed class PdfTemplateFormFillExportService
{
    private readonly string _exportDirectory;

    public PdfTemplateFormFillExportService(string exportDirectory)
    {
        _exportDirectory = exportDirectory ?? throw new ArgumentNullException(nameof(exportDirectory));
    }

    public async Task<string> CreateFilledPdfAsync(TemplateFieldValues values, DateTime startDate, DateTime endDate, bool flatten = true, string? exportDirectory = null)
    {
        if (values == null) throw new ArgumentNullException(nameof(values), "Feldwerte dürfen nicht leer sein.");

        var targetDirectory = exportDirectory ?? _exportDirectory;
        Directory.CreateDirectory(targetDirectory);

        var baseName = $"Urlaubsantrag_{startDate:yyyy-MM-dd}_bis_{endDate:yyyy-MM-dd}";
        var outputPath = GetVersionedFilePath(baseName, targetDirectory);

        using var templateStream = LoadEmbeddedTemplate();
        using var memoryStream = new MemoryStream();
        await templateStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        using var pdfReader = new PdfReader(memoryStream);
        using var pdfWriter = new PdfWriter(outputPath);
        using var pdfDoc = new PdfDocument(pdfReader, pdfWriter);

        var form = PdfAcroForm.GetAcroForm(pdfDoc, true);
        form.SetNeedAppearances(true);

        var font = PdfFontFactory.CreateFont(StandardFonts.HELVETICA);

        var fields = form.GetAllFormFields();
        LogTemplateFields(fields);

        // Original (oben)
        SetField(form, "Nachname", values.Nachname);
        SetField(form, "Vorname", values.Vorname);
        SetField(form, "Adresse", values.Adresse);
        SetField(form, "Abteilung", values.Abteilung);
        SetField(form, "Personalnummer", values.Personalnummer);
        SetField(form, "Urlaub_von", values.StartDatum);
        SetField(form, "Urlaub_bis", values.Enddatum);
        SetField(form, "Datum", values.AntragsDatum);

        SetField(form, "Text Box 6", values.GesamtVerfuegbarerUrlaub);
        SetField(form, "Text Box 7", values.BereitsErhaltenerUrlaub);
        SetField(form, "Text Box 9", values.MitDiesemAntragBeantragt);
        SetField(form, "Text Box 10", values.Resturlaub);
        SetField(form, "Text Box 11", values.AnzahlHalbtage);

        var azaLines = SplitAzaLines(values.AzaTage);
        SetField(form, "Text Box 13", azaLines.line1);
        SetField(form, "Text Box 12", azaLines.line2);

        // Kopie (unten)
        SetField(form, "Text Box 1", values.Nachname);
        SetField(form, "Text Box 2", values.Vorname);
        SetField(form, "Text Box 3", values.Adresse);
        SetField(form, "Text Box 4", values.Abteilung);
        SetField(form, "Text Box 5", values.Personalnummer);
        SetField(form, "Text Box 8", values.StartDatum);
        SetField(form, "Text Box 22", values.Enddatum);
        SetField(form, "Text Box 21", values.AntragsDatum);

        SetField(form, "Text Box 14", values.GesamtVerfuegbarerUrlaub);
        SetField(form, "Text Box 15", values.BereitsErhaltenerUrlaub);
        SetField(form, "Text Box 16", values.MitDiesemAntragBeantragt);
        SetField(form, "Text Box 17", values.Resturlaub);
        SetField(form, "Text Box 18", values.AnzahlHalbtage);

        SetField(form, "Text Box 20", azaLines.line1);
        SetField(form, "Text Box 19", azaLines.line2);

        RegenerateAppearances(form, font);

        if (flatten)
        {
            form.FlattenFields();
        }

        return outputPath;
    }

    private static (string line1, string line2) SplitAzaLines(string aza)
    {
        if (string.IsNullOrWhiteSpace(aza)) return (string.Empty, string.Empty);
        var lines = aza.Split('\n');
        var l1 = lines.Length > 0 ? lines[0] : string.Empty;
        var l2 = lines.Length > 1 ? lines[1] : string.Empty;
        return (l1, l2);
    }

    private static void SetField(PdfAcroForm form, string fieldName, string value)
    {
        var field = form.GetField(fieldName);
        if (field == null)
        {
            throw new InvalidOperationException($"Missing PDF field: {fieldName}");
        }

        field.SetFontSize(11);
        field.SetValue(value ?? string.Empty);
        Debug.WriteLine($"[PdfTemplateFormFillExportService] Set {fieldName} = {value}");
    }

    private static void RegenerateAppearances(PdfAcroForm form, PdfFont font)
    {
        var fields = form.GetAllFormFields();
        foreach (var kv in fields)
        {
            kv.Value.SetFont(font);
            kv.Value.RegenerateField();
        }
    }

    private static void LogTemplateFields(IDictionary<string, PdfFormField> fields)
    {
        Debug.WriteLine("[PdfTemplateFormFillExportService] Template fields:");
        foreach (var kv in fields)
        {
            Debug.WriteLine($"  - {kv.Key}");
        }
    }

    private Stream LoadEmbeddedTemplate()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Urlaubstool.Infrastructure.Pdf.Template.pdf";
        var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
        {
            throw new FileNotFoundException($"Embedded PDF template not found: {resourceName}. Ensure Template.pdf is marked as EmbeddedResource in .csproj");
        }
        return stream;
    }

    private string GetVersionedFilePath(string baseName, string? directory = null)
    {
        var targetDir = directory ?? _exportDirectory;
        var version = 1;
        string filePath;

        do
        {
            filePath = Path.Combine(targetDir, $"{baseName}_v{version}.pdf");
            version++;
        }
        while (File.Exists(filePath));

        return filePath;
    }
}
