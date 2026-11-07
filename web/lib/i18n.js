// Translations. Spanish text is the key; en.json / ca.json map it (shared with the C# core's L.Tr).
// Placeholders are {0}, {1}…

const tables = { en: {}, ca: {} };
let language = "en";

export async function loadTranslations() {
  await Promise.all(["en", "ca"].map(async (code) => {
    try {
      const r = await fetch(`/app/i18n/${code}.json`, { cache: "no-cache" });
      if (r.ok) tables[code] = await r.json();
    } catch { /* keep the Spanish keys */ }
  }));
}

/** "system" follows the browser (WebView2 uses the Windows language). */
export function setLanguage(choice) {
  if (choice && choice !== "system") {
    language = choice;
  } else {
    language = "en";
    for (const tag of navigator.languages?.length ? navigator.languages : [navigator.language]) {
      const code = String(tag || "").slice(0, 2).toLowerCase();
      if (code === "es" || code === "ca" || code === "en") { language = code; break; }
    }
  }
  document.documentElement.lang = language;
}

export function currentLanguage() { return language; }

/** Translated text with {n} placeholders filled in. */
export function t(key, ...args) {
  const text = language === "es" ? key : (tables[language]?.[key] ?? key);
  if (!args.length) return text;
  return text.replace(/\{(\d+)\}/g, (m, i) => (args[Number(i)] === undefined ? m : String(args[Number(i)])));
}

/** Number formatted for the UI language (1 000 000 / 1.000.000 / 1,000,000). */
export function formatNumber(n, digits = 0) {
  const locale = language === "en" ? "en-US" : language === "ca" ? "ca-ES" : "es-ES";
  return Number(n).toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits });
}
