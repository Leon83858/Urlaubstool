import { Component, useEffect, useState, type CSSProperties, type ReactNode } from "react";
import { invoke, isTauri } from "@tauri-apps/api/core";
import { canDelete, canExport, canTransition, filterAndSortHistory, restoreTarget, unarchiveTarget } from "./history";
import { NO_VOCATIONAL_SCHOOL, readVersionedSettings, writeLocalValue, type SettingsV2 as Settings } from "./settings";
import { describeDay, type DayDetail } from "./day-details";
import { addAzaDate, retainAzaDates } from "./aza";
import { formatHalfUnits, parseHalfUnits } from "./half-units";
import { appendStatusEvent, approvedHalfUnits, countsAsApproved, createRequestRecord, readVersionedRequests, type DraftRequest, type RequestRecord, type RequestStatus } from "./requests";
import { runPdfExport, type PdfReceipt } from "./pdf-flow";
import { formatGermanDate, parseGermanDate } from "./dates";
import { THEME_KEY, THEME_OPTIONS, parseThemePreference, resolveTheme, type Theme, type ThemePreference } from "./theme";
import { CALENDAR_LEGEND, DEFAULT_CALENDAR_COLORS, DEFAULT_DARK_CALENDAR_COLORS, azaDatesOf, dayCategories, describeCalendarDay, parseCalendarColors, requestMarksByDate, type CalendarColors, type CalendarDay } from "./calendar";
import { localYearSummary, type YearSummary } from "./summary";
import { applyDeletedUpdate, applyRequestUpdate, createDesktopStore, type DesktopState, type CalendarStatus, type DiagnosticsInfo, type MigrationSelection, type StoreResult } from "./store";

type Section = "Übersicht" | "Planung" | "Kalender" | "Historie" | "Einstellungen" | "Diagnose";
type CalculationResult = {
  requestedHalfUnits: number;
  annualEntitlementHalfUnits: number;
  alreadyApprovedHalfUnits: number;
  remainingBeforeHalfUnits: number;
  remainingAfterHalfUnits: number;
  errors: string[];
  warnings: string[];
  details: DayDetail[];
};

const sections: Section[] = ["Übersicht", "Planung", "Kalender", "Historie", "Einstellungen", "Diagnose"];
const SETTINGS_KEY = "urlaubstool.settings.v2";
const LEGACY_SETTINGS_KEY = "urlaubstool.settings.v1";
const REQUESTS_KEY = "urlaubstool.requests.v2";
const LEGACY_REQUESTS_KEY = "urlaubstool.requests.v1";
const DEFAULT_WORKDAYS = [true, true, true, true, true, false, false];

const defaultSettings: Settings = {
  firstName: "",
  lastName: "",
  address: "",
  department: "",
  personnelNumber: "",
  className: "",
  annualEntitlementHalfUnits: 60,
  federalState: "Nordrhein-Westfalen",
  workdays: DEFAULT_WORKDAYS,
  includeOptionalHolidays: false,
};

const stateHolidayCodes: Record<string, string[]> = {
  "Baden-Württemberg": ["BW"],
  Bayern: ["BY"],
  Berlin: ["BE"],
  Brandenburg: ["BB"],
  Bremen: ["HB"],
  Hamburg: ["HH"],
  Hessen: ["HE"],
  "Mecklenburg-Vorpommern": ["MV"],
  Niedersachsen: ["NI"],
  "Nordrhein-Westfalen": ["NW", "NRW"],
  "Rheinland-Pfalz": ["RP"],
  Saarland: ["SL"],
  Sachsen: ["SN"],
  "Sachsen-Anhalt": ["ST"],
  "Schleswig-Holstein": ["SH"],
  Thüringen: ["TH"],
};

const desktopStore = createDesktopStore(invoke);
const LOADING_MESSAGE = "Lokale Daten werden geladen.";

