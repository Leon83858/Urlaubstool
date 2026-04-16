using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Urlaubstool.Domain;
using Urlaubstool.Infrastructure.Settings;

namespace Urlaubstool.App.ViewModels;

/// <summary>
/// ViewModel for the Setup Wizard shown on first run.
/// Collects personal information (Vorname, Nachname, etc.) and vacation configuration.
/// </summary>
public class SetupWizardViewModel : ViewModelBase
{
    private string _vorname = string.Empty;
    private string _nachname = string.Empty;
    private string _adresse = string.Empty;
    private string _abteilung = string.Empty;
    private string _personalnummer = string.Empty;
    private string _klasse = string.Empty;
    private string _jahresurlaub = "30";
    private bool _studentActive;
    private string? _selectedBundesland;
    private string? _validationError;

    // Workdays selection
    private bool _monday = true;
    private bool _tuesday = true;
    private bool _wednesday = true;
    private bool _thursday = true;
    private bool _friday = true;
    private bool _saturday;
    private bool _sunday;

    // Vocational school settings per weekday
    private Dictionary<DayOfWeek, VocationalSchoolDayType> _vocationalSchool;

    public SetupWizardViewModel()
    {
        _vocationalSchool = Enum.GetValues<DayOfWeek>()
            .ToDictionary(d => d, _ => VocationalSchoolDayType.None);
        
        Bundeslaender = new List<string>
        {
            "BW", "BY", "BE", "BB", "HB", "HH", "HE", "MV",
            "NI", "NW", "RP", "SL", "SN", "ST", "SH", "TH"
        };
    }

    public string Vorname
    {
        get => _vorname;
        set => SetProperty(ref _vorname, value);
    }

    public string Nachname
    {
        get => _nachname;
        set => SetProperty(ref _nachname, value);
    }

    public string Adresse
    {
        get => _adresse;
        set => SetProperty(ref _adresse, value);
    }

    public string Abteilung
    {
        get => _abteilung;
        set => SetProperty(ref _abteilung, value);
    }

    public string Personalnummer
    {
        get => _personalnummer;
        set => SetProperty(ref _personalnummer, value);
    }

    public string Klasse
    {
        get => _klasse;
        set => SetProperty(ref _klasse, value);
    }

    public string Jahresurlaub
    {
        get => _jahresurlaub;
        set => SetProperty(ref _jahresurlaub, value);
    }

    public bool Monday
    {
        get => _monday;
        set => SetProperty(ref _monday, value);
    }

    public bool Tuesday
    {
        get => _tuesday;
        set => SetProperty(ref _tuesday, value);
    }

    public bool Wednesday
    {
        get => _wednesday;
        set => SetProperty(ref _wednesday, value);
    }

    public bool Thursday
    {
        get => _thursday;
        set => SetProperty(ref _thursday, value);
    }

    public bool Friday
    {
        get => _friday;
        set => SetProperty(ref _friday, value);
    }

    public bool Saturday
    {
        get => _saturday;
        set => SetProperty(ref _saturday, value);
    }

    public bool Sunday
    {
        get => _sunday;
        set => SetProperty(ref _sunday, value);
    }

    public bool StudentActive
    {
        get => _studentActive;
        set => SetProperty(ref _studentActive, value);
    }

    public string? SelectedBundesland
    {
        get => _selectedBundesland;
        set => SetProperty(ref _selectedBundesland, value);
    }

    public string? ValidationError
    {
        get => _validationError;
        set => SetProperty(ref _validationError, value);
    }

    public List<string> Bundeslaender { get; }

    public Dictionary<DayOfWeek, VocationalSchoolDayType> VocationalSchool => _vocationalSchool;

    /// <summary>
    /// Validates the input and returns true if valid.
    /// Uses robust decimal parsing with German and invariant culture support.
    /// </summary>
    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Vorname))
        {
            ValidationError = "Bitte geben Sie einen Vornamen ein.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Nachname))
        {
            ValidationError = "Bitte geben Sie einen Nachnamen ein.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Adresse))
        {
            ValidationError = "Bitte geben Sie eine Adresse ein.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(Abteilung))
        {
            ValidationError = "Bitte geben Sie eine Abteilung ein.";
            return false;
        }

        // Robust decimal parsing - try German culture first, then invariant
        if (!decimal.TryParse(Jahresurlaub, NumberStyles.Number, new CultureInfo("de-DE"), out var urlaub) &&
            !decimal.TryParse(Jahresurlaub, NumberStyles.Number, CultureInfo.InvariantCulture, out urlaub))
        {
            ValidationError = "Bitte geben Sie einen gültigen Jahresurlaub ein (z.B. 30 oder 28,5).";
            return false;
        }

        if (urlaub < 0)
        {
            ValidationError = "Jahresurlaub muss eine positive Zahl sein.";
            return false;
        }

        var workdays = GetSelectedWorkdays();
        if (workdays.Count == 0)
        {
            ValidationError = "Bitte wählen Sie mindestens einen Arbeitstag aus.";
            return false;
        }

        if (StudentActive && string.IsNullOrWhiteSpace(SelectedBundesland))
        {
            ValidationError = "Bitte wählen Sie ein Bundesland aus für den Schülermodus.";
            return false;
        }

        ValidationError = null;
        return true;
    }

    /// <summary>
    /// Creates AppSettings from the current input.
    /// Automatically sets Name as "Vorname Nachname" for backward compatibility.
    /// </summary>
    public AppSettings CreateSettings()
    {
        // Parse Jahresurlaub with German culture first, fallback to invariant
        decimal jahresurlaub = 0;
        if (!decimal.TryParse(Jahresurlaub, NumberStyles.Number, new CultureInfo("de-DE"), out jahresurlaub))
        {
            decimal.TryParse(Jahresurlaub, NumberStyles.Number, CultureInfo.InvariantCulture, out jahresurlaub);
        }

        return new AppSettings
        {
            Name = $"{Vorname} {Nachname}".Trim(), // Computed for backward compatibility
            Vorname = Vorname.Trim(),
            Nachname = Nachname.Trim(),
            Adresse = Adresse,
            Abteilung = Abteilung,
            Personalnummer = Personalnummer,
            Klasse = Klasse,
            Jahresurlaub = jahresurlaub,
            Workdays = GetSelectedWorkdays(),
            StudentActive = StudentActive,
            Bundesland = SelectedBundesland,
            VocationalSchool = new Dictionary<DayOfWeek, VocationalSchoolDayType>(_vocationalSchool)
        };
    }

    private HashSet<DayOfWeek> GetSelectedWorkdays()
    {
        var workdays = new HashSet<DayOfWeek>();
        if (Monday) workdays.Add(DayOfWeek.Monday);
        if (Tuesday) workdays.Add(DayOfWeek.Tuesday);
        if (Wednesday) workdays.Add(DayOfWeek.Wednesday);
        if (Thursday) workdays.Add(DayOfWeek.Thursday);
        if (Friday) workdays.Add(DayOfWeek.Friday);
        if (Saturday) workdays.Add(DayOfWeek.Saturday);
        if (Sunday) workdays.Add(DayOfWeek.Sunday);
        return workdays;
    }
}
