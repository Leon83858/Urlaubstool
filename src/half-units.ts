const MAX_HALF_UNITS = 4294967295;

export function parseHalfUnits(value: string): number | null {
  const match = /^(\d+)(?:[.,]([05]))?$/.exec(value.trim());
  if (!match) return null;
  const whole = Number(match[1]);
  if (!Number.isSafeInteger(whole) || whole > Math.floor(MAX_HALF_UNITS / 2)) return null;
  const units = whole * 2 + (match[2] === "5" ? 1 : 0);
  return units <= MAX_HALF_UNITS ? units : null;
}

export function halfUnitsFromLegacy(value: unknown): number | null {
  if (typeof value !== "number" || !Number.isFinite(value) || value < 0 || !Number.isInteger(value * 2)) return null;
  const units = value * 2;
  return units <= MAX_HALF_UNITS ? units : null;
}

export function formatHalfUnits(units: number): string {
  const absolute = Math.abs(units);
  return `${units < 0 ? "-" : ""}${Math.floor(absolute / 2)},${absolute % 2 ? "5" : "0"}`;
}

export function remainingBalance(entitlementHalfUnits: number, approvedHalfUnits: number): number {
  return entitlementHalfUnits - approvedHalfUnits;
}