function App() {
  const [section, setSection] = useState<Section>("Übersicht");
  const [version, setVersion] = useState("Web-Vorschau");
  // In der Desktop-App liegen alle Daten in der lokalen Datenbank; der
  // Browser-Speicher wird nur noch von der Web-Vorschau verwendet.
  const [desktop] = useState(() => isTauri());
  const [desktopReady, setDesktopReady] = useState(!desktop);
  const [desktopLoadError, setDesktopLoadError] = useState<string | null>(null);
  const [desktopError, setDesktopError] = useState<string | null>(null);
  const [loadedSettings] = useState(() => desktop ? { settings: defaultSettings, error: false } : loadSettings());
  const [settings, setSettings] = useState<Settings>(loadedSettings.settings);
  const [loadedRequests] = useState<{ requests: RequestRecord[]; error: string | null }>(() => desktop ? { requests: [], error: null } : loadRequests());
  const [requests, setRequests] = useState<RequestRecord[]>(loadedRequests.requests);
  const [deletedRequests, setDeletedRequests] = useState<RequestRecord[]>([]);
  const [calendarStatus, setCalendarStatus] = useState<CalendarStatus | null>(null);
  const [settingsWriteError, setSettingsWriteError] = useState(false);
  const [requestsWriteError, setRequestsWriteError] = useState(false);
  const [exporting, setExporting] = useState(false);

  useEffect(() => {
    invoke<string>("app_version").then(setVersion).catch(() => undefined);
  }, []);

  const [themePreference, setThemePreference] = useState<ThemePreference>(() => {
    try { return parseThemePreference(window.localStorage.getItem(THEME_KEY)); } catch { return "system"; }
  });
  const [systemDark, setSystemDark] = useState(() => window.matchMedia?.("(prefers-color-scheme: dark)").matches ?? false);
  const theme = resolveTheme(themePreference, systemDark);

  useEffect(() => {
    const query = window.matchMedia?.("(prefers-color-scheme: dark)");
    if (!query) return undefined;
    const update = () => setSystemDark(query.matches);
    query.addEventListener("change", update);
    return () => query.removeEventListener("change", update);
  }, []);

  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    writeLocalValue(() => window.localStorage, THEME_KEY, themePreference);
  }, [theme, themePreference]);

  useEffect(() => {
    if (!desktop) return undefined;
    const onError = (event: ErrorEvent) => desktopStore.recordError("oberflaeche", event.message || "Unbekannter Fehler");
    const onRejection = (event: PromiseRejectionEvent) => desktopStore.recordError("oberflaeche", event.reason instanceof Error ? event.reason.message : String(event.reason));
    window.addEventListener("error", onError);
    window.addEventListener("unhandledrejection", onRejection);
    return () => {
      window.removeEventListener("error", onError);
      window.removeEventListener("unhandledrejection", onRejection);
    };
  }, [desktop]);

  useEffect(() => {
    if (!desktop) return undefined;
    let active = true;
    desktopStore.load().then((result) => {
      if (!active) return;
      if (result.ok) {
        applyDesktopState(result.value);
      } else {
        setDesktopLoadError(result.error);
      }
      setDesktopReady(true);
    });
    return () => { active = false; };
  }, [desktop]);

  useEffect(() => {
    if (desktop) return;
    if (!loadedSettings.error) setSettingsWriteError(!writeLocalValue(() => window.localStorage, SETTINGS_KEY, JSON.stringify(settings)));
  }, [desktop, settings, loadedSettings.error]);

  useEffect(() => {
    if (desktop) return;
    if (!loadedRequests.error) setRequestsWriteError(!writeLocalValue(() => window.localStorage, REQUESTS_KEY, JSON.stringify(requests)));
  }, [desktop, requests, loadedRequests.error]);

  const persistRequests = (next: RequestRecord[]): string | null => {
    if (!writeLocalValue(() => window.localStorage, REQUESTS_KEY, JSON.stringify(next))) {
      setRequestsWriteError(true);
      return "Lokale Antragsdaten konnten nicht gespeichert werden.";
    }
    setRequests(next);
    return null;
  };

  function applyDesktopState(state: DesktopState) {
    setSettings(state.settings ? { ...defaultSettings, ...state.settings } : defaultSettings);
    setRequests(state.requests);
    setDeletedRequests(state.deletedRequests ?? []);
    setCalendarStatus(state.calendar ?? null);
  }

  const applyUpdate = (updated: RequestRecord) => {
    setRequests((current) => applyRequestUpdate(current, updated));
    setDeletedRequests((current) => applyDeletedUpdate(current, updated));
  };

  const exportStoredRequest = async (id: string): Promise<{ kind: "success" | "cancelled" | "error"; message: string }> => {
    if (desktopUnavailable) return { kind: "error", message: desktopUnavailable };
    const result = await desktopStore.exportRequest(id, toDateKey(new Date()));
    if (!result.ok) return { kind: "error", message: result.error };
    if (result.value === null) return { kind: "cancelled", message: "PDF-Export abgebrochen." };
    applyUpdate(result.value);
    return { kind: "success", message: `PDF gespeichert: ${result.value.exportPath ?? ""}` };
  };

  const createBackup = async (): Promise<string> => {
    if (desktopUnavailable) return desktopUnavailable;
    const result = await desktopStore.createBackup(toDateKey(new Date()));
    if (!result.ok) return `Die Sicherung konnte nicht erstellt werden: ${result.error}`;
    return result.value === null ? "Sicherung abgebrochen." : `Sicherung gespeichert: ${result.value}`;
  };

  const restoreBackup = async (): Promise<string> => {
    if (desktopUnavailable) return desktopUnavailable;
    const result = await desktopStore.restoreBackup();
    if (!result.ok) return `Die Sicherung wurde nicht wiederhergestellt; die bisherigen Daten sind unverändert. ${result.error}`;
    if (result.value === null) return "Wiederherstellung abgebrochen.";
    applyDesktopState(result.value.state);
    return `Sicherung wiederhergestellt. Der vorherige Stand liegt unter ${result.value.safetyBackup}.`;
  };

  const importSchoolHolidays = async (): Promise<string> => {
    if (desktopUnavailable) return desktopUnavailable;
    const result = await desktopStore.importSchoolHolidays();
    if (!result.ok) return `Der Ferienkalender wurde nicht importiert; die bisherigen Daten sind unverändert. ${result.error}`;
    if (result.value === null) return "Import abgebrochen.";
    setCalendarStatus(result.value);
    const school = result.value.schoolHolidays;
    return school ? `${school.breakCount} Ferienzeiträume für ${school.federalState} importiert.` : "Ferienkalender importiert.";
  };

  const desktopUnavailable = !desktopReady ? LOADING_MESSAGE : desktopLoadError ? "Lokale Daten sind nicht verfügbar." : null;

  const saveSettings = async (next: Settings): Promise<void> => {
    if (!desktop) { setSettings(next); return; }
    if (desktopUnavailable) { setDesktopError(desktopUnavailable); return; }
    const result = await desktopStore.saveSettings(next);
    if (result.ok) {
      setSettings(result.value);
      setDesktopError(null);
      // Das Bundesland kann sich geändert haben: Datenstand der Ferien neu laden.
      const state = await desktopStore.load();
      if (state.ok) setCalendarStatus(state.value.calendar ?? null);
    }
    else setDesktopError(`Einstellungen konnten nicht gespeichert werden: ${result.error}`);
  };

  const addRequest = async (draft: DraftRequest, requestedHalfUnits: number, exportPath?: string): Promise<string | null> => {
    if (desktop) {
      if (desktopUnavailable) return desktopUnavailable;
      const result = await desktopStore.createRequest(draft, requestedHalfUnits, crypto.randomUUID(), exportPath);
      if (!result.ok) return result.error;
      applyUpdate(result.value);
      return null;
    }
    if (loadedRequests.error || loadedSettings.error || settingsWriteError || requestsWriteError) return "Lokale Daten sind nicht verfügbar.";
    const nextRequest = createRequestRecord(draft, requestedHalfUnits, crypto.randomUUID(), new Date().toISOString(), exportPath);
    return persistRequests([nextRequest, ...requests]);
  };

  const updateRequestStatus = async (id: string, status: RequestStatus, rejectionReason?: string): Promise<string | null> => {
    if (desktop) {
      if (desktopUnavailable) return desktopUnavailable;
      const result = await desktopStore.changeStatus(id, status, rejectionReason);
      if (!result.ok) return result.error;
      applyUpdate(result.value);
      return null;
    }
    if (loadedRequests.error) return loadedRequests.error;
    if (loadedSettings.error) return "Einstellungen konnten nicht gelesen werden.";
    if (settingsWriteError || requestsWriteError) return "Lokale Daten konnten nicht gespeichert werden.";
    const request = requests.find((item) => item.id === id);
    const unarchiving = request !== undefined && unarchiveTarget(request) === status;
    if (!request || (!canTransition(request.status, status) && !unarchiving)) return "Dieser Statuswechsel ist nicht möglich.";
    if (status === "Abgelehnt" && !unarchiving && !rejectionReason?.trim()) return "Bitte einen Ablehnungsgrund angeben.";
    if (status === "Genehmigt") {
      if (request.calculationVersion !== 1) return "Dieser Antrag wurde mit einer älteren Berechnung erstellt und muss neu angelegt werden.";
      const approved = approvedHalfUnits(requests, request.startDate.slice(0, 4), id);
      if (approved + request.requestedHalfUnits > settings.annualEntitlementHalfUnits) return "Die Genehmigung überschreitet den Jahresurlaub.";
    }
    const timestamp = new Date().toISOString();
    return persistRequests(requests.map((item) => item.id === id ? appendStatusEvent(item, status, timestamp, rejectionReason) : item));
  };

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="brand-lockup">
          <div className="brand-mark" aria-hidden="true">U</div>
          <div>
            <p className="brand-name">Urlaubstool</p>
            <p className="brand-caption">Persönliche Planung</p>
          </div>
        </div>

        <nav className="primary-nav" aria-label="Hauptnavigation">
          <p className="nav-label">Arbeitsbereich</p>
          {sections.map((item) => (
            <button
              aria-current={section === item ? "page" : undefined}
              className={`nav-item${section === item ? " is-active" : ""}`}
              key={item}
              disabled={exporting}
              onClick={() => setSection(item)}
              type="button"
            >
              <span className="nav-indicator" aria-hidden="true" />
              {item}
            </button>
          ))}
        </nav>

        <div className="sidebar-footer">
          <span className="status-dot" aria-hidden="true" />
          <span>Lokaler Modus</span>
          <span className="version-label">{version}</span>
        </div>
      </aside>

      <main className="main-panel">
        <header className="topbar">
          <div>
            <p className="eyebrow">Urlaubsjahr · lokal gespeichert</p>
            <h1>{section}</h1>
          </div>
          <button className="profile-button" disabled={exporting} onClick={() => setSection("Einstellungen")} type="button">
            <span className="profile-avatar" aria-hidden="true">P</span>
            <span>{settings.firstName || settings.lastName ? `${settings.firstName} ${settings.lastName}`.trim() : "Profil einrichten"}</span>
          </button>
        </header>

        {desktopLoadError && <p role="alert">Die lokalen Daten konnten nicht geladen werden. Es wurde nichts verändert. Ursache: {desktopLoadError}</p>}
        {desktopError && <p role="alert">{desktopError}</p>}
        {loadedSettings.error && <p role="alert">Einstellungen konnten nicht gelesen werden. Sie wurden nicht überschrieben; bitte die Daten sichern und prüfen.</p>}
        {loadedRequests.error && <p role="alert">{loadedRequests.error}</p>}
        {(settingsWriteError || requestsWriteError) && <p role="alert">Lokale Daten konnten nicht gespeichert werden. Bitte Speicherplatz und Zugriffsrechte prüfen.</p>}
        {!loadedRequests.error && requests.some((request) => request.calculationVersion !== 1) && (
          <p role="alert">Ältere Anträge können falsch berechnete Urlaubstage enthalten. Bitte ihre Werte prüfen; ältere Entwürfe müssen für eine Genehmigung neu angelegt werden.</p>
        )}

        <ViewErrorBoundary onError={(text) => { if (desktop) desktopStore.recordError("ansicht", `${section}: ${text}`); }} resetKey={section}>
        {section === "Übersicht" && <Dashboard loadSummary={desktop ? desktopStore.yearSummary : null} requests={requests} settings={settings} onOpenHistory={() => setSection("Historie")} onOpenSettings={() => setSection("Einstellungen")} />}
        {section === "Planung" && <Planning addRequest={addRequest} exporting={exporting} onExportingChange={setExporting} requests={requests} settings={settings} storageError={desktopUnavailable ?? loadedRequests.error ?? (loadedSettings.error || settingsWriteError || requestsWriteError ? "Lokale Daten sind nicht verfügbar." : null)} />}
        {section === "Kalender" && <CalendarView loadYear={desktop ? desktopStore.calendarYear : null} requests={requests} settings={settings} theme={theme} />}
        {section === "Historie" && <HistoryView deletedRequests={deletedRequests} desktop={desktop} onExport={exportStoredRequest} requests={requests} onStatusChange={updateRequestStatus} />}
        {section === "Einstellungen" && <SettingsView calendar={calendarStatus} onImportHolidays={desktop ? importSchoolHolidays : null} onSave={saveSettings} settings={settings} storageError={desktopUnavailable !== null || loadedSettings.error || settingsWriteError} />}
        {section === "Einstellungen" && <AppearancePanel onChange={setThemePreference} preference={themePreference} />}
        {section === "Einstellungen" && desktop && <MigrationPanel disabled={desktopUnavailable !== null || exporting} onImported={applyDesktopState} />}
        {section === "Einstellungen" && desktop && <BackupPanel disabled={desktopUnavailable !== null || exporting} onBackup={createBackup} onRestore={restoreBackup} />}
        {section === "Diagnose" && <DiagnosticsView desktop={desktop} version={version} />}
        </ViewErrorBoundary>
      </main>
    </div>
  );
}

/** Fängt Abstürze einer Ansicht ab, statt die ganze App weiß werden zu lassen (REQ-DIAG-002/003). */
class ViewErrorBoundary extends Component<{ children: ReactNode; onError: (message: string) => void; resetKey: string }, { failed: boolean }> {
  state = { failed: false };

  static getDerivedStateFromError() {
    return { failed: true };
  }

  componentDidCatch(error: unknown) {
    this.props.onError(error instanceof Error ? error.message : String(error));
  }

  componentDidUpdate(previous: { resetKey: string }) {
    if (previous.resetKey !== this.props.resetKey && this.state.failed) this.setState({ failed: false });
  }

  render() {
    if (!this.state.failed) return this.props.children;
    return (
      <div className="content-area">
        <section className="workspace-section" role="alert">
          <h2>Diese Ansicht konnte nicht angezeigt werden</h2>
          <p>Ihre gespeicherten Daten sind davon nicht betroffen. Bitte die Ansicht neu laden oder einen anderen Bereich öffnen. Technische Details stehen im Bereich „Diagnose“.</p>
          <button className="primary-button" onClick={() => this.setState({ failed: false })} type="button">Ansicht neu laden</button>
        </section>
      </div>
    );
  }
}

