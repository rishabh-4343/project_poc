// Theme choice: "light", "dark", or "system" (follow the OS). Stored in localStorage under "theme"; no entry means system.
// The root layout's inline script applies the same rule before first paint, so there is no flash.
export type ThemeChoice = "light" | "dark" | "system";

const KEY = "theme";
const listeners = new Set<() => void>();
const systemDark = () => window.matchMedia("(prefers-color-scheme: dark)").matches;

export function getThemeChoice(): ThemeChoice {
  try {
    const stored = localStorage.getItem(KEY);
    return stored === "light" || stored === "dark" ? stored : "system";
  } catch {
    return "system";
  }
}

function apply(choice: ThemeChoice) {
  document.documentElement.classList.toggle("dark", choice === "dark" || (choice === "system" && systemDark()));
}

export function setThemeChoice(choice: ThemeChoice) {
  try {
    if (choice === "system") localStorage.removeItem(KEY);
    else localStorage.setItem(KEY, choice);
  } catch {
    // Storage can be blocked; the choice still applies to this page view.
  }
  apply(choice);
  listeners.forEach((l) => l());
}

// For useSyncExternalStore. While the choice is "system", an OS theme change is applied live.
export function subscribeTheme(listener: () => void) {
  listeners.add(listener);
  const media = window.matchMedia("(prefers-color-scheme: dark)");
  const onSystemChange = () => {
    if (getThemeChoice() === "system") apply("system");
  };
  media.addEventListener("change", onSystemChange);
  return () => {
    listeners.delete(listener);
    media.removeEventListener("change", onSystemChange);
  };
}
