using System.Globalization;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Paths;
using Urlaubstool.Infrastructure.Services;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.Infrastructure.Pdf;

/// <summary>
/// Generates fixed-layout PDFs aligned with the Urlaubsschein template wording.
/// Handles exception logging and detailed error reporting for PDF export failures.
/// </summary>
public sealed class PdfExportService
{
    private readonly PathService _paths;
    private readonly ExceptionLogService _exceptionLogger;

    public PdfExportService(PathService paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _exceptionLogger = new ExceptionLogService(paths);
        QuestPDF.Settings.License = LicenseType.Community;
    }

    public string Export(AppSettings settings, VacationRequest request, CalculationResult calculation, EntitlementSnapshot entitlement)
    {
        if (calculation.HasErrors)
        {
            throw new InvalidOperationException("PDF-Export ist bei vorhandenen Fehlern blockiert.");
        }

        var exportDir = _paths.GetExportDirectory();
        System.Diagnostics.Debug.WriteLine($"[PdfExportService.Export] Starting PDF export to: {exportDir}");
        
        // Preflight check: Ensure export directory exists and is writable
        try
        {
            Directory.CreateDirectory(exportDir);
            
            // Test write access by creating and deleting a temporary file
            var tempTestFile = Path.Combine(exportDir, $".write_test_{Guid.NewGuid()}.tmp");
            try
            {
                File.WriteAllText(tempTestFile, "test");
                File.Delete(tempTestFile);
                System.Diagnostics.Debug.WriteLine($"[PdfExportService.Export] Export directory write access verified");
            }
            catch (Exception writeTestEx)
            {
                // Log the write test failure and throw an informative exception
                _exceptionLogger.Write("PDF Export - Write Access Test", writeTestEx);
                throw new IOException(
                    $"Kein Schreibzugriff auf den Export-Ordner: {exportDir}",
                    writeTestEx);
            }
        }
        catch (Exception preflightEx)
        {
            if (preflightEx is IOException)
                throw; // Re-throw the informative IOException
            
            // Log any other preflight errors and wrap them
            var logPath = _exceptionLogger.Write("PDF Export - Preflight", preflightEx);
            throw new Exception(
                $"PDF-Export fehlgeschlagen: Fehler bei der Vorbereitung des Export-Ordners. Details siehe Log: {logPath}",
                preflightEx);
        }

        var baseName = $"Urlaubsantrag_{request.StartDate:yyyy-MM-dd}_bis_{request.EndDate:yyyy-MM-dd}";
        var targetPath = BuildVersionedPath(exportDir, baseName);

        var culture = new CultureInfo("de-DE");
        var totalFormatted = calculation.TotalDays.ToString("0.##", culture);
        var alreadyFormatted = entitlement.AlreadyApproved.ToString("0.##", culture);
        var remainingFormatted = entitlement.Remaining.ToString("0.##", culture);

        System.Diagnostics.Debug.WriteLine($"[PdfExportService.Export] Creating PDF document for: {targetPath}");

        try
        {
            Document.Create(container =>
            {
                container.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(30);
                    page.DefaultTextStyle(TextStyle.Default.FontSize(11));

                    page.Header().Text("Urlaubsantrag").FontSize(16).Bold().AlignCenter();

                    page.Content().Column(col =>
                    {
                        col.Spacing(10);

                        col.Item().Grid(grid =>
                        {
                            grid.Columns(2);
                            grid.Item().Text("Name").SemiBold();
                            grid.Item().Text(BuildDisplayName(settings));
                            grid.Item().Text("Abteilung").SemiBold();
                            grid.Item().Text(settings.Abteilung);
                            grid.Item().Text("Klasse").SemiBold();
                            grid.Item().Text(settings.Klasse);
                            grid.Item().Text("Zeitraum").SemiBold();
                            grid.Item().Text($"vom {request.StartDate:dd.MM.yyyy} bis {request.EndDate:dd.MM.yyyy}");
                            grid.Item().Text("Halbe Tage").SemiBold();
                            grid.Item().Text(BuildHalfDayText(request));
                        });

                        col.Item().Border(1).Padding(8).Column(inner =>
                        {
                            inner.Spacing(6);
                            inner.Item().Text("Zustehender Erholungsurlaub im Urlaubsjahr:").Bold();
                            inner.Item().Text(settings.Jahresurlaub.ToString("0.##", culture) + " Tag/e");
                            inner.Item().Text("davon bereits erhalten:").Bold();
                            inner.Item().Text(alreadyFormatted + " Tag/e");
                            inner.Item().Text("mit dieser Meldung beantragt:").Bold();
                            inner.Item().Text(totalFormatted + " Tag/e");
                            inner.Item().Text("Resturlaub:").Bold();
                            inner.Item().Text(remainingFormatted + " Tag/e");
                            inner.Item().Text("Halbe Tage während des Urlaubs:").Bold();
                            inner.Item().Text(BuildHalfDayText(request));
                        });

                        col.Item().Border(1).Padding(8).Column(inner =>
                        {
                            inner.Spacing(4);
                            inner.Item().Text("Ich beabsichtige vom ... bis ... Erholungsurlaub zu nehmen.");
                            inner.Item().Text("Bei Ablehnung, Grund:");
                            inner.Item().Text(" ").LineHeight(1.5f);
                            inner.Item().Text(" ").LineHeight(1.5f);
                        });

                        col.Item().Grid(grid =>
                        {
                            grid.Columns(2);
                            grid.Item().Column(c =>
                            {
                                c.Item().Text("Datum").Bold();
                                c.Item().Text("____________________");
                            });
                            grid.Item().Column(c =>
                            {
                                c.Item().Text("Unterschrift").Bold();
                                c.Item().Text("____________________");
                            });
                        });

                        col.Item().Grid(grid =>
                        {
                            grid.Columns(2);
                            grid.Item().Column(c =>
                            {
                                c.Item().Text("genehmigt / bearbeitet").Bold();
                                c.Item().Text("____________________");
                            });
                            grid.Item().Column(c =>
                            {
                                c.Item().Text("Personalabteilung").Bold();
                                c.Item().Text("____________________");
                            });
                        });

                        col.Item().Border(1).Padding(8).Column(inner =>
                        {
                            inner.Spacing(4);
                            inner.Item().Text("Tagesaufschlüsselung").SemiBold();
                            foreach (var day in calculation.PerDay)
                            {
                                inner.Item().Row(row =>
                                {
                                    row.RelativeItem().Text($"{day.Date:dd.MM.yyyy} ({day.DayOfWeek})");
                                    row.RelativeItem().Text(BuildFlags(day));
                                    row.ConstantItem(50).Text(day.CountedValue.ToString("0.##", culture)).AlignRight();
                                });
                            }
                        });
                    });

                    page.Footer().AlignCenter().Text("Urlaubsschein Vorlage angepasst an CSV/XLSM Felder").FontSize(9).Italic();
                });
            }).GeneratePdf(targetPath);

