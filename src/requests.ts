import { halfUnitsFromLegacy } from "./half-units.ts";

export type RequestStatus = "Entwurf" | "Exportiert" | "Genehmigt" | "Abgelehnt" | "Archiviert" | "Gelöscht";

export type DraftRequest = {
  startDate: string;
  endDate: string;
  startHalfDay: boolean;
  endHalfDay: boolean;
  azaDates: string[];
};

export type StatusEvent = {
  status: RequestStatus;
  occurredAtUtc: string;
  previousStatus?: RequestStatus;
  rejectionReason?: string;
};

export type RequestRecord = {
  id: string;
  startDate: string;
  endDate: string;
  startHalfDay: boolean;
  endHalfDay: boolean;
  status: RequestStatus;
  requestedHalfUnits: number;
  /** Vom Backend geliefert; fehlt nur bei Daten der Web-Vorschau. */
  countsAsApproved?: boolean;
  calculationVersion?: number;
  rejectionReason?: string;
  azaDates?: string[];
  createdAtUtc?: string;
  updatedAtUtc?: string;
  exportPath?: string;
  exportedAtUtc?: string;
  statusEvents?: StatusEvent[];
};

/**
 * Zählt der Antrag gegen den Jahresanspruch? Genehmigte immer, archivierte nur,
 * wenn sie vor dem Archivieren genehmigt waren (Entscheidung E4a).
 */
export function countsAsApproved(request: Pick<RequestRecord, "status" | "countsAsApproved" | "statusEvents" | "rejectionReason">): boolean {
  if (typeof request.countsAsApproved === "boolean") return request.countsAsApproved;
  if (request.status === "Genehmigt") return true;
  if (request.status !== "Archiviert") return false;
  const archived = [...(request.statusEvents ?? [])].reverse().find((event) => event.status === "Archiviert");
  return archived?.previousStatus ? archived.previousStatus === "Genehmigt" : !request.rejectionReason;
}

/** Summe der halben Tage, die im Jahr gegen den Anspruch zählen. */
export function approvedHalfUnits(requests: readonly RequestRecord[], year: string, excludingId?: string): number {
  return requests
    .filter((request) => request.id !== excludingId && request.startDate.slice(0, 4) === year && countsAsApproved(request))
    .reduce((sum, request) => sum + request.requestedHalfUnits, 0);
}

function isCalendarDate(value: unknown): value is string {
  if (typeof value !== "string" || !/^\d{4}-\d{2}-\d{2}$/.test(value)) return false;
  const parsed = new Date(`${value}T00:00:00Z`);
  return !Number.isNaN(parsed.getTime()) && parsed.toISOString().slice(0, 10) === value;
}

export function createRequestRecord(draft: DraftRequest, requestedHalfUnits: number, id: string, timestamp: string, exportPath?: string): RequestRecord {
  const statusEvents: StatusEvent[] = [{ status: "Entwurf", occurredAtUtc: timestamp }];
  if (exportPath) statusEvents.push({ status: "Exportiert", previousStatus: "Entwurf", occurredAtUtc: timestamp });
  return {
    id, ...draft, azaDates: [...draft.azaDates], requestedHalfUnits, calculationVersion: 1,
    status: exportPath ? "Exportiert" : "Entwurf", createdAtUtc: timestamp, updatedAtUtc: timestamp,
    ...(exportPath ? { exportPath, exportedAtUtc: timestamp } : {}), statusEvents,
  };
}

export function appendStatusEvent(request: RequestRecord, status: RequestStatus, timestamp: string, rejectionReason?: string): RequestRecord {
  const reason = rejectionReason?.trim();
  const event: StatusEvent = { status, previousStatus: request.status, occurredAtUtc: timestamp, ...(reason ? { rejectionReason: reason } : {}) };
  return {
    ...request, status, updatedAtUtc: timestamp, statusEvents: [...(request.statusEvents ?? []), event],
    ...(status === "Abgelehnt" && reason ? { rejectionReason: reason } : {}),
  };
}

