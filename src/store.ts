import type { CalendarDay } from "./calendar.ts";
import { NO_VOCATIONAL_SCHOOL, type SettingsV2 } from "./settings.ts";
import type { DraftRequest, RequestRecord, RequestStatus } from "./requests.ts";
import type { YearSummary } from "./summary.ts";

/** Aufruf eines Desktop-Befehls; in der App ist das `invoke` von Tauri. */
export type Call = <T>(command: string, args?: Record<string, unknown>) => Promise<T>;
/** Datenstand der importierten Schulferien (REQ-HOL-003). */
export type SchoolHolidayStatus = {
  federalState: string;
  fileName: string;
  importedAtUtc: string;
  latestEventStampUtc: string | null;
  breakCount: number;
  firstDate: string;
  lastDate: string;
  sourceUrl: string;
};
export type CalendarStatus = { publicHolidaySource: string; schoolHolidays: SchoolHolidayStatus | null };
export type DesktopState = { settings: SettingsV2 | null; requests: RequestRecord[]; deletedRequests: RequestRecord[]; calendar?: CalendarStatus };
export type RestoreResult = { state: DesktopState; safetyBackup: string };
export type StoreResult<T> = { ok: true; value: T } | { ok: false; error: string };

async function attempt<T>(run: () => Promise<T>): Promise<StoreResult<T>> {
  try {
    return { ok: true, value: await run() };
  } catch (error) {
    const text = error instanceof Error ? error.message : String(error);
    return { ok: false, error: text || "Unbekannter Fehler beim Zugriff auf die lokalen Daten." };
  }
}

export type MigrationIssue = { recordNumber: number; kind: string; message: string };
export type MigrationPreview = {
  fileName: string;
  format: string;
  sourceSha256: string;
  recordsRead: number;
  recordsReady: number;
  recordsSkipped: number;
  recordsFailed: number;
  issues: MigrationIssue[];
  issuesTruncated: boolean;
  canImport: boolean;
  needsConfirmation: boolean;
  replacesSettings: boolean;
};
export type MigrationSelection = { path: string; preview: MigrationPreview };
export type MigrationResult = {
  fileName: string;
  sourceBackup: string;
  databaseBackup: string;
  alreadyImported: boolean;
  settingsImported: boolean;
  requestsImported: number;
  statusEventsImported: number;
  exportsImported: number;
  reconciliation: {
    years: { year: number; sourceRequests: number; importedRequests: number; sourceHalfUnits: number; importedHalfUnits: number; sourceApprovedHalfUnits: number; importedApprovedHalfUnits: number }[];
    differences: string[];
  };
  state: DesktopState;
};

export type DiagnosticEntry = { timeUtc: string; level: "info" | "warn" | "error"; context: string; message: string };
export type DiagnosticsInfo = {
  version: string;
  os: string;
  arch: string;
  logFile: string;
  databaseFile: string;
  databaseStatus: string;
  recentErrors: DiagnosticEntry[];
};

