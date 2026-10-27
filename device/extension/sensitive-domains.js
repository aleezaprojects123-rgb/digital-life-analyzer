// Sites where the page title is never kept (webmail and messaging: titles there show subjects and sender names).
// The event itself is still logged (domain and sanitized URL); only `title` becomes null.
//
// FIRST VERSION: this list is a starting point for the team to review, not a complete or final list.
// An entry matches the host itself and every subdomain of it (for example "mail.google.com" matches that host only,
// not the rest of google.com).

export const SENSITIVE_TITLE_HOSTS = [
  // webmail
  "mail.google.com",
  "outlook.live.com",
  "outlook.office.com",
  "outlook.office365.com",
  "mail.yahoo.com",
  "mail.proton.me",
  "proton.me",
  "protonmail.com",
  "mail.aol.com",
  "mail.zoho.com",
  "mail.icloud.com",
  "fastmail.com",
  // messaging
  "web.whatsapp.com",
  "web.telegram.org",
  "messenger.com",
  "discord.com",
  "app.slack.com",
  "teams.microsoft.com",
  "teams.live.com",
  "chat.google.com",
  "web.skype.com",
  "signal.org",
];

/** True if the title of a page on this host must not be kept. `host` is a lowercase hostname. */
export function hasSensitiveHost(host, list = SENSITIVE_TITLE_HOSTS) {
  if (typeof host !== "string") return false;
  return list.some((entry) => host === entry || host.endsWith(`.${entry}`));
}
