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

/** Reads YYYY-MM-DD as that calendar day (no time zone shifts). Returns null for anything else. */
export function parseDay(text) {
  const match = /^(\d{4})-(\d{2})-(\d{2})$/.exec(typeof text === "string" ? text : "");
  if (!match) return null;
  const [year, month, day] = [Number(match[1]), Number(match[2]), Number(match[3])];
  const date = new Date(Date.UTC(year, month - 1, day));
  const real = date.getUTCFullYear() === year && date.getUTCMonth() === month - 1 && date.getUTCDate() === day;
  return real ? { year, month, day, weekday: date.getUTCDay() } : null;
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
