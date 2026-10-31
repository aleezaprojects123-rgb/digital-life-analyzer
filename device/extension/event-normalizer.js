// The one place that defines the shape of a browser event (the "raw" stage). Every platform produces this same shape.
//
// Raw browser event  ->  (agent) enriched with system media info  ->  (later) classified  ->  summary.
// This file only builds the raw stage. category/confidence/label_source stay null: classification happens later.
// DLA measures behaviour, not content: platform, activity type, time, and how many content items were consumed.
// content_count: 1 when this event is where the user moved to a NEW content item (Reel, video, post) on a platform that has
// item addresses, else 0. A future screen counts items with SUM(content_count) and time with SUM(duration_seconds).
// session_id: null here; the local agent stamps the id of the current recording session when it accepts the event.
// (The agent may add its own media_* fields when Windows media controls match; they are not part of the extension event.)

export const BROWSER_EVENT_FIELDS = Object.freeze([
  "schema_version", "id", "start", "end", "duration_seconds", "source",
  "platform", "domain", "url", "title", "activity_type",
  "content_count", "session_id",
  "category", "confidence", "uncertain", "label_source",
]);

/**
 * @param {{id: string, start: string, end: string, duration_seconds: number, domain: string, url: string,
 *          title: string|null, platform: string, activity_type: string}} parts
 */
export function normalizeBrowserEvent(parts) {
  return {
    schema_version: "1",
    id: parts.id,
    start: parts.start,
    end: parts.end,
    duration_seconds: parts.duration_seconds,
    source: "extension",
    platform: parts.platform ?? "unknown",
    domain: parts.domain,
    url: parts.url,
    title: parts.title ?? null,
    activity_type: parts.activity_type ?? "unknown",
    content_count: parts.content_count ?? 0,
    session_id: null,
    category: null,
    confidence: null,
    uncertain: false,
    label_source: null,
  };
}