function DiagnosticsView({ desktop, version }: { desktop: boolean; version: string }) {
  const [info, setInfo] = useState<DiagnosticsInfo | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const refresh = async () => {
    if (!desktop) return;
    const result = await desktopStore.diagnosticsInfo();
    if (result.ok) { setInfo(result.value); setError(null); }
    else setError(`Diagnosedaten konnten nicht gelesen werden: ${result.error}`);
  };

  useEffect(() => { void refresh(); }, [desktop]);

  const exportReport = async () => {
    if (busy) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await desktopStore.exportDiagnostics(toDateKey(new Date()));
      setMessage(!result.ok ? `Der Bericht wurde nicht gespeichert: ${result.error}` : result.value === null ? "Export abgebrochen." : `Diagnosebericht gespeichert: ${result.value}`);
    } finally {
      setBusy(false);
    }
  };

  if (!desktop) {
    return (
      <div className="content-area"><section className="workspace-section"><h2>Diagnose</h2><p>Version {version || "unbekannt"}. Protokoll und Diagnosebericht stehen nur in der Desktop-App zur Verfügung.</p></section></div>
    );
  }

  return (
    <div className="content-area settings-layout">
      <section className="workspace-section">
        <div className="section-heading">
          <div>
            <p className="eyebrow">Informationen</p>
            <h2>System und Daten</h2>
          </div>
          <button className="secondary-button" onClick={() => { void refresh(); }} type="button">Aktualisieren</button>
        </div>
        {error && <p role="alert">{error}</p>}
        {info && (
          <dl className="diagnostics-list">
            <dt>Version</dt><dd>{info.version}</dd>
            <dt>System</dt><dd>{info.os} ({info.arch})</dd>
            <dt>Datenbank</dt><dd>{info.databaseFile}</dd>
            <dt>Zustand</dt><dd>{info.databaseStatus}</dd>
            <dt>Protokoll</dt><dd>{info.logFile}</dd>
          </dl>
        )}
        <p className="save-note">Das Protokoll enthält Zeitpunkt, Bereich und Fehlermeldung, aber keine Namen, Adressen oder Antragsdaten. Pfade im Benutzerordner werden mit „~“ abgekürzt.</p>
        <div className="form-actions">
          <button className="primary-button" disabled={busy} onClick={() => { void exportReport(); }} type="button">Diagnosebericht speichern …</button>
        </div>
        {message && <p role="status">{message}</p>}
      </section>
      <section className="workspace-section">
        <div className="section-heading"><div><p className="eyebrow">Protokoll</p><h2>Letzte Warnungen und Fehler</h2></div></div>
        {info && info.recentErrors.length === 0 && <p>Keine Fehler protokolliert.</p>}
        {info && info.recentErrors.length > 0 && (
          <ul className="diagnostics-errors">
            {info.recentErrors.map((entry, index) => (
              <li key={`${entry.timeUtc}-${index}`}>
                <span className="diagnostics-meta">{new Intl.DateTimeFormat("de-DE", { dateStyle: "short", timeStyle: "medium" }).format(new Date(entry.timeUtc))} · {entry.context}</span>
                <span>{entry.message}</span>
              </li>
            ))}
          </ul>
        )}
      </section>
    </div>
  );
}

function Dashboard({
  loadSummary,
  onOpenHistory,
  onOpenSettings,
  requests,
  settings,
}: {
  loadSummary: ((year: number) => Promise<StoreResult<YearSummary>>) | null;
  onOpenHistory: () => void;
  onOpenSettings: () => void;
  requests: RequestRecord[];
  settings: Settings;
}) {
  const year = new Date().getFullYear();
  const local = localYearSummary(requests, year, settings.annualEntitlementHalfUnits);
  const [loaded, setLoaded] = useState<{ summary: YearSummary; error: string | null } | null>(null);

  useEffect(() => {
    if (!loadSummary) return undefined;
    let active = true;
    loadSummary(year).then((result) => {
      if (!active) return;
      setLoaded(result.ok ? { summary: result.value, error: null } : { summary: localYearSummary(requests, year, settings.annualEntitlementHalfUnits), error: result.error });
    });
    return () => { active = false; };
    // Neu laden, sobald sich Anträge oder der Anspruch ändern.
  }, [loadSummary, year, requests, settings.annualEntitlementHalfUnits]);

  const summary = loadSummary ? loaded?.summary ?? null : local;
  const approvedDays = summary?.approvedHalfUnits ?? 0;
  const requestedDays = summary?.requestedHalfUnits ?? 0;
  const remainingDays = summary?.availableHalfUnits ?? settings.annualEntitlementHalfUnits;
  const today = toDateKey(new Date());
  const nextRequest = requests.filter((request) => request.endDate >= today && request.status !== "Abgelehnt" && request.status !== "Archiviert").sort((a, b) => a.startDate.localeCompare(b.startDate))[0];

  return (
    <div className="content-area">
      {loaded?.error && <p role="alert">Die Jahresübersicht konnte nicht aus der Datenbank geladen werden: {loaded.error}</p>}
      <section className="summary-grid" aria-label="Urlaubsübersicht" aria-busy={summary === null}>
        <article className="summary-panel summary-panel--green">
          <p className="summary-label">Jahresanspruch</p>
          <p className="summary-value">{formatHalfUnits(summary?.entitlementHalfUnits ?? settings.annualEntitlementHalfUnits)} <span>Tage</span></p>
          <p className="summary-footnote">{settings.firstName ? `${settings.firstName} ${settings.lastName}`.trim() : "Noch nicht eingerichtet"}</p>
        </article>
        <article className="summary-panel summary-panel--gold">
          <p className="summary-label">Genehmigt</p>
          <p className="summary-value">{formatHalfUnits(approvedDays)} <span>Tage</span></p>
          <p className="summary-footnote">{approvedDays > 0 ? "Im aktuellen Jahr genehmigt" : "Keine Einträge"}</p>
        </article>
        <article className="summary-panel summary-panel--coral">
          <p className="summary-label">Beantragt</p>
          <p className="summary-value">{formatHalfUnits(requestedDays)} <span>Tage</span></p>
          <p className="summary-footnote">{requestedDays > 0 ? "Offene oder laufende Anträge" : "Keine offenen Anträge"}</p>
        </article>
        <article className="summary-panel summary-panel--blue">
          <p className="summary-label">Verfügbar</p>
          <p className="summary-value">{formatHalfUnits(remainingDays)} <span>Tage</span></p>
          <p className="summary-footnote">{remainingDays < 0 ? "Genehmigte Tage überschreiten den Jahresanspruch." : "Gegenwärtig noch frei"}</p>
          {remainingDays < 0 && <p role="alert">Bitte Jahresanspruch und genehmigte Anträge prüfen.</p>}
        </article>
      </section>

      <section className="dashboard-lower">
        <div className="section-heading">
          <div>
            <p className="eyebrow">Startpunkt</p>
            <h2>Dein Urlaubsjahr</h2>
          </div>
          <span className="year-chip">{year}</span>
        </div>
        <div className="setup-row">
          <div className="setup-symbol" aria-hidden="true">{settings.firstName ? settings.firstName.slice(0, 2).toUpperCase() : "01"}</div>
          <div className="setup-copy">
            <h3>{settings.firstName || settings.lastName ? `${settings.firstName} ${settings.lastName}`.trim() : "Persönliche Angaben fehlen"}</h3>
            <p>{settings.firstName ? `Bundesland: ${settings.federalState}` : "Anspruch, Arbeitswoche und Bundesland sind noch nicht hinterlegt."}</p>
          </div>
          <button className="primary-button" onClick={onOpenSettings} type="button">Einstellungen öffnen</button>
        </div>
        <div className="next-row">
          <div>
            <p className="eyebrow">Nächster Urlaub</p>
            <h3>{nextRequest ? formatRange(nextRequest.startDate, nextRequest.endDate) : "Noch kein Urlaub geplant"}</h3>
          </div>
          <span className="next-date">{nextRequest ? `${formatHalfUnits(nextRequest.requestedHalfUnits)} Tage` : "--.--."}</span>
        </div>
        <div className="year-requests">
          <div className="year-requests-head">
            <p className="eyebrow">Anträge {year}</p>
            {summary && summary.requests.length > 0 && <button className="secondary-button" onClick={onOpenHistory} type="button">Zur Historie</button>}
          </div>
          {summary && summary.requests.length > 0 ? (
            <ul className="year-requests-list">
              {summary.requests.map((request) => (
                <li key={request.id}>
                  <span>{formatRange(request.startDate, request.endDate)}</span>
                  <span className="year-requests-days">{formatHalfUnits(request.requestedHalfUnits)} Tage</span>
                  <StatusPill status={request.status} />
                </li>
              ))}
            </ul>
          ) : (
            <p className="year-requests-empty">{summary ? "Für dieses Jahr gibt es noch keine Anträge." : "Wird geladen …"}</p>
          )}
        </div>
      </section>
    </div>
  );
}