function errorText(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

/**
 * Meldet jeden fehlgeschlagenen Backend-Aufruf mit Befehlsname und Fehlertext an das
 * Diagnoseprotokoll (REQ-DIAG-001). Eingabedaten werden nie mitgeschickt.
 */
export function withErrorLog(call: Call): Call {
  return async <T>(command: string, args?: Record<string, unknown>): Promise<T> => {
    try {
      return await call<T>(command, args);
    } catch (error) {
      if (command !== "record_error") {
        void call("record_error", { context: command, message: errorText(error) }).catch(() => undefined);
      }
      throw error;
    }
  };
}

/** Datenzugriff der Desktop-App: Berechnung, Prüfung und Speicherung liegen im Rust-Backend. */
export function createDesktopStore(rawCall: Call) {
  const call = withErrorLog(rawCall);
  return {
    load: () => attempt(() => call<DesktopState>("load_state")),
    saveSettings: (settings: SettingsV2) => attempt(() => call<SettingsV2>("save_settings", {
      settings: {
        firstName: settings.firstName,
        lastName: settings.lastName,
        address: settings.address ?? "",
        department: settings.department,
        personnelNumber: settings.personnelNumber ?? "",
        className: settings.className,
        annualEntitlementHalfUnits: settings.annualEntitlementHalfUnits,
        federalState: settings.federalState,
        workdays: [...settings.workdays],
        includeOptionalHolidays: settings.includeOptionalHolidays,
        studentMode: settings.studentMode ?? false,
        vocationalSchoolWeek: [...(settings.vocationalSchoolWeek ?? NO_VOCATIONAL_SCHOOL)],
      },
    })),
    createRequest: (draft: DraftRequest, expectedHalfUnits: number, requestId: string, exportPath?: string) =>
      attempt(() => call<RequestRecord>("create_request", {
        input: {
          requestId,
          startDate: draft.startDate,
          endDate: draft.endDate,
          startHalfDay: draft.startHalfDay,
          endHalfDay: draft.endHalfDay,
          azaDates: [...draft.azaDates],
          expectedHalfUnits,
          ...(exportPath ? { exportPath } : {}),
        },
      })),
    changeStatus: (requestId: string, status: RequestStatus, rejectionReason?: string) =>
      attempt(() => call<RequestRecord>("change_request_status", { requestId, status, rejectionReason: rejectionReason?.trim() || null })),
    /** `null` im Ergebnis bedeutet: Speichern-Dialog abgebrochen. */
    exportRequest: (requestId: string, applicationDate: string) =>
      attempt(() => call<RequestRecord | null>("export_request_pdf", { requestId, applicationDate })),
    createBackup: (today: string) => attempt(() => call<string | null>("create_backup", { today })),
    restoreBackup: () => attempt(() => call<RestoreResult | null>("restore_backup")),
    /** Kalenderfakten eines Jahres (Feiertage, Ferien, Berufsschule) aus dem Backend. */
    /** Dateiauswahl und Vorschau einer Altdatei; `null` bedeutet abgebrochen. */
    previewMigration: () => attempt(() => call<MigrationSelection | null>("preview_migration")),
    importMigration: (selection: MigrationSelection, confirmSkipped: boolean) =>
      attempt(() => call<MigrationResult>("import_migration", { path: selection.path, sourceSha256: selection.preview.sourceSha256, confirmSkipped })),
    /** Bericht des letzten Imports speichern; `null` bedeutet: Dialog abgebrochen. */
    exportMigrationReport: (today: string) => attempt(() => call<string | null>("export_migration_report", { today })),
    diagnosticsInfo: () => attempt(() => call<DiagnosticsInfo>("diagnostics_info")),
    /** `null` im Ergebnis bedeutet: Speichern-Dialog abgebrochen. */
    exportDiagnostics: (today: string) => attempt(() => call<string | null>("export_diagnostics", { today })),
    /** Fehler der Oberfläche selbst (z. B. Absturz einer Ansicht) protokollieren. */
    recordError: (context: string, message: string) => { void rawCall("record_error", { context, message }).catch(() => undefined); },
    /** Summen und Anträge eines Jahres, berechnet im Backend (REQ-ARCH-002). */
    yearSummary: (year: number) => attempt(() => call<YearSummary>("year_summary", { year })),
    calendarYear: (year: number) => attempt(() => call<CalendarDay[]>("calendar_year", { year })),
    /** Öffnet die Dateiauswahl für eine KMK-ICS-Datei; `null` bedeutet abgebrochen. */
    importSchoolHolidays: () => attempt(() => call<CalendarStatus | null>("import_school_holidays")),
  };
}

/** Ersetzt einen geänderten Antrag in der Liste; gelöschte Anträge verschwinden daraus. */
export function applyRequestUpdate(requests: readonly RequestRecord[], updated: RequestRecord): RequestRecord[] {
  if (updated.status === "Gelöscht") return requests.filter((request) => request.id !== updated.id);
  return requests.some((request) => request.id === updated.id)
    ? requests.map((request) => (request.id === updated.id ? updated : request))
    : [updated, ...requests];
}

/** Hält die Liste gelöschter Anträge passend zu einer Änderung aktuell. */
export function applyDeletedUpdate(deleted: readonly RequestRecord[], updated: RequestRecord): RequestRecord[] {
  const others = deleted.filter((request) => request.id !== updated.id);
  return updated.status === "Gelöscht" ? [updated, ...others] : others;
}
