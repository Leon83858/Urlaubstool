import { halfUnitsFromLegacy } from "./half-units.ts";

export type Settings = {
  firstName: string;
  lastName: string;
  address: string;
  department: string;
  personnelNumber: string;
  className: string;
  annualEntitlement: number;
  federalState: string;
  workdays: boolean[];
};

export type SettingsV2 = Omit<Settings, "annualEntitlement"> & {
  annualEntitlementHalfUnits: number;
  includeOptionalHolidays: boolean;
  /** Berufsschulmodus (REQ-CALC-006); fehlt bei älteren Ständen und gilt dann als aus. */
  studentMode?: boolean;
  /** Berufsschule je Wochentag Mo–So: 0 keine, 1 halbtags, 2 ganztags. */
  vocationalSchoolWeek?: number[];
};

export const NO_VOCATIONAL_SCHOOL: readonly number[] = [0, 0, 0, 0, 0, 0, 0];

/** Liefert eine gültige Berufsschulwoche (7 Werte 0–2) oder `null`. */
export function vocationalWeek(value: unknown): number[] | null {
  if (value === undefined) return [...NO_VOCATIONAL_SCHOOL];
  if (!Array.isArray(value) || value.length !== 7 || !value.every((entry) => entry === 0 || entry === 1 || entry === 2)) return null;
  return [...value];
}

export function writeLocalValue(getStorage: () => Pick<Storage, "setItem">, key: string, value: string): boolean {
  try {
    getStorage().setItem(key, value);
    return true;
  } catch {
    return false;
  }
}

export function parseSettings(raw: string | null, defaults: Settings): { settings: Settings; error: boolean } {
  if (raw === null) return { settings: defaults, error: false };
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("Invalid settings");
    const settings = { ...defaults, ...parsed };
    if (typeof settings.firstName !== "string" || typeof settings.lastName !== "string" ||
        typeof settings.department !== "string" || typeof settings.className !== "string" ||
        typeof settings.federalState !== "string" || !Number.isFinite(settings.annualEntitlement) ||
        settings.annualEntitlement <= 0 || !Number.isInteger(settings.annualEntitlement * 2) ||
        settings.annualEntitlement > 2147483647.5 || !Array.isArray(settings.workdays) ||
        settings.workdays.length !== 7 || !settings.workdays.every((value) => typeof value === "boolean")) {
      throw new Error("Invalid settings");
    }
    return { settings, error: false };
  } catch {
    return { settings: defaults, error: true };
  }
}

export function parseSettingsV2(raw: string | null, defaults: SettingsV2): { settings: SettingsV2; error: boolean } {
  if (raw === null) return { settings: defaults, error: false };
  try {
    const parsed: unknown = JSON.parse(raw);
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) throw new Error("Invalid settings");
    const values = parsed as Record<string, unknown>;
    const legacy = "annualEntitlement" in values ? halfUnitsFromLegacy(values.annualEntitlement) : undefined;
    if (legacy === null) throw new Error("Invalid legacy entitlement");
    const units = "annualEntitlementHalfUnits" in values ? values.annualEntitlementHalfUnits : legacy ?? defaults.annualEntitlementHalfUnits;
    if (typeof units !== "number" || !Number.isInteger(units) || units <= 0 || units > 4294967295 || (legacy !== undefined && legacy !== units)) throw new Error("Invalid entitlement");
    const { annualEntitlement: _legacy, ...remaining } = values;
    const settings = { ...defaults, ...remaining, includeOptionalHolidays: "includeOptionalHolidays" in remaining ? remaining.includeOptionalHolidays : defaults.includeOptionalHolidays ?? false, annualEntitlementHalfUnits: units } as SettingsV2;
    if (typeof settings.firstName !== "string" || typeof settings.lastName !== "string" ||
        typeof settings.department !== "string" || typeof settings.className !== "string" ||
        (settings.address !== undefined && typeof settings.address !== "string") ||
        (settings.personnelNumber !== undefined && typeof settings.personnelNumber !== "string") ||
        typeof settings.federalState !== "string" || typeof settings.includeOptionalHolidays !== "boolean" || !Array.isArray(settings.workdays) ||
        settings.workdays.length !== 7 || !settings.workdays.every((value) => typeof value === "boolean") ||
        (settings.studentMode !== undefined && typeof settings.studentMode !== "boolean") ||
        vocationalWeek(settings.vocationalSchoolWeek) === null) throw new Error("Invalid settings");
    return { settings, error: false };
  } catch {
    return { settings: defaults, error: true };
  }
}

export function readVersionedSettings(storage: Pick<Storage, "getItem">, key: string, legacyKey: string, defaults: SettingsV2): { settings: SettingsV2; error: boolean } {
  try {
    const stored = storage.getItem(key);
    return parseSettingsV2(stored === null ? storage.getItem(legacyKey) : stored, defaults);
  } catch {
    return { settings: defaults, error: true };
  }
}