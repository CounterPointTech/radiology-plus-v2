"use client";

import { Moon, SunMedium } from "lucide-react";
import { useCallback, useEffect, useState } from "react";

const STORAGE_KEY = "radplus.admin.theme";

function currentTheme(): "dark" | "light" {
  if (typeof document === "undefined") return "dark";
  return document.documentElement.dataset.theme === "light" ? "light" : "dark";
}

/**
 * Dark/light switch. Dark is the console default; the choice persists in
 * localStorage and is applied pre-hydration by the inline script in the root
 * layout (no flash). A short-lived `theme-switching` class cross-fades colors.
 */
export function ThemeToggle({ collapsed = false }: { collapsed?: boolean }) {
  const [theme, setTheme] = useState<"dark" | "light">("dark");

  useEffect(() => {
    setTheme(currentTheme());
  }, []);

  const toggle = useCallback(() => {
    const next = currentTheme() === "light" ? "dark" : "light";
    const root = document.documentElement;
    root.classList.add("theme-switching");
    if (next === "light") {
      root.dataset.theme = "light";
    } else {
      delete root.dataset.theme;
    }
    window.localStorage.setItem(STORAGE_KEY, next);
    setTheme(next);
    window.setTimeout(() => root.classList.remove("theme-switching"), 350);
  }, []);

  const label = theme === "light" ? "Switch to dark mode" : "Switch to light mode";
  return (
    <button
      type="button"
      onClick={toggle}
      aria-label={label}
      title={label}
      className="group inline-flex items-center gap-2 rounded-md px-2.5 py-2 text-sm text-[color:var(--color-muted-fg)] hover:text-[color:var(--color-base-fg)] hover:bg-[color:var(--color-surface-2)] transition-colors"
    >
      <span className="relative size-4 shrink-0">
        <SunMedium
          className={`absolute inset-0 size-4 transition-all duration-300 ${
            theme === "light" ? "rotate-0 scale-100 opacity-100" : "rotate-90 scale-0 opacity-0"
          }`}
        />
        <Moon
          className={`absolute inset-0 size-4 transition-all duration-300 ${
            theme === "light" ? "-rotate-90 scale-0 opacity-0" : "rotate-0 scale-100 opacity-100"
          }`}
        />
      </span>
      {collapsed ? null : <span>{theme === "light" ? "Light" : "Dark"}</span>}
    </button>
  );
}
