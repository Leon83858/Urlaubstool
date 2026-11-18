export function addAzaDate(dates: readonly string[], date: string, start: string, end: string): { dates: string[]; error: string | null } {
  if (!date || !start || !end || date < start || date > end) {
    return { dates: [...dates], error: "Der AZA-Tag muss innerhalb des Antragszeitraums liegen." };
  }
  if (dates.includes(date)) {
    return { dates: [...dates], error: "Dieser AZA-Tag ist bereits enthalten." };
  }
  return { dates: [...dates, date].sort(), error: null };
}

export function retainAzaDates(dates: readonly string[], start: string, end: string): { dates: string[]; removed: string[] } {
  if (!start || !end || start > end) return { dates: [...dates], removed: [] };
  return {
    dates: dates.filter((date) => date >= start && date <= end),
    removed: dates.filter((date) => date < start || date > end),
  };
}