function Planning({ addRequest, exporting, onExportingChange, requests, settings, storageError }: { addRequest: (draft: DraftRequest, requestedHalfUnits: number, exportPath?: string) => Promise<string | null>; exporting: boolean; onExportingChange: (value: boolean) => void; requests: RequestRecord[]; settings: Settings; storageError: string | null }) {
  const [draft, setDraft] = useState<DraftRequest>({
    startDate: nextIsoDate(),
    endDate: nextIsoDate(7),
    startHalfDay: false,
    endHalfDay: false,
    azaDates: [],
  });
  const [azaDate, setAzaDate] = useState("");
  const [startValid, setStartValid] = useState(true);
  const [endValid, setEndValid] = useState(true);
  const [azaMessage, setAzaMessage] = useState<string | null>(null);
  const [exportMessage, setExportMessage] = useState<{ kind: "success" | "cancelled" | "error"; message: string } | null>(null);

  const updateRange = (field: "startDate" | "endDate", value: string) => {
    const updated = { ...draft, [field]: value };
    const retained = retainAzaDates(updated.azaDates, updated.startDate, updated.endDate);
    setDraft({ ...updated, azaDates: retained.dates });
    setAzaMessage(retained.removed.length ? `AZA-Tage außerhalb des Zeitraums entfernt: ${retained.removed.map(formatGermanDate).join(", ")}.` : null);
  };

  const addAza = () => {
    const result = addAzaDate(draft.azaDates, azaDate, draft.startDate, draft.endDate);
    setAzaMessage(result.error);
    if (!result.error) {
      setDraft({ ...draft, azaDates: result.dates });
      setAzaDate("");
    }
  };

  const approvedDays = approvedHalfUnits(requests, draft.startDate.slice(0, 4));
  const calculationKey = JSON.stringify({
    ...draft,
    workdays: settings.workdays,
    federalState: stateHolidayCodes[settings.federalState]?.[0] ?? "",
    includeOptionalHolidays: settings.includeOptionalHolidays,
    annualEntitlementHalfUnits: settings.annualEntitlementHalfUnits,
    alreadyApprovedHalfUnits: approvedDays,
  });
  const [calculation, setCalculation] = useState<{ key: string; result: CalculationResult | null; error: string | null } | null>(null);

  useEffect(() => {
    let active = true;
    if (!isTauri()) {
      setCalculation({ key: calculationKey, result: null, error: "Die Berechnung ist nur in der Desktop-App verfügbar." });
      return () => { active = false; };
    }
    invoke<Omit<CalculationResult, "details"> & { days: DayDetail[] }>("calculate_vacation", { input: JSON.parse(calculationKey) })
      .then(({ days, ...result }) => { if (active) setCalculation({ key: calculationKey, result: { ...result, details: days }, error: null }); })
      .catch((error: unknown) => { if (active) setCalculation({ key: calculationKey, result: null, error: String(error) }); });
    return () => { active = false; };
  }, [calculationKey]);

  const currentCalculation = calculation?.key === calculationKey ? calculation : null;
  const preview = currentCalculation?.result;
  const datesValid = startValid && endValid;
  const error = !datesValid
    ? "Bitte Start- und Enddatum vollständig im Format TT.MM.JJJJ eingeben."
    : currentCalculation?.error ?? preview?.errors[0] ?? null;
  const canSave = preview !== undefined && preview !== null && !error && !storageError && preview.requestedHalfUnits > 0;

  const resetDraft = () => {
    setDraft({
      startDate: nextIsoDate(),
      endDate: nextIsoDate(7),
      startHalfDay: false,
      endHalfDay: false,
      azaDates: [],
    });
    setAzaDate("");
    setAzaMessage(null);
  };

  const save = async () => {
    if (!canSave || !preview || exporting) return;
    onExportingChange(true);
    setExportMessage(null);
    try {
      const error = await addRequest(draft, preview.requestedHalfUnits);
      if (error) { setExportMessage({ kind: "error", message: error }); return; }
      resetDraft();
      setExportMessage({ kind: "success", message: "Antrag gespeichert." });
    } finally {
      onExportingChange(false);
    }
  };

  const exportPdf = async () => {
    if (!canSave || !preview || exporting) return;
    onExportingChange(true);
    setExportMessage(null);
    try {
      const result = await runPdfExport(
        preview.requestedHalfUnits,
        () => invoke<PdfReceipt | null>("export_vacation_pdf", {
          input: JSON.parse(calculationKey),
          personal: { firstName: settings.firstName, lastName: settings.lastName, address: settings.address ?? "", department: settings.department, personnelNumber: settings.personnelNumber ?? "", className: settings.className, applicationDate: toDateKey(new Date()) },
        }),
        (receipt) => addRequest(draft, receipt.requestedHalfUnits, receipt.path),
      );
      if (result.kind === "success") resetDraft();
      setExportMessage(result);
    } finally {
      onExportingChange(false);
    }
  };

  return (
    <div className="content-area">
      <section className="workspace-section">
        <div className="section-heading">
          <div>
            <p className="eyebrow">Urlaubsantrag</p>
            <h2>Zeitraum festlegen</h2>
          </div>
          <span className="state-pill">Entwurf</span>
        </div>
        <fieldset className="planning-fields" disabled={exporting}>
        <div className="date-fields">
          <label>
            Erster Urlaubstag
            <DateInput label="Erster Urlaubstag" value={draft.startDate} onChange={(value) => updateRange("startDate", value)} onValidityChange={setStartValid} />
          </label>
          <label>
            Letzter Urlaubstag
            <DateInput label="Letzter Urlaubstag" value={draft.endDate} onChange={(value) => updateRange("endDate", value)} onValidityChange={setEndValid} />
          </label>
        </div>
        <div className="day-options">
          <label>
            <input checked={draft.startHalfDay} onChange={(event) => setDraft((current) => ({ ...current, startHalfDay: event.target.checked }))} type="checkbox" />
            Halbtag am Start
          </label>
          <label>
            <input checked={draft.endHalfDay} onChange={(event) => setDraft((current) => ({ ...current, endHalfDay: event.target.checked }))} type="checkbox" />
            Halbtag am Ende
          </label>
        </div>
        <div className="aza-fields">
          <label>AZA-Tag <DateInput allowEmpty label="AZA-Tag" value={azaDate} onChange={setAzaDate} /></label>
          <button className="secondary-button" onClick={addAza} type="button">AZA hinzufügen</button>
        </div>
        {azaMessage && <p role="status">{azaMessage}</p>}
        {draft.azaDates.length > 0 && <ul className="aza-list">{draft.azaDates.map((date) => (
          <li key={date}><span>{formatGermanDate(date)}</span><button aria-label={`AZA ${formatGermanDate(date)} entfernen`} title="AZA entfernen" type="button" onClick={() => { setDraft({ ...draft, azaDates: draft.azaDates.filter((item) => item !== date) }); setAzaMessage(null); }}>×</button></li>
        ))}</ul>}
        <div className="empty-result">
          <div className="empty-rule" />
          <div>
            <p className="eyebrow">Tagesbewertung</p>
            <h3>{error ? "Berechnung nicht möglich" : preview ? "Vorschau" : "Berechnung läuft"}</h3>
            <p>
              {error ?? (preview
                ? `Relevante Arbeitstage im Zeitraum: ${formatHalfUnits(preview.requestedHalfUnits)} Tage.`
                : "Der Zeitraum wird geprüft.")}
            </p>
            {preview?.warnings.map((warning) => <p key={warning} role="status">{warning}</p>)}
          </div>
          <div className="empty-total"><span>Beantragt</span><strong>{preview && !error ? formatHalfUnits(preview.requestedHalfUnits) : "--"}</strong></div>
        </div>
        {preview && <dl className="calculation-balance">
          <div><dt>Jahresanspruch</dt><dd>{formatHalfUnits(preview.annualEntitlementHalfUnits)} Tage</dd></div>
          <div><dt>Bereits genehmigt</dt><dd>{formatHalfUnits(preview.alreadyApprovedHalfUnits)} Tage</dd></div>
          <div><dt>Rest vor Antrag</dt><dd>{formatHalfUnits(preview.remainingBeforeHalfUnits)} Tage</dd></div>
          <div><dt>Rest nach Antrag</dt><dd>{formatHalfUnits(preview.remainingAfterHalfUnits)} Tage</dd></div>
        </dl>}
        {preview && preview.details.length > 0 && (
          <details className="day-breakdown">
            <summary>Tagesaufschlüsselung</summary>
            <ul>
              {preview.details.map((day) => {
                const detail = describeDay(day);
                return <li key={day.date}>
                  <div className="day-breakdown-heading"><strong>{detail.title}</strong><span>{detail.counted}</span></div>
                  <p>{detail.facts.join(" · ")}</p>
                  <p>{detail.reasons.join(" ")}</p>
                </li>;
              })}
            </ul>
          </details>
        )}
        <div className="form-actions">
          <button className="secondary-button" disabled={!canSave} onClick={save} type="button">Antrag speichern</button>
          <button className="primary-button" disabled={!canSave} onClick={exportPdf} type="button">{exporting ? "PDF wird erstellt…" : "PDF exportieren"}</button>
        </div>
        </fieldset>
        {exportMessage && <p role={exportMessage.kind === "error" ? "alert" : "status"}>{exportMessage.message}</p>}
      </section>
    </div>
  );
}

const CALENDAR_COLORS_KEY = "urlaubstool.calendarColors.v1";
const WEEK_DAYS = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];

function calendarDefaults(theme: Theme): CalendarColors {
  return theme === "dark" ? DEFAULT_DARK_CALENDAR_COLORS : DEFAULT_CALENDAR_COLORS;
}

function calendarColorsKey(theme: Theme): string {
  return theme === "dark" ? `${CALENDAR_COLORS_KEY}.dark` : CALENDAR_COLORS_KEY;
}

function loadCalendarColors(theme: Theme): CalendarColors {
  try {
    return parseCalendarColors(window.localStorage.getItem(calendarColorsKey(theme)), calendarDefaults(theme));
  } catch {
    return { ...calendarDefaults(theme) };
  }
}

/** Ersatz ohne Backend (Web-Vorschau): nur Arbeitstage, keine Feiertage/Ferien. */
function localCalendarYear(year: number, settings: Settings): CalendarDay[] {
  const days: CalendarDay[] = [];
  for (let date = new Date(year, 0, 1); date.getFullYear() === year; date = addDays(date, 1)) {
    days.push({ date: toDateKey(date), isWorkday: isWorkingDate(date, settings), publicHoliday: null, holidayIsOptional: false, isSchoolVacation: null, vocationalSchool: null });
  }
  return days;
}

