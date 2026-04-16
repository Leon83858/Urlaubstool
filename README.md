# Urlaubstool

Cross-platform Avalonia desktop app for Urlaubsschein creation, calculation, PDF export, and history management. Uses the same UI engine as RawImporter (Avalonia 11.1.3 with Fluent theme and custom color resources).

## Engine Evidence
- RawImporter uses Avalonia: see [Raw Importer/C# Mac/RawImporterCS.csproj](Raw%20Importer/C%23%20Mac/RawImporterCS.csproj) with `PackageReference Include="Avalonia" Version="11.1.3"` and XAML views [Raw Importer/C# Mac/App.axaml](Raw%20Importer/C%23%20Mac/App.axaml).
- Urlaubstool mirrors this: Avalonia 11.1.3, Fluent theme, and matching color resources in [Urlaubstool.App/Colors.axaml](Urlaubstool.App/Colors.axaml).

## Template Fields
PDF template (`basis/Template.pdf`) contains two identical sections (Original + Kopie) with these fields:
- Personal: Name, Vorname, Adresse (Straße/PLZ), Abteilung, Personalnummer
- Request dates: Start Datum, Enddatum
- Vacation calculations: Gesammt verfügbarer Urlaub, Bereits erhaltener Urlaub, Mit diesem Antrag beantragt, Resturlaub, Anzahl Halbtage
- AZA-Tage: Multiline section listing vocational school days during vacation period
- Signature: Antragsdatum, Unterschrift placeholder
- Administrative: Genehmigt, Bearbeitet, Personalabteilung, Ablehnungsgrund (left blank for initial submission)

## Projects
- `Urlaubstool.App` – Avalonia UI (SetupWizard, MainView, History/Admin, Settings).
- `Urlaubstool.Domain` – Pure calculation, validation, models, holiday interfaces.
- `Urlaubstool.Infrastructure` – Persistence (settings JSON, ledger CSV), offline holiday providers, PDF export (iText7 stamping), paths service.
- `Urlaubstool.Tests` – xUnit + FluentAssertions coverage.

## Build & Run
```bash
cd "/Users/leonpilger/Documents/Apps/ultimate urlaubstool"
dotnet restore
dotnet build -c Release
dotnet test -c Release
dotnet run --project Urlaubstool.App
```

## Settings & Ledger Paths
- macOS: `~/Library/Application Support/Urlaubstool/settings.json` and `~/Library/Application Support/Urlaubstool/ledger.csv`
- Windows: `%APPDATA%\Urlaubstool\settings.json` and `...\ledger.csv`
- PDF exports: `~/Documents/Urlaubstool/Exports/` (versioned filenames `Urlaubsantrag_YYYY-MM-DD_bis_YYYY-MM-DD_vX.pdf`).

## Holiday Data
- Public holidays offline in code (fixed + Easter-based + state-specific + Buß- und Bettag for SN).
- School holidays offline JSON embedded: [Urlaubstool.Infrastructure/Data/school_holidays.json](Urlaubstool.Infrastructure/Data/school_holidays.json), cross-year ranges split during load to cover January correctly.
- Providers: `PublicHolidayProvider`, `SchoolHolidayProvider` (both used by `VacationCalculator`).

## Calculation Rules (implemented in Domain)
- Non-workdays and public holidays count 0.
- **AZA-Tage (Arbeitszeitausgleich/Überstundenabbau)**: User can mark specific dates as AZA days in the vacation request. These days count as 0 vacation days but are included in the period (for compensatory time off).
- Student mode: school holidays override vocational rules; full school day = error, half = max 0.5.
- Half-days only on start/end; otherwise hard error. Year crossing hard error. Remaining days insufficiency hard error.
- Uses `DateOnly` and `decimal` only.

## Features
- **AZA-Tage Selection**: Users can activate an AZA checkbox in the main window to add specific dates for compensatory time off (Arbeitszeitausgleich) within the vacation period. Each AZA day can be selected via DatePicker and will not deduct vacation days.
- Cross-platform compatibility: Windows (win-x64, win-arm64), macOS (osx-arm64, osx-x64)
- PDF export with AcroForm field filling (iText7)
- Vacation ledger with history management
- Settings persistence across platforms

## Persistence
- Settings JSON with schema version, safe-write temp then replace.
- Ledger CSV schema versioned, invariant decimal, retry on file lock.

## PDF Export Architecture
**NO Excel dependency** - Pure PDF stamping approach using iText7:

### Template
- Embedded resource: `Urlaubstool.Infrastructure/Pdf/Template.pdf` (basis/Template.pdf)
- Single-page A4 template with two identical sections: Original (top) and Kopie (bottom)

### Stamping Service
- `PdfTemplateStampExportService` loads embedded template and stamps text at precise coordinates
- Uses `TemplateLayout.TemplatePdf_v1.cs` for field coordinate definitions (X/Y baseline positions)
- All coordinates use PDF coordinate system (origin at bottom-left, Y = baseline for accurate line alignment)
- Text is clipped to MaxWidth; font shrinks if needed, ellipsis added if still too wide
- NO AcroForm fields required - pure coordinate-based rendering

### Field Layout
- `TemplateLayout.Original` defines coordinates for top section
- `TemplateLayout.Kopie` mirrors Original with vertical offset (-421pt)
- Each field specifies: X, Y (baseline), MaxWidth, FontSize, TextAlignment (Left/Right/Center)
- Multiline fields (AZA-Tage) support line wrapping with configurable LineHeight and MaxLines

### Placeholder Resolution
- `PlaceholderResolver` converts AppSettings + VacationRequest + CalculationResult into `TemplateFieldValues`
- German formatting: dates (dd.MM.yyyy), decimals (comma separator)
- AZA-Tage builds multiline text listing vocational school days: "Mittwoch, 15.05.2025 - Ganztagsschule"

### Export Process
1. Validate required fields (Name, Vorname, Adresse, Abteilung)
2. Resolve field values with German formatting
3. Load embedded Template.pdf
4. Stamp text onto both Original and Kopie sections using iText7 Canvas API
5. Save versioned PDF: `Urlaubsantrag_2025-06-01_bis_2025-06-05_v1.pdf`
6. Record in ledger

## Updating Template/Layout
- Template: Replace `Urlaubstool.Infrastructure/Pdf/Template.pdf` and rebuild (embedded resource)
- Coordinates: Edit `TemplateLayout.TemplatePdf_v1.cs` field positions to match new template layout
- Fields: Add new fields to `TemplateFieldValues`, `PlaceholderResolver.ResolvePlaceholders()`, and stamping methods

## Tests Covered
- VacationCalculator: weekend exclusion, public holiday exclusion, student full/half rules, school holidays override, half-day edges, cross-year error, remaining days insufficient, Buß- und Bettag known dates, cross-year school holiday lookup.
- Persistence: settings and ledger roundtrips preserve decimals and selections.

## Runtime Notes
- UI text is German; comments remain English for maintainability.
- App runs fully offline; no network dependencies.
- PDF export requires NO external applications (LibreOffice/Excel/Numbers) - iText7 handles everything.

============================================================
11) EXECUTION, DESK-CHECKS & STABILITY LOOPS (MANDATORY)
============================================================

