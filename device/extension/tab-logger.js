// The heart of F1 tab logging. Pure logic, no browser calls, so it can be tested in Node.
//
// background.js tells it which tab is in front ("the snapshot") whenever something changes. It keeps ONE open period
// for that tab, and when the tab, the page or the focus changes it closes the period as one Contract 1 event.
//
// It records nothing unless getStatus() says recording is allowed, at the moment of each update.
// Events are kept in memory only (no storage permission).

import { sanitizeUrlDetailed, getDomain, isHiddenSite as defaultIsHiddenSite } from "./url-sanitizer.js";
import { hasSensitiveHost } from "./sensitive-domains.js";

export const MAX_TITLE_CHARS = 120;

/** Whole-second UTC time with a trailing Z, as the contract wants: 2026-10-27T05:15:00Z */
export function toUtcSeconds(ms) {
  return new Date(Math.floor(ms / 1000) * 1000).toISOString().replace(".000Z", "Z");
}

/** Title for the event: null if the sanitizer masked anything or the site is sensitive; otherwise cleaned and cut to 120 characters. */
export function makeTitle(rawTitle, { masked, host }, sensitiveHost = hasSensitiveHost) {
  if (masked || sensitiveHost(host)) return null;
  if (typeof rawTitle !== "string") return null;
  const cleaned = rawTitle.replace(/[\u0000-\u001f\u007f-\u009f]/g, " ").replace(/\s+/g, " ").trim();
  if (cleaned.length === 0) return null;
  return [...cleaned].slice(0, MAX_TITLE_CHARS).join("");
}

export class TabLogger {
  /**
   * @param {object} deps
   * @param {() => Promise<{recording: boolean, noConsent: boolean}>} deps.getStatus
   * @param {() => number} [deps.now]
   * @param {() => string} [deps.newId]
   * @param {(domain: string) => boolean} [deps.isHiddenSite]
   * @param {(host: string) => boolean} [deps.sensitiveHost]
   */
  constructor({ getStatus, now = Date.now, newId = () => crypto.randomUUID(), isHiddenSite = defaultIsHiddenSite, sensitiveHost = hasSensitiveHost }) {
    this.getStatus = getStatus;
    this.now = now;
    this.newId = newId;
    this.isHiddenSite = isHiddenSite;
    this.sensitiveHost = sensitiveHost;
    /** Closed events, in memory only. */
    this.events = [];
    this.open = null;
  }

  /**
   * Something changed. `snapshot` is the tab now in front in the focused browser window,
   * or null if the browser has no focus (or there is no tab).  { tabId, url, title, incognito }
   * Call these one after another, not at the same time.
   */
  async update(snapshot, atMs = this.now()) {
    const status = await this.getStatus();

    if (!status.recording) {
      // Not consented, paused, or the agent is not there: nothing is recorded and the unfinished period is dropped.
      this.open = null;
      if (status.noConsent) this.events.length = 0; // no consent on record at all: keep nothing
      return;
    }

    const target = this.#describe(snapshot);

    if (this.open && target && this.open.tabId === target.tabId && this.open.url === target.url) {
      this.open.rawTitle = target.rawTitle; // same page, maybe a new title: still the same period
      return;
    }

    if (this.open) this.#close(atMs);
    if (target) this.open = { ...target, startMs: atMs };
  }

  /** What the popup shows. Counts nothing unless recording is allowed right now. */
  async summary() {
    const status = await this.getStatus();
    return status.recording ? { recording: true, eventCount: this.events.length } : { recording: false, eventCount: 0 };
  }

  #describe(snapshot) {
    if (!snapshot || snapshot.incognito || typeof snapshot.url !== "string") return null;

    const cleaned = sanitizeUrlDetailed(snapshot.url); // chrome://, edge://, about:, file:, new tab ... give null
    if (!cleaned) return null;

    const host = new URL(cleaned.url).hostname;
    const domain = getDomain(host);
    if (!domain) return null;
    if (this.isHiddenSite(domain)) return null; // hidden site: no event at all

    return { tabId: snapshot.tabId, url: cleaned.url, host, domain, masked: cleaned.masked, rawTitle: snapshot.title };
  }

  #close(endMs) {
    const open = this.open;
    this.open = null;

    const start = toUtcSeconds(open.startMs);
    const end = toUtcSeconds(Math.max(endMs, open.startMs));
    const duration = Math.floor(Math.max(endMs, open.startMs) / 1000) - Math.floor(open.startMs / 1000);

    this.events.push({
      schema_version: "1",
      id: this.newId(),
      start,
      end,
      duration_seconds: duration,
      source: "extension",
      app: null,
      domain: open.domain,
      url: open.url,
      title: makeTitle(open.rawTitle, open, this.sensitiveHost),
      idle: false,
      presence: null,
      category: null,
      confidence: null,
      uncertain: false,
      label_source: null,
    });
  }
}