function CalendarView({ loadYear, requests, settings, theme }: { loadYear: ((year: number) => Promise<StoreResult<CalendarDay[]>>) | null; requests: RequestRecord[]; settings: Settings; theme: Theme }) {
  const [viewDate, setViewDate] = useState(() => new Date(new Date().getFullYear(), new Date().getMonth(), 1));
  const [calendarMode, setCalendarMode] = useState<"month" | "year">("month");
  const [colors, setColors] = useState<CalendarColors>(() => loadCalendarColors(theme));
  const [colorsTheme, setColorsTheme] = useState<Theme>(theme);
  const [loaded, setLoaded] = useState<{ year: number; days: CalendarDay[]; error: string | null } | null>(null);
  const year = viewDate.getFullYear();
  const settingsKey = JSON.stringify([settings.federalState, settings.workdays, settings.includeOptionalHolidays, settings.studentMode, settings.vocationalSchoolWeek]);

  useEffect(() => {
    let active = true;
    if (!loadYear) {
      setLoaded({ year, days: localCalendarYear(year, settings), error: null });
      return undefined;
    }
    loadYear(year).then((result) => {
      if (!active) return;
      setLoaded(result.ok
        ? { year, days: result.value, error: null }
        : { year, days: localCalendarYear(year, settings), error: `Feiertage und Ferien konnten nicht geladen werden: ${result.error}` });
    });
    return () => { active = false; };
    // settingsKey bündelt die Einstellungen, die den Kalender verändern.
  }, [loadYear, year, settingsKey]);

  useEffect(() => {
    if (colorsTheme !== theme) {
      setColors(loadCalendarColors(theme));
      setColorsTheme(theme);
      return;
    }
    writeLocalValue(() => window.localStorage, calendarColorsKey(theme), JSON.stringify(colors));
  }, [colors, colorsTheme, theme]);

  function move(amount: number) {
    setViewDate((current) => new Date(current.getFullYear(), current.getMonth() + amount, 1));
  }

  const marks = requestMarksByDate(requests);
  const aza = azaDatesOf(requests);
  const byDate = new Map((loaded?.year === year ? loaded.days : []).map((day) => [day.date, day]));
  const style = Object.fromEntries(Object.entries(colors).map(([category, color]) => [`--cal-${category}`, color])) as CSSProperties;
  const vacationsKnown = loaded?.year === year && loaded.days.some((day) => day.isSchoolVacation !== null);

  return (
    <div className="content-area">
      <section className="workspace-section calendar-section" style={style}>
        <div className="section-heading">
          <div>
            <p className="eyebrow">Kalender</p>
            <h2>{calendarMode === "month" ? new Intl.DateTimeFormat("de-DE", { month: "long", year: "numeric" }).format(viewDate) : year}</h2>
          </div>
          <div className="view-toggle" role="group" aria-label="Kalenderansicht">
            <button aria-pressed={calendarMode === "month"} className={calendarMode === "month" ? "is-selected" : ""} onClick={() => setCalendarMode("month")} type="button">Monat</button>
            <button aria-pressed={calendarMode === "year"} className={calendarMode === "year" ? "is-selected" : ""} onClick={() => setCalendarMode("year")} type="button">Jahr</button>
          </div>
        </div>
        <div className="calendar-controls">
          <button aria-label="Vorheriges Jahr" onClick={() => move(-12)} type="button">«</button>
          <button aria-label={calendarMode === "month" ? "Vorheriger Monat" : "Vorheriges Jahr"} onClick={() => move(calendarMode === "month" ? -1 : -12)} type="button">‹</button>
          <button className="calendar-today" onClick={() => setViewDate(new Date(new Date().getFullYear(), new Date().getMonth(), 1))} type="button">Heute</button>
          <button aria-label={calendarMode === "month" ? "Nächster Monat" : "Nächstes Jahr"} onClick={() => move(calendarMode === "month" ? 1 : 12)} type="button">›</button>
          <button aria-label="Nächstes Jahr" onClick={() => move(12)} type="button">»</button>
        </div>
        {loaded?.error && <p role="alert">{loaded.error}</p>}
        {calendarMode === "month" ? (
          <CalendarGrid aza={aza} byDate={byDate} marks={marks} month={viewDate.getMonth()} year={year} />
        ) : (
          <div className="year-calendar-grid">
            {Array.from({ length: 12 }, (_, month) => (
              <article className="mini-month" key={month}>
                <h3>{new Intl.DateTimeFormat("de-DE", { month: "long" }).format(new Date(year, month, 1))}</h3>
                <CalendarGrid aza={aza} byDate={byDate} compact marks={marks} month={month} year={year} />
              </article>
            ))}
          </div>
        )}
        <ul className="calendar-legend" aria-label="Legende">
          {CALENDAR_LEGEND.map((entry) => (
            <li key={entry.category}><span className="legend-swatch" style={{ background: colors[entry.category] }} aria-hidden="true" />{entry.label}</li>
          ))}
        </ul>
        <p className="calendar-note">
          {settings.federalState ? `Feiertage: ${settings.federalState}` : "Bitte ein Bundesland in den Einstellungen wählen."}
          {" · "}{vacationsKnown ? "Schulferien importiert" : "Schulferien für dieses Jahr nicht importiert"}
          {!loadYear && " · Web-Vorschau ohne Feiertagsdaten"}
        </p>
        <details className="calendar-colors">
          <summary>Farben anpassen ({theme === "dark" ? "Dunkelmodus" : "Hellmodus"})</summary>
          <div className="calendar-color-fields">
            {CALENDAR_LEGEND.map((entry) => (
              <label key={entry.category}>
                <input onChange={(event) => setColors((current) => ({ ...current, [entry.category]: event.target.value }))} type="color" value={colors[entry.category]} />
                {entry.label}
              </label>
            ))}
          </div>
          <button className="secondary-button" onClick={() => setColors({ ...calendarDefaults(theme) })} type="button">Standardfarben</button>
        </details>
      </section>
    </div>
  );
}

function CalendarGrid({ aza, byDate, compact = false, marks, month, year }: {
  aza: Set<string>;
  byDate: Map<string, CalendarDay>;
  compact?: boolean;
  marks: ReturnType<typeof requestMarksByDate>;
  month: number;
  year: number;
}) {
  const firstDayOffset = (new Date(year, month, 1).getDay() + 6) % 7;
  const daysInMonth = new Date(year, month + 1, 0).getDate();
  const todayKey = toDateKey(new Date());

  return (
    <div className={`calendar-grid${compact ? " is-compact" : ""}`} role="grid" aria-label={new Intl.DateTimeFormat("de-DE", { month: "long", year: "numeric" }).format(new Date(year, month, 1))}>
      {WEEK_DAYS.map((day) => (
        <span className="calendar-weekday" key={day} role="columnheader">{compact ? day[0] : day}</span>
      ))}
      {Array.from({ length: 42 }, (_, index) => {
        const dayNumber = index - firstDayOffset + 1;
        if (dayNumber <= 0 || dayNumber > daysInMonth) return <span aria-hidden="true" className="calendar-day is-muted" key={index} />;
        const isoKey = toDateKey(new Date(year, month, dayNumber));
        const day = byDate.get(isoKey);
        const categories = day ? dayCategories(day, marks, aza) : [];
        const description = day ? describeCalendarDay(day, categories) : isoKey;
        const [primary] = categories;
        const marker = categories.includes("aza") && primary !== "aza" ? "aza" : categories.find((category) => category === "holiday" || category === "school");
        return (
          <span
            aria-label={description}
            className={`calendar-day${isoKey === todayKey ? " is-today" : ""}${primary && primary !== "workday" ? " is-marked" : ""}`}
            key={index}
            role="gridcell"
            style={primary ? { background: `var(--cal-${primary})` } : undefined}
            title={description}
          >
            {dayNumber}
            {marker && primary !== marker && <span className="calendar-marker" style={{ background: `var(--cal-${marker})` }} aria-hidden="true" />}
          </span>
        );
      })}
    </div>
  );
}

