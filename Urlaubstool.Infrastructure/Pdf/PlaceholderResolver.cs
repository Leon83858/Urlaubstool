using System;
using System.Collections.Generic;
using System.Linq;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.Infrastructure.Pdf;

/// <summary>
/// Resolves all PDF template field values from application data.
/// Maps template fields to data from AppSettings, VacationRequest, and CalculationResult.
/// Produces German-formatted output suitable for PDF stamping.
/// </summary>
public class PlaceholderResolver
{
    private readonly AppSettings _settings;
    private readonly VacationRequest _request;
    private readonly CalculationResult _calculation;

    public PlaceholderResolver(AppSettings settings, VacationRequest request, CalculationResult calculation)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _calculation = calculation ?? throw new ArgumentNullException(nameof(calculation));
    }

    /// <summary>
    /// Resolves all template field values for PDF stamping.
    /// Returns a strongly-typed model with German-formatted strings.
    /// </summary>
    public TemplateFieldValues ResolvePlaceholders()
    {
        // Calculate additional values needed for the template
        var remainingDaysAfterRequest = _request.AnnualEntitlement - _request.AlreadyApprovedThisYear - _calculation.TotalDays;
        var halfDayCount = (_request.StartHalfDay ? 0.5m : 0m) + (_request.EndHalfDay ? 0.5m : 0m);

        // Build AZA-Tage text (vocational school days during vacation period)
        var azaTageText = BuildAzaTageText();

        return new TemplateFieldValues
        {
            // Personal information
            Name = _settings.Name ?? string.Empty,
            Vorname = _settings.Vorname ?? string.Empty,
            Nachname = _settings.Nachname ?? string.Empty,
            Adresse = _settings.Adresse ?? string.Empty,
            Abteilung = _settings.Abteilung ?? string.Empty,
            Personalnummer = _settings.Personalnummer ?? string.Empty,

            // Request dates (German format: dd.MM.yyyy)
            StartDatum = _request.StartDate.ToString("dd.MM.yyyy"),
            Enddatum = _request.EndDate.ToString("dd.MM.yyyy"),
            
            // Vacation totals and calculations (German decimal format with comma)
            GesamtVerfuegbarerUrlaub = FormatDecimal(_request.AnnualEntitlement),
            BereitsErhaltenerUrlaub = FormatDecimal(_request.AlreadyApprovedThisYear),
            MitDiesemAntragBeantragt = FormatDecimal(_calculation.TotalDays),
            Resturlaub = FormatDecimal(remainingDaysAfterRequest),
            AnzahlHalbtage = FormatDecimal(halfDayCount),
            
            // AZA-Tage multiline text (vocational school days during vacation)
            AzaTage = azaTageText,
            
            // Application date (today, German format)
            AntragsDatum = DateTime.Now.ToString("dd.MM.yyyy"),
            
            // Signature placeholder (print applicant name or leave blank)
            UnterschriftPlatzhalter = $"{_settings.Vorname} {_settings.Name}".Trim(),
            
            // Administrative fields (left blank for initial submission)
            Genehmigt = string.Empty,
            Bearbeitet = string.Empty,
            Personalabteilung = string.Empty,
            AblehnungGrund = string.Empty
        };
    }

    /// <summary>
    /// Formats a decimal number with German culture (comma as decimal separator).
    /// Shows up to 1 decimal place, omitting ".0" for whole numbers.
    /// Examples: 5 → "5", 5.5 → "5,5", 10.25 → "10,25"
    /// </summary>
    private static string FormatDecimal(decimal value)
    {
        // Format with German culture (comma decimal separator)
        var formatted = value.ToString("0.##", System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        return formatted;
    }

    /// <summary>
    /// Builds the AZA-Tage (vocational school days and AZA days) text for multiline display.
    /// Returns a newline-separated string listing all special non-vacation days in the vacation period.
    /// If no such days exist, returns empty string.
    /// Format: "Mittwoch, 15.05.2025 - Ganztagsschule" (one line per day)
    /// </summary>
    private string BuildAzaTageText()
    {
        var lines = new List<string>();
        var culture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");

        // 1. Collective list of days to report
        // We use a dictionary to ensure we handle dates uniquely if they fall into multiple categories
        // Key: Date, Value: Description
        var specialDays = new Dictionary<DateOnly, string>();

        // Add Vocational School Days
        foreach (var day in _calculation.PerDay.Where(d => d.VocationalSchool != VocationalSchoolDayType.None))
        {
            var typeStr = day.VocationalSchool == VocationalSchoolDayType.Full ? "Ganztagsschule" : "Halbtagsschule";
            specialDays[day.Date] = typeStr;
        }

        // Add AZA Days (Explicit overrides)
        if (_request.AzaDates != null)
        {
            foreach (var date in _request.AzaDates)
            {
                // If already present (e.g. school day), append info or overwrite? 
                // AZA is usually more specific/important as it forces 0 days.
                if (specialDays.ContainsKey(date))
                {
                    specialDays[date] += " / AZA-Tag";
                }
                else
                {
                    specialDays[date] = "AZA-Tag";
                }
            }
        }

        if (specialDays.Count == 0)
        {
            return string.Empty;
        }

        // Sort by date
        foreach (var kvp in specialDays.OrderBy(x => x.Key))
        {
            var date = kvp.Key;
            var description = kvp.Value;
            var dayName = date.ToString("dddd", culture);
            var dateStr = date.ToString("dd.MM.yyyy");
            
            lines.Add($"{dayName}, {dateStr} - {description}");
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Validates that all required fields are populated.
    /// Returns a list of German error messages if any required fields are missing.
    /// </summary>
    public List<string> ValidateRequiredFields()
    {
        var errors = new List<string>();

        System.Diagnostics.Debug.WriteLine($"[PlaceholderResolver.ValidateRequiredFields] Starting validation");
        System.Diagnostics.Debug.WriteLine($"[PlaceholderResolver.ValidateRequiredFields] Vorname: '{_settings.Vorname}', Nachname: '{_settings.Nachname}'");

        if (string.IsNullOrWhiteSpace(_settings.Vorname))
        {
            errors.Add("Vorname ist leer → Bitte geben Sie Ihren Vornamen in den Einstellungen ein");
        }

        if (string.IsNullOrWhiteSpace(_settings.Nachname))
        {
            errors.Add("Nachname ist leer → Bitte geben Sie Ihren Nachnamen in den Einstellungen ein");
        }

        if (string.IsNullOrWhiteSpace(_settings.Adresse))
        {
            errors.Add("Adresse ist leer → Bitte geben Sie Ihre Adresse in den Einstellungen ein");
        }

        if (string.IsNullOrWhiteSpace(_settings.Abteilung))
        {
            errors.Add("Abteilung ist leer → Bitte geben Sie Ihre Abteilung in den Einstellungen ein");
        }

        if (_settings.Jahresurlaub <= 0)
        {
            errors.Add($"Jahresurlaub ist ungültig ({_settings.Jahresurlaub}) → Bitte geben Sie Ihren jährlichen Urlaubsanspruch in den Einstellungen ein");
        }

        if (_settings.Workdays.Count == 0)
        {
            errors.Add("Keine Arbeitstage definiert → Bitte wählen Sie in den Einstellungen Ihre Arbeitstage aus");
        }

        // Personalnummer is optional (some organizations don't use it)

        if (_calculation.HasErrors)
        {
            var calcErrors = string.Join(", ", _calculation.Errors);
            errors.Add($"Die Berechnung enthält Fehler: {calcErrors} → Bitte korrigieren Sie den gewählten Zeitraum");
        }

        if (errors.Count > 0)
        {
            System.Diagnostics.Debug.WriteLine($"[PlaceholderResolver.ValidateRequiredFields] Validation failed with {errors.Count} error(s)");
            foreach (var err in errors)
            {
                System.Diagnostics.Debug.WriteLine($"  - {err}");
            }
        }
        else
        {
            System.Diagnostics.Debug.WriteLine($"[PlaceholderResolver.ValidateRequiredFields] Validation passed");
        }

        return errors;
    }
}

/// <summary>
/// Strongly-typed model containing all resolved field values for PDF stamping.
/// All values are pre-formatted strings ready for display (German formats).
/// </summary>
public class TemplateFieldValues
{
    // Personal Information
    public required string Name { get; init; } // Full name (backward compatibility)
    public required string Vorname { get; init; }
    public required string Nachname { get; init; }
    public required string Adresse { get; init; }
    public required string Abteilung { get; init; }
    public required string Personalnummer { get; init; }

    // Request Date Range
    public required string StartDatum { get; init; }
    public required string Enddatum { get; init; }

    // Vacation Calculations
    public required string GesamtVerfuegbarerUrlaub { get; init; }
    public required string BereitsErhaltenerUrlaub { get; init; }
    public required string MitDiesemAntragBeantragt { get; init; }
    public required string Resturlaub { get; init; }
    public required string AnzahlHalbtage { get; init; }

    // Multiline AZA-Tage section
    public required string AzaTage { get; init; }

    // Signature and Date
    public required string AntragsDatum { get; init; }
    public required string UnterschriftPlatzhalter { get; init; }

    // Administrative fields (typically blank for initial submission)
    public required string Genehmigt { get; init; }
    public required string Bearbeitet { get; init; }
    public required string Personalabteilung { get; init; }
    public required string AblehnungGrund { get; init; }
}
