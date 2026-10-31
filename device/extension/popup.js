// Shows "N events this session" only while the agent says recording is allowed.
// In every other case (no agent, no consent, paused, any error) it says nothing is being recorded.
const message = document.getElementById("message");

// Tell the background worker the popup is open. Opening a popup takes the focus from the browser window; without this
// the worker would read that as "browser left" and end the current page's period each time the popup is opened.
// The connection closes by itself when the popup closes. Nothing is sent over it.
chrome.runtime.connect({ name: "dla-popup" });

chrome.runtime.sendMessage({ type: "dla-summary" }).then(
  (reply) => {
    if (reply && reply.recording === true && Number.isInteger(reply.eventCount)) {
      message.textContent = reply.eventCount === 1 ? "1 event this session." : `${reply.eventCount} events this session.`;
    }
  },
  () => { /* leave the default text */ },
);
