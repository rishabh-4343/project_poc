// Time helpers; all display times are in the viewer's locale and timezone.
const time = new Intl.DateTimeFormat(undefined, { hour: "2-digit", minute: "2-digit", second: "2-digit", hour12: false });
const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: "medium", timeStyle: "short" });

export function formatClock(iso: string) {
  return time.format(new Date(iso));
}

const day = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short" });

export function formatDay(iso: string) {
  return day.format(new Date(iso));
}

export function formatDateTime(iso: string | null | undefined) {
  return iso ? dateTime.format(new Date(iso)) : "—";
}

export function formatRelative(iso: string | null | undefined) {
  if (!iso) {
    return "Never";
  }
  const seconds = Math.round((Date.now() - new Date(iso).getTime()) / 1000);
  if (seconds < 10) return "Just now";
  if (seconds < 60) return `${seconds}s ago`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`;
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`;
  return dateTime.format(new Date(iso));
}