            System.Diagnostics.Debug.WriteLine($"[PdfExportService.Export] PDF successfully generated at: {targetPath}");
            return targetPath;
        }
        catch (AggregateException aggEx)
        {
            // Unwrap AggregateException and find the innermost exception
            var flattened = aggEx.Flatten();
            var innermostEx = GetInnermostException(flattened);
            
            // Log the complete AggregateException with all inner exceptions
            var logPath = _exceptionLogger.Write("PDF Export", aggEx);
            System.Diagnostics.Debug.WriteLine($"[PdfExportService.Export] AggregateException occurred: {innermostEx.GetType().Name}: {innermostEx.Message}");
            
            // Construct a user-friendly German error message with details from innermost exception
            var errorMsg = $"PDF-Export fehlgeschlagen: {innermostEx.GetType().Name}: {innermostEx.Message}. " +
                          $"Details siehe Log: {logPath}";
            
            throw new Exception(errorMsg, innermostEx);
        }
        catch (Exception ex)
        {
            // Handle any other exception type (including QuestPDF.Infrastructure.PdfException)
            var logPath = _exceptionLogger.Write("PDF Export", ex);
            System.Diagnostics.Debug.WriteLine($"[PdfExportService.Export] Exception occurred: {ex.GetType().Name}: {ex.Message}");
            
            // Construct a German error message with exception details and log path
            var errorMsg = $"PDF-Export fehlgeschlagen: {ex.GetType().Name}: {ex.Message}. " +
                          $"Details siehe Log: {logPath}";
            
            throw new Exception(errorMsg, ex);
        }
    }

    /// <summary>
    /// Recursively finds the innermost (deepest) exception in an exception chain.
    /// Traverses InnerException until reaching a null value.
    /// </summary>
    private static Exception GetInnermostException(Exception ex)
    {
        while (ex.InnerException != null)
        {
            ex = ex.InnerException;
        }
        return ex;
    }

    private static string BuildDisplayName(AppSettings settings)
    {
        // Prefer Vorname + Nachname if available, fallback to Name for old settings
        if (!string.IsNullOrWhiteSpace(settings.Vorname))
        {
            return $"{settings.Vorname} {settings.Name}".Trim();
        }
        return settings.Name;
    }

    private static string BuildHalfDayText(VacationRequest request)
    {
        if (request.StartHalfDay && request.EndHalfDay)
        {
            return "Halbtag am Anfang und Ende";
        }
        if (request.StartHalfDay)
        {
            return "Halbtag am Anfang";
        }
        if (request.EndHalfDay)
        {
            return "Halbtag am Ende";
        }
        return "Keine";
    }

    private static string BuildFlags(DayEvaluation day)
    {
        var flags = new List<string>();
        if (!day.IsWorkday) flags.Add("Wochenende/Nicht-Arbeitstag");
        if (day.IsPublicHoliday) flags.Add("Feiertag");
        if (day.IsSchoolHoliday) flags.Add("Schulferien");
        if (day.VocationalSchool == VocationalSchoolDayType.Full) flags.Add("Berufsschule voll");
        if (day.VocationalSchool == VocationalSchoolDayType.Half) flags.Add("Berufsschule halb");
        return string.Join(", ", flags);
    }

    private static string BuildVersionedPath(string exportDir, string baseName)
    {
        var version = 1;
        while (true)
        {
            var candidate = Path.Combine(exportDir, $"{baseName}_v{version}.pdf");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
            version++;
        }
    }
}
