import { countsAsApproved, type RequestRecord } from "./requests.ts";

/** Kalenderfakten eines Tages aus dem Backend (gleiche Quelle wie die Berechnung). */
export type CalendarDay = {
  date: string;
  isWorkday: boolean;
  publicHoliday: string | null;
  holidayIsOptional: boolean;
  isSchoolVacation: boolean | null;
  vocationalSchool: string | null;
};

export type CalendarCategory =
  | "workday"
  | "weekend"
  | "holiday"
  | "vacation"
  | "school"
  | "aza"
  | "approved"
  | "pending"
  | "rejected";

export type CalendarColors = Record<CalendarCategory, string>;

export const CALENDAR_LEGEND: { category: CalendarCategory; label: string }[] = [
  { category: "workday", label: "Arbeitstag" },
  { category: "weekend", label: "Arbeitsfrei/Wochenende" },
  { category: "holiday", label: "Feiertag" },
  { category: "vacation", label: "Schulferien" },
  { category: "school", label: "Berufsschule" },
  { category: "aza", label: "AZA" },
  { category: "approved", label: "Genehmigt" },
  { category: "pending", label: "Beantragt (Entwurf/Exportiert)" },
  { category: "rejected", label: "Abgelehnt" },
];

export const DEFAULT_CALENDAR_COLORS: CalendarColors = {
  workday: "#ffffff",
  weekend: "#eef1ec",
  holiday: "#f6dede",
  vacation: "#dfeaf6",
  school: "#f6ecd2",
  aza: "#ece7f6",
  approved: "#cfe8d6",
  pending: "#fbe3c4",
  rejected: "#e3e3e3",
};

/** Standardfarben im Dunkelmodus: gedeckte Töne, auf denen helle Schrift lesbar bleibt. */
export const DEFAULT_DARK_CALENDAR_COLORS: CalendarColors = {
  workday: "#1d221e",
  weekend: "#262c27",
  holiday: "#5a2e2e",
  vacation: "#26405a",
  school: "#584a22",
  aza: "#43365e",
  approved: "#255a3b",
  pending: "#6a4a1f",
  rejected: "#3d3d3d",
};

const HEX = /^#[0-9a-f]{6}$/i;

/** Liest gespeicherte Farben; ungültige Einträge fallen auf die Standardfarbe zurück. */
export function parseCalendarColors(raw: string | null, defaults: CalendarColors = DEFAULT_CALENDAR_COLORS): CalendarColors {
  const colors = { ...defaults };
  if (raw === null) return colors;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) return colors;
    for (const { category } of CALENDAR_LEGEND) {
      const value = (parsed as Record<string, unknown>)[category];
      if (typeof value === "string" && HEX.test(value)) colors[category] = value.toLowerCase();
    }
  } catch {
    // Beschädigte Werte: Standardfarben verwenden.
  }
  return colors;
}

function datesBetween(start: string, end: string): string[] {
  const [sy, sm, sd] = start.split("-").map(Number);
  const [ey, em, ed] = end.split("-").map(Number);
  const values: string[] = [];
  const current = new Date(Date.UTC(sy, sm - 1, sd));
  const last = Date.UTC(ey, em - 1, ed);
  if (Number.isNaN(current.getTime()) || Number.isNaN(last)) return values;
  for (let guard = 0; current.getTime() <= last && guard < 400; guard += 1) {
    values.push(current.toISOString().slice(0, 10));
    current.setUTCDate(current.getUTCDate() + 1);
  }
  return values;
}

type RequestMark = "approved" | "pending" | "rejected";
const MARK_PRIORITY: Record<RequestMark, number> = { approved: 3, pending: 2, rejected: 1 };

function markOf(request: RequestRecord): RequestMark | null {
  if (request.status === "Gelöscht") return null;
  if (countsAsApproved(request)) return "approved";
  if (request.status === "Entwurf" || request.status === "Exportiert") return "pending";
  if (request.status === "Abgelehnt" || request.status === "Archiviert") return "rejected";
  return null;
}

/** Antragsstatus je Datum; bei Überschneidung gewinnt Genehmigt vor Beantragt vor Abgelehnt. */
export function requestMarksByDate(requests: readonly RequestRecord[]): Map<string, RequestMark> {
  const marks = new Map<string, RequestMark>();
  for (const request of requests) {
    const mark = markOf(request);
    if (!mark) continue;
    for (const date of datesBetween(request.startDate, request.endDate)) {
      const current = marks.get(date);
      if (!current || MARK_PRIORITY[mark] > MARK_PRIORITY[current]) marks.set(date, mark);
    }
  }
  return marks;
}

/** AZA-Tage nicht gelöschter Anträge. */
export function azaDatesOf(requests: readonly RequestRecord[]): Set<string> {
  return new Set(requests.filter((request) => request.status !== "Gelöscht").flatMap((request) => request.azaDates ?? []));
}

/**
 * Kategorien eines Tages, wichtigste zuerst. Die erste bestimmt die Hintergrundfarbe;
 * alle erscheinen in der Beschreibung (Tooltip/Screenreader).
 */
export function dayCategories(day: CalendarDay, marks: Map<string, RequestMark>, aza: Set<string>): CalendarCategory[] {
  const result: CalendarCategory[] = [];
  const mark = marks.get(day.date);
  if (mark) result.push(mark);
  if (aza.has(day.date)) result.push("aza");
  if (day.publicHoliday) result.push("holiday");
  if (day.vocationalSchool) result.push("school");
  if (day.isSchoolVacation) result.push("vacation");
  result.push(day.isWorkday ? "workday" : "weekend");
  return result;
}

/** Lesbare Beschreibung eines Tages für Tooltip und Screenreader. */
export function describeCalendarDay(day: CalendarDay, categories: CalendarCategory[]): string {
  const [year, month, date] = day.date.split("-");
  const parts = [`${date}.${month}.${year}`];
  for (const category of categories) {
    if (category === "holiday") parts.push(`Feiertag: ${day.publicHoliday}${day.holidayIsOptional ? " (optional)" : ""}`);
    else if (category === "school") parts.push(`Berufsschule ${day.vocationalSchool?.toLowerCase()}`);
    else parts.push(CALENDAR_LEGEND.find((entry) => entry.category === category)?.label ?? category);
  }
  if (day.isSchoolVacation === null) parts.push("Schulferien unbekannt");
  return parts.join(" · ");
}
