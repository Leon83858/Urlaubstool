import { countsAsApproved, type RequestRecord } from "./requests.ts";

export function filterAndSortHistory<T extends { startDate: string; status: string }>(
  requests: readonly T[],
  year: string,
  status: string,
): T[] {
  return requests
    .filter((request) => request.startDate.slice(0, 4) === year && (status === "Alle Status" || request.status === status))
    .sort((left, right) => right.startDate.localeCompare(left.startDate));
}

/** Statuswechsel der Historie; maßgeblich ist die Prüfung im Backend (`RequestStatus::can_transition_to`). */
export function canTransition(current: string, next: string): boolean {
  return (next === "Genehmigt" && current === "Exportiert") ||
    (next === "Abgelehnt" && (current === "Exportiert" || current === "Genehmigt")) ||
    (next === "Archiviert" && current === "Abgelehnt");
}

/**
 * Löschen ist nur für Entwürfe, exportierte, abgelehnte und archivierte Anträge möglich,
 * aber nie für Anträge, die gegen den Jahresanspruch zählen (z. B. aus der Altanwendung
 * übernommene, nach Genehmigung archivierte Anträge).
 */
export function canDelete(current: string, countsAsApproved = false): boolean {
  if (countsAsApproved) return false;
  return current === "Entwurf" || current === "Exportiert" || current === "Abgelehnt" || current === "Archiviert";
}

/** Ein PDF lässt sich für Entwürfe erstellen und für exportierte oder genehmigte Anträge erneut erstellen. */
export function canExport(current: string): boolean {
  return current === "Entwurf" || current === "Exportiert" || current === "Genehmigt";
}

/** Status, in den ein gelöschter Antrag zurückkehrt; `null`, wenn er nicht bekannt ist. */
export function restoreTarget(request: { statusEvents?: readonly { status: string; previousStatus?: string }[] }): string | null {
  const deletion = [...(request.statusEvents ?? [])].reverse().find((event) => event.status === "Gelöscht");
  return deletion?.previousStatus && deletion.previousStatus !== "Gelöscht" ? deletion.previousStatus : null;
}

/**
 * Status, in den ein archivierter Antrag beim Ent-Archivieren zurückkehrt (Entscheidung
 * 12.11.2026): „Genehmigt“, wenn er vor dem Archivieren genehmigt war (Altfälle), sonst
 * „Abgelehnt“. `null` für nicht archivierte Anträge. Maßgeblich prüft das Backend.
 */
export function unarchiveTarget(request: Pick<RequestRecord, "status" | "countsAsApproved" | "statusEvents" | "rejectionReason">): "Genehmigt" | "Abgelehnt" | null {
  if (request.status !== "Archiviert") return null;
  return countsAsApproved(request) ? "Genehmigt" : "Abgelehnt";
}
