// The heart of F1 tab logging. Pure logic, no browser calls, so it can be tested in Node.
//
// background.js tells it which tab is in front ("the snapshot") whenever something changes. It keeps ONE open period
// for that tab, and when the tab, the page or the focus changes it closes the period as one Contract 1 event.
//
// It records nothing unless getStatus() says recording is allowed, at the moment of each update.
// Events are kept in memory only (no storage permission).

import { sanitizeUrlDetailed, getDomain, isHiddenSite as defaultIsHiddenSite } from "./url-sanitizer.js";
import { hasSensitiveHost } from "./sensitive-domains.js";
import { contentKey, isPublicVideoPage } from "./content-key.js";
import { describeActivity } from "./platforms.js";
import { normalizeBrowserEvent } from "./event-normalizer.js";

export const MAX_TITLE_CHARS = 120;

/** A period that ended because the browser lost focus is held this long before it is sent (the popup may take it back). */
export const HOLD_MS = 3000;

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
   * @param {(event: object) => void} [deps.onEventClosed]
   */
  constructor({ getStatus, now = Date.now, newId = () => crypto.randomUUID(), isHiddenSite = defaultIsHiddenSite, sensitiveHost = hasSensitiveHost, onEventClosed, holdMs = HOLD_MS, diag }) {
    this.holdMs = holdMs;
    /** Optional (string) => void: one safe line per new period (platform, activity type and yes/no flags only). */
    this.diag = diag;
    /**
     * tabId -> the content key (Reel, video, post ...) already counted in that tab. A new period counts as a new content item
     * (content_count 1) only when its key differs from the one counted last in the same tab. Coming back to the same item after a
     * tab switch or a lost focus, or a page re-render, is the same item: content_count 0. Memory only.
     */
    this.lastCounted = new Map();
    this.holdTimer = null;
    this.getStatus = getStatus;
    this.now = now;
    this.newId = newId;
    this.isHiddenSite = isHiddenSite;
    this.sensitiveHost = sensitiveHost;
    this.onEventClosed = onEventClosed;
    /** Closed events, in memory only. */
    this.events = [];
    this.open = null;
    /** True while the extension's own popup is open. The popup takes the keyboard focus from the browser window, which looks like "browser lost focus" but is not. */
    this.popupOpen = false;
    /** The last period that ended only because focus was lost, so it can be taken back if the popup turns out to be the reason. */
    this.undo = null;
  }

  /**
   * Something changed. `snapshot` is the tab now in front in the focused browser window,
   * or null if the browser has no focus (or there is no tab).  { tabId, url, title, incognito }
   * Call these one after another, not at the same time.
   *
   * `focusLoss` is true when the snapshot is empty only because a window-focus event said the browser is no longer in
   * front. While the popup is open that is ignored (the popup holds the focus, you are still on the same page).
   */
  async update(snapshot, atMs = this.now(), { focusLoss = false } = {}) {
    const status = await this.getStatus();
    this.flush(); // anything new that happens makes an earlier focus loss final

    if (!status.recording) {
      // Not consented, paused, or the agent is not there: nothing is recorded and the unfinished period is dropped.
      this.#dropOpen();
      if (status.noConsent) {
        this.events.length = 0; // no consent on record at all: keep nothing
        this.lastCounted.clear();
      }
      return;
    }

    if (focusLoss && this.popupOpen) return;

    const target = this.#describe(snapshot);

    if (this.open && target && this.open.tabId === target.tabId && this.open.url === target.url && this.open.key === target.key) {
      this.open.rawTitle = target.rawTitle; // same page, maybe a new title: still the same period (a re-render is not a new item)
      return;
    }

    // The video changed inside the same tab (next Short, next /watch?v=...): the title the tab still shows may be the
    // old video's title until the page updates it, so an unchanged title is not trusted for the new period.
    const sameTabNewVideo = this.open && target && this.open.tabId === target.tabId && target.key !== null && this.open.key !== target.key;
    const previousTitle = sameTabNewVideo ? this.open.rawTitle : undefined;

    const previous = this.open;
    if (this.open) this.#close(atMs, focusLoss && !target);
    if (target) {
      // A new content item is counted only on evidence: the item has a key and it differs from the one last counted in this tab.
      const isNew = target.key !== null && this.lastCounted.get(target.tabId) !== target.key;
      // Arriving in a tab (first period, or coming from another tab) on a platform that asks for it: the page may still show the item
      // that was left, for a moment. Such a period is provisional: it is counted only if it lasts target.settleMs (decided at close).
      const arrival = !previous || previous.tabId !== target.tabId;
      const provisional = isNew && arrival && target.settleMs > 0;
      const counted = isNew && !provisional;
      if (counted) this.lastCounted.set(target.tabId, target.key);
      const why = !previous ? "first" : previous.tabId !== target.tabId ? "tab" : previous.url !== target.url ? "url" : "content";
      this.diag?.(`new period platform=${target.platform} type=${target.activityType} new-content-item=${provisional ? "pending" : counted ? "yes" : "no"} changed-because=${why}`);
      this.open = { ...target, startMs: atMs, contentCount: counted ? 1 : 0, provisional };
      if (sameTabNewVideo && target.rawTitle === previousTitle) this.open.rawTitle = null;
    }
  }

  /** Throws away the unfinished period without an event. If it was the one that carried a count, that count is given back. */
  #dropOpen() {
    const open = this.open;
    this.open = null;
    if (open && open.contentCount === 1 && this.lastCounted.get(open.tabId) === open.key) this.lastCounted.delete(open.tabId);
  }

  /**
   * The extension's popup has opened. If the period that was just ended by a focus loss ended because the popup took
   * the focus, take that back: the user is still on the same page. (A real tab change, a new page, or anything else
   * that happened since makes the earlier end final, so nothing is undone then.)
   */
  popupOpened() {
    this.popupOpen = true;
    const undo = this.undo;
    this.#release();
    if (undo && !this.open && this.events.at(-1) === undo.event) {
      this.events.pop();
      this.open = undo.open;
    }
  }

  popupClosed() {
    this.popupOpen = false;
  }

  /** Sends a held event now. Also runs by itself HOLD_MS after the focus loss, so leaving for another app is delivered. */
  flush() {
    const held = this.undo;
    this.#release();
    if (held) this.onEventClosed?.(held.event);
  }

  #release() {
    this.undo = null;
    if (this.holdTimer !== null) {
      clearTimeout(this.holdTimer);
      this.holdTimer = null;
    }
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

    const activity = describeActivity({ url: cleaned.url, host, domain, rawTitle: snapshot.title });
    // Which content item this is (Reel, video, post), read from the real address by the platform adapter. Memory only: it is
    // never put into an event. null = no reliable identity, so the logger neither splits nor counts on a guess.
    const key = contentKey(snapshot.url);
    // A masked Shorts/watch id is a public video id, so the video title is still allowed. Everything else masked stays untitled.
    const masked = cleaned.masked && !isPublicVideoPage(snapshot.url);
    return { tabId: snapshot.tabId, url: cleaned.url, host, domain, masked, key, rawTitle: activity.rawTitle, platform: activity.platform, activityType: activity.activity_type, settleMs: activity.settleMs ?? 0 };
  }

  #close(endMs, undoable = false) {
    const open = this.open;
    this.open = null;

    const start = toUtcSeconds(open.startMs);
    const end = toUtcSeconds(Math.max(endMs, open.startMs));
    const duration = Math.floor(Math.max(endMs, open.startMs) / 1000) - Math.floor(open.startMs / 1000);

    // A provisional period (see update) is a real new item only if it was on screen long enough; a shorter one was a transition.
    if (open.provisional && Math.max(endMs, open.startMs) - open.startMs >= open.settleMs) {
      open.contentCount = 1;
      this.lastCounted.set(open.tabId, open.key);
    }

    const event = normalizeBrowserEvent({
      id: this.newId(),
      start,
      end,
      duration_seconds: duration,
      platform: open.platform,
      domain: open.domain,
      url: open.url,
      title: makeTitle(open.rawTitle, open, this.sensitiveHost),
      activity_type: open.activityType,
      content_count: open.contentCount ?? 0,
    });
    this.events.push(event);
    if (undoable) {
      this.undo = { event, open };
      this.holdTimer = setTimeout(() => this.flush(), this.holdMs);
      this.holdTimer.unref?.(); // Node only (tests): never keep the test process waiting
    } else {
      this.onEventClosed?.(event);
    }
  }
}
