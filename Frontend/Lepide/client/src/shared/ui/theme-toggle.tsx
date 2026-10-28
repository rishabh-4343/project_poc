"use client";

import { useSyncExternalStore } from "react";
import { Monitor, Moon, Sun } from "lucide-react";
import { cn } from "../cn";
import { getThemeChoice, setThemeChoice, subscribeTheme, type ThemeChoice } from "../theme";

const OPTIONS: { value: ThemeChoice; label: string; icon: typeof Sun }[] = [
  { value: "light", label: "Light theme", icon: Sun },
  { value: "dark", label: "Dark theme", icon: Moon },
  { value: "system", label: "Use system theme", icon: Monitor },
];

// Light / dark / system. The server renders "system"; the stored choice takes over right after hydration.
export function ThemeToggle() {
  const choice = useSyncExternalStore(subscribeTheme, getThemeChoice, () => "system" as ThemeChoice);

  return (
    <div role="radiogroup" aria-label="Theme" className="flex items-center gap-0.5 rounded-md border border-border bg-surface-2 p-0.5">
      {OPTIONS.map(({ value, label, icon: Icon }) => (
        <button
          key={value}
          type="button"
          role="radio"
          aria-checked={choice === value}
          aria-label={label}
          title={label}
          onClick={() => setThemeChoice(value)}
          className={cn(
            "flex size-7 items-center justify-center rounded transition-colors duration-[120ms]",
            choice === value ? "bg-surface text-fg shadow-card" : "text-fg-muted hover:text-fg",
          )}
        >
          <Icon className="size-4" aria-hidden />
        </button>
      ))}
    </div>
  );
}
