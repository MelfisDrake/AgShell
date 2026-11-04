import { en } from "./strings";

// The interface's texts in the language the settings pick. The Russian text is the key; English comes from strings.ts,
// and scripts/check-strings.mjs fails the build when a Russian text in the code is not wrapped in t() or has no English.

export type Language = "ru" | "en";

let language: Language = "ru";

export function setLanguage(next: Language): void {
  language = next;
  document.documentElement.lang = next;
}

export function currentLanguage(): Language {
  return language;
}

/** Marks a text kept in a table built at load (actions, tabs) for translation; it is translated where shown, with t(). */
export function tr(russian: string): string {
  return russian;
}

/** Picks the Russian form for a count (1 файл, 2 файла, 5 файлов) and translates it; English keys each form. */
export function plural(count: number, one: string, few: string, many: string, values?: Record<string, string | number>): string {
  const tens = count % 100;
  const units = count % 10;
  const form = tens >= 11 && tens <= 14 ? many : units === 1 ? one : units >= 2 && units <= 4 ? few : many;
  return t(form, { n: count, ...values });
}

/** The text in the interface language; {name} holes take the matching values. */
export function t(russian: string, values?: Record<string, string | number>): string {
  const text = language === "en" ? (en[russian] ?? russian) : russian;
  return values ? text.replace(/\{(\w+)\}/g, (hole, name: string) => (name in values ? String(values[name]) : hole)) : text;
}
