import { countsAsApproved, type RequestRecord } from "./requests.ts";

/** Ein Antrag in der Jahresübersicht. */
export type YearRequest = { id: string; startDate: string; endDate: string; status: string; requestedHalfUnits: number; countsAsApproved: boolean };

/**
 * Summen eines Urlaubsjahres (REQ-UI-005). In der Desktop-App kommen sie aus dem
 * Backend (`year_summary`, REQ-ARCH-002); `localYearSummary` dient nur der
 * Web-Vorschau und folgt denselben Regeln.
 */
export type YearSummary = {
  year: number;
  entitlementHalfUnits: number;
  approvedHalfUnits: number;
  requestedHalfUnits: number;
  availableHalfUnits: number;
  requests: YearRequest[];
};

const OPEN_STATUSES = new Set(["Entwurf", "Exportiert"]);

export function localYearSummary(requests: readonly RequestRecord[], year: number, entitlementHalfUnits: number): YearSummary {
  const inYear = requests
    .filter((request) => request.status !== "Gelöscht" && request.startDate.slice(0, 4) === String(year))
    .sort((left, right) => left.startDate.localeCompare(right.startDate) || left.id.localeCompare(right.id));
  const approvedHalfUnits = inYear.filter((request) => countsAsApproved(request)).reduce((sum, request) => sum + request.requestedHalfUnits, 0);
  const requestedHalfUnits = inYear.filter((request) => OPEN_STATUSES.has(request.status)).reduce((sum, request) => sum + request.requestedHalfUnits, 0);
  return {
    year,
    entitlementHalfUnits,
    approvedHalfUnits,
    requestedHalfUnits,
    availableHalfUnits: entitlementHalfUnits - approvedHalfUnits,
    requests: inYear.map((request) => ({
      id: request.id,
      startDate: request.startDate,
      endDate: request.endDate,
      status: request.status,
      requestedHalfUnits: request.requestedHalfUnits,
      countsAsApproved: countsAsApproved(request),
    })),
  };
}
