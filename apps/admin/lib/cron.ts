// Cron presets + a small humanizer for the Script Manager. Times are UTC —
// the scheduler evaluates cron in UTC; the "next run" timestamp from the API
// is rendered in local time, which is the ground truth for the user.

export interface CronPreset {
  label: string;
  expression: string;
}

export const CRON_PRESETS: CronPreset[] = [
  { label: "Every 15 minutes", expression: "*/15 * * * *" },
  { label: "Every hour", expression: "0 * * * *" },
  { label: "Nightly at 02:00 UTC", expression: "0 2 * * *" },
  { label: "Weekdays at 08:00 UTC", expression: "0 8 * * 1-5" },
  { label: "Sundays at 03:00 UTC", expression: "0 3 * * 0" },
  { label: "Monthly on the 1st at 01:00 UTC", expression: "0 1 1 * *" },
];

const DOW = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

function two(n: number): string {
  return String(n).padStart(2, "0");
}

function dayPhrase(dow: string): string | null {
  if (dow === "*") return null;
  if (dow === "1-5") return "on weekdays";
  if (/^\d$/.test(dow)) {
    const n = Number(dow);
    return n >= 0 && n <= 6 ? `on ${DOW[n]}s` : null;
  }
  return `on days ${dow}`;
}

/**
 * Best-effort plain-English description of a 5-field cron (a leading seconds
 * field is tolerated and dropped). Returns null when the shape is too exotic —
 * the raw expression is shown instead.
 */
export function describeCron(expression: string | null | undefined): string | null {
  if (!expression) return null;
  let fields = expression.trim().split(/\s+/);
  if (fields.length === 6) fields = fields.slice(1); // seconds variant
  if (fields.length !== 5) return null;
  const [min, hour, dom, , dow] = fields as [string, string, string, string, string];

  // Every N minutes
  const everyN = /^\*\/(\d+)$/.exec(min);
  if (everyN && hour === "*" && dom === "*" && dow === "*") {
    return `every ${everyN[1]} minutes`;
  }
  // Hourly
  if (/^\d+$/.test(min) && hour === "*" && dom === "*" && dow === "*") {
    return Number(min) === 0 ? "every hour" : `hourly at :${two(Number(min))}`;
  }
  // Fixed time
  if (/^\d+$/.test(min) && /^\d+$/.test(hour)) {
    const at = `${two(Number(hour))}:${two(Number(min))} UTC`;
    const day = dayPhrase(dow);
    if (dom === "*" && day === null) return `every day at ${at}`;
    if (dom === "*" && day !== null) return `${day} at ${at}`;
    if (/^\d+$/.test(dom) && dow === "*") return `monthly on day ${dom} at ${at}`;
  }
  return null;
}
