/** Interne Kalenderdaten sind JJJJ-MM-TT; angezeigt und eingegeben wird TT.MM.JJJJ. */

function isRealDate(year: number, month: number, day: number): boolean {
  if (year < 1900 || year > 2999 || month < 1 || month > 12 || day < 1) return false;
  const daysInMonth = new Date(Date.UTC(year, month, 0)).getUTCDate();
  return day <= daysInMonth;
}

/** „2026-03-16“ → „16.03.2026“; ungültige Werte bleiben unverändert. */
export function formatGermanDate(iso: string): string {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(iso);
  return match ? `${match[3]}.${match[2]}.${match[1]}` : iso;
}

/**
 * „16.03.2026“ oder „16.3.2026“ → „2026-03-16“. Gibt `null` zurück, wenn die Eingabe
 * kein vollständiges, existierendes Datum im Format TT.MM.JJJJ ist.
 */
export function parseGermanDate(text: string): string | null {
  const match = /^\s*(\d{1,2})\.(\d{1,2})\.(\d{4})\s*$/.exec(text);
  if (!match) return null;
  const day = Number(match[1]);
  const month = Number(match[2]);
  const year = Number(match[3]);
  if (!isRealDate(year, month, day)) return null;
  return `${year}-${String(month).padStart(2, "0")}-${String(day).padStart(2, "0")}`;
}