function HistoryView({ deletedRequests, desktop, onExport, onStatusChange, requests }: { deletedRequests: RequestRecord[]; desktop: boolean; onExport: (id: string) => Promise<{ kind: "success" | "cancelled" | "error"; message: string }>; onStatusChange: (id: string, status: RequestStatus, rejectionReason?: string) => Promise<string | null>; requests: RequestRecord[] }) {
  const allowDelete = desktop;
  const [year, setYear] = useState(String(new Date().getFullYear()));
  const [statusFilter, setStatusFilter] = useState("Alle Status");
  const [rejectionId, setRejectionId] = useState<string | null>(null);
  const [rejectionReason, setRejectionReason] = useState("");
  const [statusError, setStatusError] = useState<string | null>(null);
  const [deleteId, setDeleteId] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [exportMessage, setExportMessage] = useState<{ id: string; kind: "success" | "cancelled" | "error"; message: string } | null>(null);
  const exportRequest = async (id: string) => {
    if (busy) return;
    setBusy(true);
    setStatusError(null);
    try {
      setExportMessage({ id, ...(await onExport(id)) });
    } finally {
      setBusy(false);
    }
  };
  const changeStatus = async (id: string, status: RequestStatus, reason?: string): Promise<boolean> => {
    if (busy) return false;
    setBusy(true);
    setStatusError(null);
    try {
      const error = await onStatusChange(id, status, reason);
      setStatusError(error);
      return error === null;
    } finally {
      setBusy(false);
    }
  };
  const showDeleted = statusFilter === "Gelöscht";
  const listed = showDeleted ? deletedRequests : requests;
  const years = [...new Set([String(new Date().getFullYear()), ...listed.map((request) => request.startDate.slice(0, 4))])].sort().reverse();
  const visibleRequests = filterAndSortHistory(listed, year, showDeleted ? "Alle Status" : statusFilter);

  return (
    <div className="content-area">
      <section className="workspace-section">
        <div className="section-heading">
          <div>
            <p className="eyebrow">Anträge</p>
            <h2>Historie</h2>
          </div>
          <div className="history-filters">
            <select aria-label="Jahr" value={year} onChange={(event) => setYear(event.target.value)}>{years.map((item) => <option key={item}>{item}</option>)}</select>
            <select aria-label="Status" value={statusFilter} onChange={(event) => setStatusFilter(event.target.value)}><option>Alle Status</option><option>Entwurf</option><option>Exportiert</option><option>Genehmigt</option><option>Abgelehnt</option><option>Archiviert</option>{desktop && <option>Gelöscht</option>}</select>
          </div>
        </div>
        {statusError && <p role="alert">{statusError}</p>}
        {visibleRequests.length === 0 ? (
          <div className="history-empty">
            <span className="history-empty-mark" aria-hidden="true">—</span>
            <p>{showDeleted ? "Keine gelöschten Anträge in diesem Jahr." : "Keine Anträge für die gewählten Filter."}</p>
          </div>
        ) : (
          <div className="history-list">
            {visibleRequests.map((request) => (
              <article className="history-card" key={request.id}>
                <div className="history-card-head">
                  <div>
                    <p className="eyebrow history-card-range">{formatRange(request.startDate, request.endDate)}</p>
                    <h3>{formatHalfUnits(request.requestedHalfUnits)} Tage</h3>
                  </div>
                  <StatusPill status={request.status} />
                </div>
                {request.rejectionReason && <p>Ablehnungsgrund: {request.rejectionReason}</p>}
                {request.azaDates && request.azaDates.length > 0 && <p>AZA: {request.azaDates.map(formatGermanDate).join(", ")}</p>}
                {request.exportPath && <p>PDF: {request.exportPath}</p>}
                {request.statusEvents && request.statusEvents.length > 0 && <details><summary>Statusverlauf</summary><ul>{request.statusEvents.map((event, index) => <li key={`${event.occurredAtUtc}-${index}`}>{new Intl.DateTimeFormat("de-DE", { dateStyle: "short", timeStyle: "short" }).format(new Date(event.occurredAtUtc))}: {event.status}{event.rejectionReason ? ` – ${event.rejectionReason}` : ""}</li>)}</ul></details>}
                {request.calculationVersion !== 1 && request.status === "Entwurf" && <p>Dieser ältere Entwurf muss für eine Genehmigung neu angelegt werden.</p>}
                {showDeleted ? (
                  <div className="history-actions">
                    {(() => {
                      const target = restoreTarget(request);
                      return (
                        <button className="secondary-button" disabled={busy || target === null} title={target === null ? "Der Status vor dem Löschen ist nicht bekannt." : undefined} onClick={() => { if (target) void changeStatus(request.id, target as RequestStatus); }} type="button">
                          {target ? `Wiederherstellen (als ${target})` : "Wiederherstellen"}
                        </button>
                      );
                    })()}
                  </div>
                ) : (
                <div className="history-actions">
                  {(["Genehmigt", "Abgelehnt", "Archiviert"] as RequestStatus[]).map((status) => (
                    <button
                      key={status}
                      className="secondary-button"
                      disabled={busy || !canTransition(request.status, status)}
                      onClick={() => {
                        setStatusError(null);
                        setDeleteId(null);
                        if (status === "Abgelehnt") { setRejectionId(request.id); setRejectionReason(""); }
                        else void changeStatus(request.id, status);
                      }}
                      type="button"
                    >
                      {status}
                    </button>
                  ))}
                  {(() => {
                    const target = unarchiveTarget(request);
                    return target && (
                      <button className="secondary-button" disabled={busy} onClick={() => { setStatusError(null); setDeleteId(null); setRejectionId(null); void changeStatus(request.id, target); }} type="button">
                        {`Ent-archivieren (als ${target})`}
                      </button>
                    );
                  })()}
                  {allowDelete && (
                    <button className="secondary-button" disabled={busy || !canDelete(request.status, countsAsApproved(request))} title={canDelete(request.status, countsAsApproved(request)) ? undefined : request.status === "Archiviert" ? "Zählt als genehmigt: zuerst ent-archivieren und ablehnen." : "Genehmigte Anträge müssen zuerst abgelehnt werden."} onClick={() => { setStatusError(null); setRejectionId(null); setDeleteId(request.id); }} type="button">Löschen</button>
                  )}
                  {desktop && canExport(request.status) && (
                    <button className="secondary-button" disabled={busy} onClick={() => { setDeleteId(null); setRejectionId(null); void exportRequest(request.id); }} type="button">
                      {request.status === "Entwurf" ? "PDF exportieren" : "PDF erneut exportieren"}
                    </button>
                  )}
                </div>
                )}
                {request.status === "Entwurf" && !showDeleted && <p className="save-note">Genehmigen ist erst nach dem PDF-Export möglich.</p>}
                {exportMessage?.id === request.id && <p role={exportMessage.kind === "error" ? "alert" : "status"}>{exportMessage.message}</p>}
                {deleteId === request.id && (
                  <div role="group" aria-label="Löschen bestätigen">
                    <p>Diesen Antrag wirklich löschen? Er wird aus der Historie entfernt; der Statusverlauf und eine exportierte PDF-Datei bleiben erhalten.</p>
                    <button className="secondary-button" disabled={busy} type="button" onClick={() => { void changeStatus(request.id, "Gelöscht").then((done) => { if (done) setDeleteId(null); }); }}>Löschen bestätigen</button>
                    <button className="secondary-button" type="button" onClick={() => { setDeleteId(null); setStatusError(null); }}>Abbrechen</button>
                  </div>
                )}
                {rejectionId === request.id && (
                  <div>
                    <label>Ablehnungsgrund <input value={rejectionReason} onChange={(event) => setRejectionReason(event.target.value)} /></label>
                    <button className="secondary-button" disabled={busy} type="button" onClick={() => {
                      void changeStatus(request.id, "Abgelehnt", rejectionReason).then((done) => { if (done) { setRejectionId(null); setRejectionReason(""); } });
                    }}>Ablehnung bestätigen</button>
                    <button className="secondary-button" type="button" onClick={() => { setRejectionId(null); setStatusError(null); }}>Abbrechen</button>
                  </div>
                )}
              </article>
            ))}
          </div>
        )}
      </section>
    </div>
  );
}

function AppearancePanel({ onChange, preference }: { onChange: (next: ThemePreference) => void; preference: ThemePreference }) {
  return (
    <div className="content-area settings-layout">
      <section className="workspace-section">
        <div className="section-heading"><div><p className="eyebrow">Darstellung</p><h2>Hell- und Dunkelmodus</h2></div></div>
        <div className="view-toggle" role="radiogroup" aria-label="Farbschema">
          {THEME_OPTIONS.map((option) => (
            <button aria-checked={preference === option.value} className={preference === option.value ? "is-selected" : ""} key={option.value} onClick={() => onChange(option.value)} role="radio" type="button">{option.label}</button>
          ))}
        </div>
        <p className="save-note">Die Auswahl gilt sofort und wird auf diesem Gerät gespeichert.</p>
      </section>
    </div>
  );
}

