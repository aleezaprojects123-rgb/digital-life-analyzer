// How times and dates are written on screen. Pure functions, no data of their own.
// Dates are written from fixed name lists (not Intl), so the text is the same on every PC.

const WEEKDAYS = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
const MONTHS = [
  "January", "February", "March", "April", "May", "June",
  "July", "August", "September", "October", "November", "December",
];

/** Hours and minutes, written <hours>h <mm>m. Minutes are rounded to the nearest one and always have two digits. Bad input counts as 0. */
export function formatDuration(seconds) {
  const safe = Number.isFinite(seconds) && seconds > 0 ? seconds : 0;
  const totalMinutes = Math.round(safe / 60);
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return `${hours}h ${String(minutes).padStart(2, "0")}m`;
}

/**
 * The short form for a label on a bar: just minutes under an hour ("<m>m"), otherwise the same text as formatDuration.
 * A positive time that rounds to 0 minutes is "<1m" (so a bar that exists never says 0), and 0 is "0m".
 */
export function formatDurationShort(seconds) {
  const safe = Number.isFinite(seconds) && seconds > 0 ? seconds : 0;
  const text = formatDuration(safe);
  const minutesOnly = /^0h 0?(\d+m)$/.exec(text);
  if (!minutesOnly) return text;
  if (safe > 0 && minutesOnly[1] === "0m") return "<1m";
  return minutesOnly[1];
}

/** Reads YYYY-MM-DD as that calendar day (no time zone shifts). Returns null for anything else. */
export function parseDay(text) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(typeof text === "string" ? text : "");
  if (!match) return null;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  const real = date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
  return real ? { year, month, day, weekday: date.getUTCDay() } : null;
}

/** This PC's calendar day as YYYY-MM-DD (local time, not UTC). The agent works in the PC's time zone too. */
export function localToday(now = new Date()) {
  const pad = (n) => String(n).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

/** Whole days from one YYYY-MM-DD to another (negative when the second is earlier). NaN if either is not a real day. */
export function daysBetween(from, to) {
  const a = parseDay(from);
  const b = parseDay(to);
  if (!a || !b) return NaN;
  return Math.round((Date.UTC(b.year, b.month - 1, b.day) - Date.UTC(a.year, a.month - 1, a.day)) / 86400000);
}

/** <Weekday>, <day> <Month> */
export function formatLongDate(text) {
  const d = parseDay(text);
  return d ? `${WEEKDAYS[d.weekday]}, ${d.day} ${MONTHS[d.month - 1]}` : "";
}

const short = (name) => name.slice(0, 3);

/** A weekly range: <Wkd>, <dd> <Mon> – <Wkd>, <dd> <Mon>. */
export function formatWeekRange(firstDay, lastDay) {
  const a = parseDay(firstDay);
  const b = parseDay(lastDay);
  if (!a || !b) return "";
  const one = (d) => `${short(WEEKDAYS[d.weekday])}, ${String(d.day).padStart(2, "0")} ${short(MONTHS[d.month - 1])}`;
  return `${one(a)} – ${one(b)}`;
}

/** A monthly range: <Mon> <d> – <Mon> <d>. */
export function formatMonthRange(firstDay, lastDay) {
  const a = parseDay(firstDay);
  const b = parseDay(lastDay);
  if (!a || !b) return "";
  const one = (d) => `${short(MONTHS[d.month - 1])} ${d.day}`;
  return `${one(a)} – ${one(b)}`;
}

/** "Mon" from the agent's "Monday", or from the date when the word is missing. */
export function weekdayShort(dayOfWeek, date) {
  if (typeof dayOfWeek === "string" && dayOfWeek.length >= 3) return dayOfWeek.slice(0, 3);
  const d = parseDay(date);
  return d ? short(WEEKDAYS[d.weekday]) : "";
}

/** The count with the singular word for 1 and the plural word otherwise. */
export function countWord(count, singular, plural = `${singular}s`) {
  return `${count} ${count === 1 ? singular : plural}`;
}
