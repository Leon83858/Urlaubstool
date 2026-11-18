export type DayDetail = {
  date: string;
  weekday: string;
  isWorkday: boolean;
  publicHoliday: string | null;
  isSchoolVacation: boolean | null;
  vocationalSchool: string | null;
  isAza: boolean;
  startHalfDay: boolean;
  endHalfDay: boolean;
  countedHalfUnits: number;
  reasons: string[];
};

export function describeDay(day: DayDetail): { title: string; counted: string; facts: string[]; reasons: string[] } {
  const [year, month, date] = day.date.split("-");
  const facts = [
    day.isWorkday ? "Arbeitstag" : "Kein Arbeitstag",
    `Feiertag: ${day.publicHoliday ?? "Nein"}`,
    `Schulferien: ${day.isSchoolVacation === null ? "unbekannt" : day.isSchoolVacation ? "Ja" : "Nein"}`,
    `Berufsschule: ${day.vocationalSchool ?? "nicht aktiv"}`,
    `AZA: ${day.isAza ? "Ja" : "Nein"}`,
  ];
  if (day.startHalfDay) facts.push("Halbtag am Start");
  if (day.endHalfDay) facts.push("Halbtag am Ende");
  return {
    title: `${day.weekday}, ${date}.${month}.${year}`,
    counted: `${Math.floor(day.countedHalfUnits / 2)},${day.countedHalfUnits % 2 ? "5" : "0"} Tage`,
    facts,
    reasons: day.reasons,
  };
}