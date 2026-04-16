using System.Collections.Immutable;

namespace Urlaubstool.Domain;

/// <summary>
/// Performs deterministic vacation day calculation respecting workdays, holidays, and student rules.
/// </summary>
public sealed class VacationCalculator
{
    private readonly IPublicHolidayProvider _publicHolidayProvider;
    private readonly ISchoolHolidayProvider _schoolHolidayProvider;

    public VacationCalculator(IPublicHolidayProvider publicHolidayProvider, ISchoolHolidayProvider schoolHolidayProvider)
    {
        _publicHolidayProvider = publicHolidayProvider;
        _schoolHolidayProvider = schoolHolidayProvider;
    }

    public CalculationResult Calculate(VacationRequest request)
    {
        var errors = new List<string>();
        var warnings = new List<string>();
        var perDay = new List<DayEvaluation>();

        if (request.StartDate > request.EndDate)
        {
            errors.Add("Das Startdatum darf nicht nach dem Enddatum liegen.");
            return new CalculationResult(0m, perDay, errors, warnings);
        }

        if (request.StartDate.Year != request.EndDate.Year)
        {
            errors.Add("Zeitraum überschreitet das Kalenderjahr. Bitte zwei separate Anträge erstellen.");
        }

        if (request.WorkdaysOfWeek.Count == 0)
        {
            errors.Add("Es muss mindestens ein Arbeitstag pro Woche ausgewählt sein.");
        }

        if (request.AnnualEntitlement <= 0)
        {
            errors.Add("Der Jahresurlaub muss größer als 0 sein.");
        }

        if (!string.IsNullOrWhiteSpace(request.State) && !Bundeslaender.Codes.Contains(request.State))
        {
            errors.Add("Unbekanntes Bundesland.");
        }

        if (request.Student.Active)
        {
            if (string.IsNullOrWhiteSpace(request.Student.State))
            {
                errors.Add("Bitte ein Bundesland auswählen, wenn Schülerparameter aktiv sind.");
            }
            else if (!Bundeslaender.Codes.Contains(request.Student.State))
            {
                errors.Add("Unbekanntes Bundesland für Schülerparameter.");
            }
        }

        // Validate half-day rules before processing dates
        if (request.StartHalfDay && request.EndHalfDay && request.StartDate == request.EndDate)
        {
            errors.Add("Es ist nicht möglich, einen einzelnen Tag als Halbtag am Anfang UND Ende zu beantragen.");
        }

        var year = request.StartDate.Year;
        if (request.EndDate.Year != year)
        {
            // Already added above; keep single error to avoid duplication.
        }

        // Track full school days to aggregate error messages.
        var fullSchoolDayHit = false;
        var fullSchoolDayDates = new List<string>();

        for (var date = request.StartDate; date <= request.EndDate; date = date.AddDays(1))
        {
            var dayMessages = new List<string>();
            var counted = 1m;
            var isWorkday = request.WorkdaysOfWeek.Contains(date.DayOfWeek);
            var isPublicHoliday = false;
            var isSchoolHoliday = false;
            var vocational = VocationalSchoolDayType.None;

            // Rule 1: non-workdays count zero.
            if (!isWorkday)
            {
                counted = 0m;
                dayMessages.Add("Kein Arbeitstag.");
            }

            // Rule 2: public holidays override workdays.
            var stateForHoliday = request.State ?? request.Student.State;
            if (!string.IsNullOrWhiteSpace(stateForHoliday) && _publicHolidayProvider.IsPublicHoliday(date, stateForHoliday!))
            {
                isPublicHoliday = true;
                counted = 0m;
                dayMessages.Add("Feiertag im ausgewählten Bundesland.");
            }

            // Rule 2.5: AZA-Tage (Arbeitszeitausgleich/Überstundenabbau) count zero.
            if (request.AzaDates.Contains(date))
            {
                counted = 0m;
                dayMessages.Add("AZA-Tag (Arbeitszeitausgleich) - kein Urlaubstag.");
            }

            // Rule 3: student mode handling.
            // Ignored if it's already a public holiday (no school anyway).
            if (!isPublicHoliday && request.Student.Active && request.Student.State is { } state)
            {
                isSchoolHoliday = _schoolHolidayProvider.IsSchoolHoliday(date, state);
                if (!isSchoolHoliday)
                {
                    if (request.Student.VocationalSchoolDays.TryGetValue(date.DayOfWeek, out var dayType))
                    {
                        vocational = dayType;
                        switch (dayType)
                        {
                            case VocationalSchoolDayType.Full:
                                // Aggregate full school day errors instead of adding individually.
                                fullSchoolDayHit = true;
                                fullSchoolDayDates.Add(date.ToString("dd.MM.yyyy"));
                                counted = 0m;
                                dayMessages.Add("An ganztägigen Schultagen kann kein Urlaub genommen werden.");
                                break;
                            case VocationalSchoolDayType.Half:
                                counted = Math.Min(counted, 0.5m);
                                dayMessages.Add("Berufsschule (halbtägig) begrenzt den Urlaubstag auf 0,5.");
                                break;
                        }
                    }
                }
            }

            // Rule 4: half-day edges only.
            if (request.StartHalfDay && date == request.StartDate)
            {
                counted = Math.Min(counted, 0.5m);
                dayMessages.Add("Halbtag am Startdatum.");
            }

            if (request.EndHalfDay && date == request.EndDate)
            {
                counted = Math.Min(counted, 0.5m);
                dayMessages.Add("Halbtag am Enddatum.");
            }

            perDay.Add(new DayEvaluation(date, date.DayOfWeek, isWorkday, isPublicHoliday, isSchoolHoliday, vocational, counted, dayMessages.ToImmutableArray()));
        }

        // Add aggregated error for full school days.
        if (fullSchoolDayHit)
        {
            errors.Add("An ganztägigen Schultagen kann kein Urlaub genommen werden.");
            if (fullSchoolDayDates.Count > 0)
            {
                errors.Add($"Betroffene Termine: {string.Join(", ", fullSchoolDayDates)}");
            }
        }

        var total = perDay.Sum(d => d.CountedValue);
        var entitlement = EntitlementSnapshot.From(request.AnnualEntitlement, request.AlreadyApprovedThisYear);
        if (total > entitlement.Remaining)
        {
            errors.Add("Die beantragten Tage überschreiten den Resturlaub. Bitte Zeitraum anpassen.");
        }

        return new CalculationResult(total, perDay.ToImmutableArray(), errors.ToImmutableArray(), warnings.ToImmutableArray());
    }
}