export function parseRequestsV2(raw: string | null): { requests: RequestRecord[]; error: boolean } {
  if (raw === null) return { requests: [], error: false };
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) throw new Error("Invalid requests");
    const requests: RequestRecord[] = parsed.map((item: unknown) => {
      if (!item || typeof item !== "object" || Array.isArray(item)) throw new Error("Invalid request");
      const values = item as Record<string, unknown>;
      const legacy = "days" in values ? halfUnitsFromLegacy(values.days) : undefined;
      if (legacy === null) throw new Error("Invalid legacy days");
      const units = "requestedHalfUnits" in values ? values.requestedHalfUnits : legacy;
      if (typeof values.id !== "string" || !values.id ||
          !isCalendarDate(values.startDate) || !isCalendarDate(values.endDate) || values.startDate > values.endDate ||
          typeof values.startHalfDay !== "boolean" || typeof values.endHalfDay !== "boolean" ||
          typeof values.status !== "string" || !["Entwurf", "Exportiert", "Genehmigt", "Abgelehnt", "Archiviert"].includes(values.status) ||
          typeof units !== "number" || !Number.isInteger(units) || units < 0 || units > 4294967295 ||
          (legacy !== undefined && legacy !== units) ||
          (values.calculationVersion !== undefined && typeof values.calculationVersion !== "number") ||
          (values.countsAsApproved !== undefined && typeof values.countsAsApproved !== "boolean") ||
          (values.rejectionReason !== undefined && typeof values.rejectionReason !== "string") ||
          (values.createdAtUtc !== undefined && (typeof values.createdAtUtc !== "string" || Number.isNaN(Date.parse(values.createdAtUtc)))) ||
          (values.updatedAtUtc !== undefined && (typeof values.updatedAtUtc !== "string" || Number.isNaN(Date.parse(values.updatedAtUtc)))) ||
          (values.exportedAtUtc !== undefined && (typeof values.exportedAtUtc !== "string" || Number.isNaN(Date.parse(values.exportedAtUtc)))) ||
          (values.exportPath !== undefined && typeof values.exportPath !== "string")) throw new Error("Invalid request fields");
      if (values.statusEvents !== undefined && (!Array.isArray(values.statusEvents) || !values.statusEvents.every((event: unknown) => {
        if (!event || typeof event !== "object" || Array.isArray(event)) return false;
        const entry = event as Record<string, unknown>;
        return typeof entry.status === "string" && ["Entwurf", "Exportiert", "Genehmigt", "Abgelehnt", "Archiviert"].includes(entry.status) &&
          typeof entry.occurredAtUtc === "string" && !Number.isNaN(Date.parse(entry.occurredAtUtc)) &&
          (entry.previousStatus === undefined || (typeof entry.previousStatus === "string" && ["Entwurf", "Exportiert", "Genehmigt", "Abgelehnt", "Archiviert"].includes(entry.previousStatus))) &&
          (entry.rejectionReason === undefined || typeof entry.rejectionReason === "string");
      }))) throw new Error("Invalid status events");
      if (values.azaDates !== undefined && (!Array.isArray(values.azaDates) ||
          !values.azaDates.every((date: unknown) => isCalendarDate(date) && date >= (values.startDate as string) && date <= (values.endDate as string)) ||
          new Set(values.azaDates).size !== values.azaDates.length)) throw new Error("Invalid AZA dates");
      const { days: _legacy, ...remaining } = values;
      return { ...remaining, requestedHalfUnits: units } as RequestRecord;
    });
    if (new Set(requests.map((request) => request.id)).size !== requests.length) throw new Error("Duplicate request ID");
    return { requests, error: false };
  } catch {
    return { requests: [], error: true };
  }
}

export function readVersionedRequests(storage: Pick<Storage, "getItem">, key: string, legacyKey: string): { requests: RequestRecord[]; error: boolean } {
  try {
    const stored = storage.getItem(key);
    return parseRequestsV2(stored === null ? storage.getItem(legacyKey) : stored);
  } catch {
    return { requests: [], error: true };
  }
}