/** Übernahme von Altdaten der bisherigen Anwendung (REQ-MIG-001…005, REQ-MIG-007). */
function MigrationPanel({ disabled, onImported }: { disabled: boolean; onImported: (state: DesktopState) => void }) {
  const [selection, setSelection] = useState<MigrationSelection | null>(null);
  const [confirmSkipped, setConfirmSkipped] = useState(false);
  const [confirmSettings, setConfirmSettings] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ kind: "status" | "alert"; text: string; details?: string[]; report?: boolean } | null>(null);
  const [reportMessage, setReportMessage] = useState<string | null>(null);
  const saveReport = async () => {
    const result = await desktopStore.exportMigrationReport(toDateKey(new Date()));
    setReportMessage(!result.ok ? `Der Bericht konnte nicht gespeichert werden: ${result.error}` : result.value === null ? "Speichern abgebrochen." : `Bericht gespeichert: ${result.value}`);
  };

  const choose = async () => {
    if (busy) return;
    setBusy(true);
    setMessage(null);
    try {
      const result = await desktopStore.previewMigration();
      if (!result.ok) { setSelection(null); setMessage({ kind: "alert", text: `Die Datei konnte nicht gelesen werden. Es wurde nichts verändert. ${result.error}` }); return; }
      setSelection(result.value);
      setConfirmSkipped(false);
      setConfirmSettings(false);
      if (result.value === null) setMessage({ kind: "status", text: "Auswahl abgebrochen." });
    } finally {
      setBusy(false);
    }
  };

  const runImport = async () => {
    if (!selection || busy) return;
    setBusy(true);
    try {
      const result = await desktopStore.importMigration(selection, confirmSkipped);
      if (!result.ok) { setMessage({ kind: "alert", text: result.error }); return; }
      const done = result.value;
      onImported(done.state);
      setSelection(null);
      setMessage({
        kind: "status",
        text: done.alreadyImported ? `${done.fileName} wurde bereits früher importiert; es wurde nichts doppelt übernommen.` : `${done.fileName} importiert.`,
        details: [
          `Einstellungen übernommen: ${done.settingsImported ? "ja" : "nein"}`,
          `Anträge: ${done.requestsImported}, Statusereignisse: ${done.statusEventsImported}, PDF-Nachweise: ${done.exportsImported}`,
          ...done.reconciliation.years.map((year) => `Abgleich ${year.year}: ${year.importedRequests} von ${year.sourceRequests} Anträgen, ${formatHalfUnits(year.importedHalfUnits)} von ${formatHalfUnits(year.sourceHalfUnits)} Tagen, davon genehmigt ${formatHalfUnits(year.importedApprovedHalfUnits)} von ${formatHalfUnits(year.sourceApprovedHalfUnits)}`),
          ...(done.reconciliation.differences.length ? done.reconciliation.differences.map((text) => `Abweichung: ${text}`) : ["Abgleich: keine Abweichungen."]),
          `Sicherung der Altdatei: ${done.sourceBackup}`,
          `Sicherung der Datenbank vor dem Import: ${done.databaseBackup}`,
        ],
        report: true,
      });
      setReportMessage(null);
    } finally {
      setBusy(false);
    }
  };

  const preview = selection?.preview;
  const blocked = !preview || !preview.canImport || (preview.needsConfirmation && !confirmSkipped) || (preview.replacesSettings && !confirmSettings);

  return (
    <div className="content-area settings-layout">
      <section className="workspace-section">
        <div className="section-heading"><div><p className="eyebrow">Altdaten</p><h2>Daten aus der bisherigen Anwendung übernehmen</h2></div></div>
        <p className="save-note">Unterstützt: settings.json, ledger.csv, history.csv und history.jsonl. Die Originaldatei wird nur gelesen und nie verändert. Vor dem Import werden die Datei und die aktuelle Datenbank im Ordner „Sicherungen“ gesichert. Ein erneuter Import derselben Datei legt nichts doppelt an.</p>
        <div className="form-actions">
          <button className="secondary-button" disabled={disabled || busy} onClick={() => { void choose(); }} type="button">Altdatei auswählen …</button>
        </div>
        {preview && (
          <div className="migration-preview" aria-label="Importvorschau">
            <p><strong>{preview.fileName}</strong> · {preview.format}</p>
            <dl className="diagnostics-list">
              <dt>Gelesen</dt><dd>{preview.recordsRead}</dd>
              <dt>Bereit</dt><dd>{preview.recordsReady}</dd>
              <dt>Übersprungen</dt><dd>{preview.recordsSkipped}</dd>
              <dt>Fehlerhaft</dt><dd>{preview.recordsFailed}</dd>
              <dt>Prüfsumme</dt><dd>{preview.sourceSha256.slice(0, 16)}…</dd>
            </dl>
            {preview.issues.length > 0 && (
              <details open={preview.recordsFailed > 0}>
                <summary>Hinweise und Fehler ({preview.issues.length}{preview.issuesTruncated ? "+" : ""})</summary>
                <ul className="diagnostics-errors">
                  {preview.issues.map((issue, index) => (
                    <li key={index}><span className="diagnostics-meta">Datensatz {issue.recordNumber} · {issue.kind}</span><span>{issue.message}</span></li>
                  ))}
                </ul>
              </details>
            )}
            {!preview.canImport && <p role="alert">Diese Datei kann nicht importiert werden, solange fehlerhafte Datensätze enthalten sind oder nichts importierbar ist. Bitte die genannten Datensätze in einer Kopie prüfen.</p>}
            {preview.canImport && preview.needsConfirmation && (
              <label><input checked={confirmSkipped} onChange={(event) => setConfirmSkipped(event.target.checked)} type="checkbox" /> Ich bestätige, dass die {preview.recordsSkipped} übersprungenen Datensätze nicht übernommen werden.</label>
            )}
            {preview.canImport && preview.replacesSettings && (
              <label><input checked={confirmSettings} onChange={(event) => setConfirmSettings(event.target.checked)} type="checkbox" /> Die aktuellen Einstellungen werden durch die Einstellungen aus der Datei ersetzt.</label>
            )}
            <div className="form-actions">
              <button className="secondary-button" disabled={busy} onClick={() => { setSelection(null); setMessage(null); }} type="button">Verwerfen</button>
              <button className="primary-button" disabled={busy || blocked} onClick={() => { void runImport(); }} type="button">Importieren</button>
            </div>
          </div>
        )}
        {message && (
          <div role={message.kind}>
            <p>{message.text}</p>
            {message.details && <ul>{message.details.map((line) => <li key={line}>{line}</li>)}</ul>}
            {message.report && (
              <div className="form-actions">
                <button className="secondary-button" disabled={busy} onClick={() => { void saveReport(); }} type="button">Bericht speichern …</button>
              </div>
            )}
            {reportMessage && <p role="status">{reportMessage}</p>}
          </div>
        )}
      </section>
    </div>
  );
}

function BackupPanel({ disabled, onBackup, onRestore }: { disabled: boolean; onBackup: () => Promise<string>; onRestore: () => Promise<string> }) {
  const [busy, setBusy] = useState(false);
  const [confirming, setConfirming] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const run = async (action: () => Promise<string>) => {
    if (busy) return;
    setBusy(true);
    setMessage(null);
    try {
      setMessage(await action());
    } finally {
      setBusy(false);
      setConfirming(false);
    }
  };
  return (
    <div className="content-area settings-layout">
      <section className="workspace-section">
        <div className="section-heading">
          <div>
            <p className="eyebrow">Datensicherung</p>
            <h2>Sicherung und Wiederherstellung</h2>
          </div>
        </div>
        <p className="save-note">Eine Sicherung enthält Einstellungen, alle Anträge, den Statusverlauf und die Exportnachweise. PDF-Dateien selbst sind nicht enthalten.</p>
        <div className="form-actions">
          <button className="secondary-button" disabled={disabled || busy} onClick={() => { void run(onBackup); }} type="button">Sicherung erstellen</button>
          <button className="secondary-button" disabled={disabled || busy} onClick={() => { setMessage(null); setConfirming(true); }} type="button">Sicherung wiederherstellen</button>
        </div>
        {confirming && (
          <div role="group" aria-label="Wiederherstellung bestätigen">
            <p>Die Wiederherstellung ersetzt alle aktuellen Daten durch die gewählte Sicherung. Der jetzige Stand wird vorher automatisch gesichert.</p>
            <button className="secondary-button" disabled={busy} onClick={() => { void run(onRestore); }} type="button">Sicherung auswählen und wiederherstellen</button>
            <button className="secondary-button" disabled={busy} onClick={() => setConfirming(false)} type="button">Abbrechen</button>
          </div>
        )}
        {message && <p role="status">{message}</p>}
      </section>
    </div>
  );
}

const WEEKDAY_LABELS = ["Mo", "Di", "Mi", "Do", "Fr", "Sa", "So"];
const VOCATIONAL_OPTIONS = [
  { value: 0, label: "Keine" },
  { value: 1, label: "Halbtags" },
  { value: 2, label: "Ganztags" },
];

function formatImportTime(utc: string): string {
  const date = new Date(utc);
  return Number.isNaN(date.getTime()) ? utc : new Intl.DateTimeFormat("de-DE", { dateStyle: "medium", timeStyle: "short" }).format(date);
}

