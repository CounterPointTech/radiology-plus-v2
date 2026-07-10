"use client";

import { Moon, SunMedium } from "lucide-react";
import { useCallback, useEffect, useState } from "react";

const STORAGE_KEY = "radplus.theme";

function currentTheme(): "dark" | "light" {
  if (typeof document === "undefined") return "light";
  return document.documentElement.dataset.theme === "dark" ? "dark" : "light";
}

/**
 * Dark/light switch. Light is the clinical default; the choice persists in
 * localStorage and is applied pre-hydration by the inline script in the root
 * layout (no flash). A short-lived `theme-switching` class cross-fades colors.
 */
export function ThemeToggle() {
  const [theme, setTheme] = useState<"dark" | "light">("light");

  useEffect(() => {
    setTheme(currentTheme());
  }, []);

  const toggle = useCallback(() => {
    const next = currentTheme() === "dark" ? "light" : "dark";
    const root = document.documentElement;
    root.classList.add("theme-switching");
    if (next === "dark") {
      root.dataset.theme = "dark";
    } else {
      delete root.dataset.theme;
    }
    window.localStorage.setItem(STORAGE_KEY, next);
    setTheme(next);
    window.setTimeout(() => root.classList.remove("theme-switching"), 350);
  }, []);

  const label = theme === "dark" ? "Switch to light mode" : "Switch to dark mode";
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={label}
      title={label}
      className="inline-flex items-center justify-center rounded-md p-2 text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)] transition-colors"
    >
      <span className="relative size-4 shrink-0">
        <SunMedium
          className={`absolute inset-0 size-4 transition-all duration-300 ${
            theme === "dark" ? "-rotate-90 scale-0 opacity-0" : "rotate-0 scale-100 opacity-100"
          }`}
        />
        <Moon
          className={`absolute inset-0 size-4 transition-all duration-300 ${
            theme === "dark" ? "rotate-0 scale-100 opacity-100" : "rotate-90 scale-0 opacity-0"
          }`}
        />
      </span>
    </button>
  );
}
