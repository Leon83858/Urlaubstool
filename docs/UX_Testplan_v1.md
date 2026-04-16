# UX Testplan v1

Manual smoke tests and checklist to verify implemented UX improvements.

Prerequisites
- Build the app in Debug: `dotnet build Urlaubstool.App/Urlaubstool.App.csproj -c Debug`
- Run the app: `dotnet run -c Debug -p Urlaubstool.App/Urlaubstool.App.csproj`

Core checks (A → G)

A) Live Calculation (no "Neu berechnen" button)
- Expectation: No "Neu berechnen" button in the UI.
- Test: Change `Von` (StartDate), `Bis` (EndDate) and toggle half-day checkboxes.
  - Result: Summary numbers (`Beantragt`, `Resturlaub`) update immediately.

B) Year handling
- Expectation: No editable year field in request form. `Year` is derived from StartDate.
- Test: Change StartDate crossing year boundary and verify history/approved-day calculations reference the correct year.

C) Breakdown (Tagesaufschlüsselung)
- Expectation: Collapsible section shows per-day rows.
- Test: Open the expander and verify each date shows DayOfWeek, Days, Badges (Feiertag, Schulferien etc.).

D) Field-near Validation + Focus
- Expectation: Field errors appear directly under controls; Export focuses the first invalid field.
- Tests:
  - Set EndDate < StartDate. Error should appear under EndDate. Attempt to export -> focus on EndDate and message shown.
  - Remove the invalid condition and confirm error clears.

E) AZA UX
- Expectation: Single DatePicker + Add button; chips show added dates; duplicates prevented; out-of-range prevented/auto-removed.
- Tests:
  - Add an AZA date within the range -> chip appears sorted.
  - Add the same date again -> blocked with an info message.
  - Add an AZA date outside the current range -> blocked with a message.
  - Change Start/End so some AZA dates become out-of-range -> they get removed and an informational message appears.

F) History safer actions
- Expectation: `Reject` action present; `Delete` requires confirmation.
- Tests:
  - Click `✖` (Reject) on an exported request -> a prompt appears for reason; entering a reason marks request as Rejected.
  - Click `✗` (Delete) -> confirmation dialog appears; cancelling keeps the entry; confirming deletes (or marks deleted).
  - Confirm that Archived entries cannot be Approved/Rejected.

G) Export UX
- Expectation: Export disables inputs (IsBusy), shows short status text "Exportiere…"; on success a dialog appears with `PDF öffnen` and `Ordner öffnen` buttons.
- Tests:
  - Start export: observe Export button disabled and status text shown.
  - On successful export: result dialog shows; `PDF öffnen` opens the PDF (platform action), `Ordner öffnen` opens the folder.
  - On export failure (e.g., invalid export path): a clear message is shown and no crash occurs.

Regression checks (quick)
- Request spanning only weekdays -> day-sum equals expected.
- Half-day in start or end reduces day count by 0.5 appropriately.
- AZA dates are excluded from the requested-days total.

If anything fails
- Note exact repro steps and replicate locally.
- Inspect application console logs for binding errors or exceptions.
- Fix and repeat build → test loop.

---

If you want, I can run further automated checks or create simple unit tests for the new ViewModel methods (`AddAzaDate`, `CleanAzaDays`, field validation, breakdown generation`).