11.1 Start the application
- Run the app from source:
  - `dotnet run --project Urlaubstool.App`
- The app must open without crashing and must reach the main screen.

11.2 If the app fails to start
If startup fails (crash, exception, build error, missing resource, runtime error):
- Identify the exact root cause using:
  - build output
  - runtime stack trace
  - logs
- Fix the issue permanently in code/config/resources (no hacks, no "ignore errors").
- Then run again:
  - `dotnet build -c Release`
  - `dotnet test -c Release`
  - `dotnet run --project Urlaubstool.App`
Repeat until the app starts successfully.

11.3 Desk-checks and simulations (minimum 5 clean runs each)
After the app starts, perform a thorough "Schreibtischtest" and simulation runs. You must execute each of the following scenarios end-to-end and ensure they complete with:
- no crashes
- no unhandled exceptions
- no validation warnings that indicate incorrect logic
- no incorrect calculations
- no broken UI states (buttons enabled incorrectly, empty lists, wrong totals, etc.)

Run EACH scenario at least 5 times without errors or warnings (5 consecutive clean passes per scenario).
If any run fails, fix the issue and restart the count for that scenario.

Scenario A — Basic employee mode (no student parameters)
1) Disable Schülerparameter
2) Configure Workdays = Mo–Fr, Entitlement = 30
3) Create a request for a normal Mon–Fri week (no holidays)
4) Verify total days = 5
5) Export PDF succeeds
6) Entry appears in history correctly
Repeat until 5 consecutive clean passes.

Scenario B — Public holiday exclusion
1) Use a known nationwide holiday (e.g., 03.10) inside a range
2) Verify holiday is counted as 0
3) Export PDF succeeds and shows correct totals
Repeat until 5 consecutive clean passes.

Scenario C — Student mode, full school day blocks
1) Enable Schülerparameter and set at least one weekday to "Voll"
2) Choose a range that includes that weekday outside school holidays
3) Verify the calculator returns a hard error and Export is blocked
4) Verify the error message is German and clear
Repeat until 5 consecutive clean passes.

Scenario D — Student mode, half school day caps
1) Set at least one weekday to "Halb"
2) Choose a range that includes that weekday outside school holidays
3) Verify that day counts max 0.5, and totals reflect it
Repeat until 5 consecutive clean passes.

Scenario E — Half-day edge rule
1) Create a multi-day range
2) Apply StartHalfDay and/or EndHalfDay
3) Verify only edges are capped and internal days are not affected
4) Try an invalid half-day configuration and confirm it becomes a hard error
Repeat until 5 consecutive clean passes.

Scenario F — Year boundary error
1) Create a range that crosses 31.12 -> 01.01
2) Verify hard error and Export blocked
Repeat until 5 consecutive clean passes.

Scenario G — History workflow
1) Create/export a request
2) Approve it in History
3) Verify remaining days reduced correctly
4) Reject another request and verify remaining days are not reduced (and credit-back works if reservation exists)
Repeat until 5 consecutive clean passes.

11.4 After fixes, always re-verify full build/test/run
Every time you apply a fix during these loops, you must run:
- `dotnet build -c Release`
- `dotnet test -c Release`
- `dotnet run --project Urlaubstool.App`

11.5 Stop condition (success)
You may stop only when:
- The app starts successfully
- All scenarios A–G have achieved 5 consecutive clean passes
- `dotnet test -c Release` passes completely

No shortcuts. No TODOs. No "should work" claims without actually running.