function SettingsView({ calendar, onImportHolidays, onSave, settings, storageError }: {
  calendar: CalendarStatus | null;
  onImportHolidays: (() => Promise<string>) | null;
  onSave: (next: Settings) => void | Promise<void>;
  settings: Settings;
  storageError: boolean;
}) {
  const [form, setForm] = useState<Settings>(settings);
  const [importBusy, setImportBusy] = useState(false);
  const [importMessage, setImportMessage] = useState<string | null>(null);
  const week = form.vocationalSchoolWeek ?? [...NO_VOCATIONAL_SCHOOL];
  const school = calendar?.schoolHolidays ?? null;
  const schoolForState = school !== null && school.federalState === settings.federalState ? school : null;
  const runImport = async () => {
    if (!onImportHolidays || importBusy) return;
    setImportBusy(true);
    setImportMessage(null);
    try {
      setImportMessage(await onImportHolidays());
    } finally {
      setImportBusy(false);
    }
  };
  const [annualEntitlementText, setAnnualEntitlementText] = useState(() => formatHalfUnits(settings.annualEntitlementHalfUnits));
  const [submitted, setSubmitted] = useState(false);

  useEffect(() => {
    setForm(settings);
    setAnnualEntitlementText(formatHalfUnits(settings.annualEntitlementHalfUnits));
  }, [settings]);

  const updateField = <K extends keyof Settings>(key: K, value: Settings[K]) => {
    setForm((current) => ({ ...current, [key]: value }));
  };

  const toggleWorkday = (index: number) => {
    setForm((current) => ({
      ...current,
      workdays: current.workdays.map((value, currentIndex) => (currentIndex === index ? !value : value)),
    }));
  };

  const annualEntitlementHalfUnits = parseHalfUnits(annualEntitlementText);
  const settingsError = annualEntitlementHalfUnits === null || annualEntitlementHalfUnits === 0
    ? "Der Jahresurlaub muss größer als 0 und in halben Tagen angegeben sein."
    : !form.workdays.some(Boolean)
      ? "Bitte mindestens einen Arbeitstag auswählen."
      : !form.federalState
        ? "Bitte ein Bundesland auswählen."
        : null;

  return (
    <div className="content-area settings-layout">
      <section className="workspace-section">
        <div className="section-heading">
          <div>
            <p className="eyebrow">Grundlagen</p>
            <h2>Persönliche Angaben</h2>
          </div>
        </div>
        <div className="settings-fields">
          <label>
            Vorname
            <input value={form.firstName} onChange={(event) => updateField("firstName", event.target.value)} placeholder="" />
          </label>
          <label>
            Nachname
            <input value={form.lastName} onChange={(event) => updateField("lastName", event.target.value)} placeholder="" />
          </label>
          <label>
            Straße, PLZ und Ort
            <input value={form.address ?? ""} onChange={(event) => updateField("address", event.target.value)} placeholder="Optional" />
          </label>
          <label>
            Personalnummer
            <input value={form.personnelNumber ?? ""} onChange={(event) => updateField("personnelNumber", event.target.value)} placeholder="Optional" />
          </label>
          <label>
            Abteilung
            <input value={form.department} onChange={(event) => updateField("department", event.target.value)} placeholder="Optional" />
          </label>
          <label>
            Klasse
            <input value={form.className} onChange={(event) => updateField("className", event.target.value)} placeholder="Optional" />
          </label>
          <label>
            Jahresurlaub
            <input
              aria-invalid={submitted && !!settingsError}
              inputMode="decimal"
              onChange={(event) => setAnnualEntitlementText(event.target.value)}
              placeholder="Tage"
              value={annualEntitlementText}
            />
          </label>
          <label>
            Bundesland
            <select value={form.federalState} onChange={(event) => updateField("federalState", event.target.value)}>
              <option value="">Auswählen</option>
              {Object.keys(stateHolidayCodes).map((state) => (
                <option key={state} value={state}>{state}</option>
              ))}
            </select>
          </label>
        </div>
        <div className="workweek-row">
          <span>Arbeitstage</span>
          <div className="weekday-options">
            {[
              { label: "Mo", id: 0 },
              { label: "Di", id: 1 },
              { label: "Mi", id: 2 },
              { label: "Do", id: 3 },
              { label: "Fr", id: 4 },
              { label: "Sa", id: 5 },
              { label: "So", id: 6 },
            ].map((day) => (
              <label key={day.label}>
                <input checked={form.workdays[day.id]} onChange={() => toggleWorkday(day.id)} type="checkbox" />
                {day.label}
              </label>
            ))}
          </div>
        </div>
        <div className="workweek-row">
          <label><input checked={form.studentMode ?? false} onChange={(event) => updateField("studentMode", event.target.checked)} type="checkbox" /> Berufsschulmodus (Auszubildende)</label>
        </div>
        {form.studentMode && (
          <fieldset className="vocational-week">
            <legend>Berufsschule je Wochentag</legend>
            <p className="save-note">Ganztägige Berufsschule ist kein Urlaubstag; halbtags zählt ein halber Tag. In importierten Schulferien gilt der Tag als normaler Arbeitstag.</p>
            <div className="vocational-days">
              {WEEKDAY_LABELS.map((label, index) => (
                <label key={label}>
                  {label}
                  <select
                    onChange={(event) => updateField("vocationalSchoolWeek", week.map((value, current) => (current === index ? Number(event.target.value) : value)))}
                    value={week[index]}
                  >
                    {VOCATIONAL_OPTIONS.map((option) => <option key={option.value} value={option.value}>{option.label}</option>)}
                  </select>
                </label>
              ))}
            </div>
          </fieldset>
        )}
        <div className="workweek-row">
          <label><input checked={form.includeOptionalHolidays} onChange={(event) => updateField("includeOptionalHolidays", event.target.checked)} type="checkbox" /> Regionale Feiertage berücksichtigen (gelten nur in Teilen des Landes, z. B. Mariä Himmelfahrt in Bayern)</label>
        </div>
        {submitted && settingsError && <p role="alert">{settingsError}</p>}
        <div className="form-actions">
          <span className="save-note">Änderungen werden lokal gespeichert.</span>
          <button className="primary-button" disabled={storageError} onClick={() => { setSubmitted(true); if (!settingsError && !storageError && annualEntitlementHalfUnits !== null) void onSave({ ...form, annualEntitlementHalfUnits }); }} type="button">Speichern</button>
        </div>
      </section>
      <section className="source-strip" aria-label="Kalenderdaten">
        <span className="source-marker" aria-hidden="true" />
        <div>
          <strong>Kalenderdaten</strong>
          <p>Feiertage: {calendar?.publicHolidaySource ?? "Gesetzliche Feiertage werden offline berechnet."}</p>
          {schoolForState ? (
            <p>
              Schulferien {schoolForState.federalState}: {schoolForState.breakCount} Zeiträume vom {formatGermanDate(schoolForState.firstDate)} bis {formatGermanDate(schoolForState.lastDate)}
              {" · "}Datei {schoolForState.fileName}, importiert am {formatImportTime(schoolForState.importedAtUtc)}
              {schoolForState.latestEventStampUtc ? `, Datenstand der Quelle ${formatImportTime(schoolForState.latestEventStampUtc)}` : ""}
            </p>
          ) : (
            <p>Schulferien {settings.federalState || "–"}: nicht importiert. Die ICS-Datei des Bundeslands gibt es im Ferienkalender der KMK (kmk.org → Ferienkalender).</p>
          )}
          {onImportHolidays && (
            <button className="secondary-button" disabled={storageError || importBusy || !settings.federalState} onClick={() => { void runImport(); }} type="button">
              Ferienkalender (ICS) importieren …
            </button>
          )}
          {importMessage && <p role="status">{importMessage}</p>}
        </div>
        <span className="source-state">{schoolForState ? "Importiert" : "Nicht importiert"}</span>
      </section>
    </div>
  );
}

function loadSettings(): { settings: Settings; error: boolean } {
  try {
    return readVersionedSettings(window.localStorage, SETTINGS_KEY, LEGACY_SETTINGS_KEY, defaultSettings);
  } catch {
    return { settings: defaultSettings, error: true };
  }
}

function loadRequests(): { requests: RequestRecord[]; error: string | null } {
  try {
    const result = readVersionedRequests(window.localStorage, REQUESTS_KEY, LEGACY_REQUESTS_KEY);
    if (result.error) throw new Error("Ungültige Antragsdaten");
    return { requests: result.requests, error: null };
  } catch {
    return { requests: [], error: "Antragsdaten konnten nicht gelesen werden. Sie wurden nicht überschrieben; bitte die Daten sichern und prüfen." };
  }
}

function isWorkingDate(date: Date, settings: Settings): boolean {
  if (!settings.workdays.length) {
    return false;
  }

  const weekday = date.getDay();
  const isoIndex = (weekday + 6) % 7;
  return settings.workdays[isoIndex];
}

function addDays(date: Date, days: number): Date {
  const next = new Date(date);
  next.setDate(next.getDate() + days);
  return next;
}

function parseDate(value: string): Date | null {
  const date = new Date(`${value}T00:00:00`);
  return Number.isNaN(date.getTime()) ? null : date;
}

/**
 * Datumsfeld im Format TT.MM.JJJJ, unabhängig von der Systemsprache. Nach außen
 * werden nur gültige Daten (JJJJ-MM-TT) gemeldet; eine unvollständige oder
 * ungültige Eingabe bleibt sichtbar und wird markiert, ändert den Wert aber nicht.
 */
function DateInput({ allowEmpty = false, label, onChange, onValidityChange, value }: { allowEmpty?: boolean; label: string; onChange: (value: string) => void; onValidityChange?: (valid: boolean) => void; value: string }) {
  const [text, setText] = useState(() => formatGermanDate(value));
  const [lastValue, setLastValue] = useState(value);
  if (value !== lastValue) {
    setLastValue(value);
    if (parseGermanDate(text) !== value) setText(formatGermanDate(value));
  }
  const parsed = parseGermanDate(text);
  const invalid = text.trim() !== "" ? parsed === null : !allowEmpty;
  useEffect(() => { onValidityChange?.(!invalid); }, [invalid, onValidityChange]);
  return (
    <input
      aria-invalid={invalid}
      aria-label={label}
      autoComplete="off"
      inputMode="numeric"
      maxLength={10}
      onBlur={() => { if (parsed) setText(formatGermanDate(parsed)); }}
      onChange={(event) => {
        const next = event.target.value;
        setText(next);
        const nextValue = parseGermanDate(next);
        if (nextValue !== null) onChange(nextValue);
        else if (allowEmpty && next.trim() === "") onChange("");
      }}
      placeholder="TT.MM.JJJJ"
      title={invalid ? "Bitte ein gültiges Datum im Format TT.MM.JJJJ eingeben." : undefined}
      type="text"
      value={text}
    />
  );
}

function toDateKey(date: Date): string {
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

function nextIsoDate(offsetDays = 0): string {
  const date = new Date();
  date.setDate(date.getDate() + offsetDays);
  return toDateKey(date);
}

function formatRange(start: string, end: string): string {
  const startValue = parseDate(start);
  const endValue = parseDate(end);
  if (!startValue || !endValue) {
    return "Unbekannter Zeitraum";
  }

  const formatter = new Intl.DateTimeFormat("de-DE", { day: "2-digit", month: "2-digit" });
  return `${formatter.format(startValue)} – ${formatter.format(endValue)}`;
}

function StatusPill({ status }: { status: string }) {
  return <span className="state-pill" style={{ background: statusColor(status as RequestStatus) }}>{status}</span>;
}

function statusColor(status: RequestStatus): string {
  switch (status) {
    case "Genehmigt":
      return "var(--status-genehmigt)";
    case "Exportiert":
      return "var(--status-exportiert)";
    case "Abgelehnt":
      return "var(--status-abgelehnt)";
    case "Archiviert":
      return "var(--status-archiviert)";
    default:
      return "var(--status-entwurf)";
  }
}

export default App;