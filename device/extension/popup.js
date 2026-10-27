// Shows "N events this session" only while the agent says recording is allowed.
// In every other case (no agent, no consent, paused, any error) it says nothing is being recorded.
const message = document.getElementById("message");

chrome.runtime.sendMessage({ type: "dla-summary" }).then(
  (reply) => {
    if (reply && reply.recording === true && Number.isInteger(reply.eventCount)) {
      message.textContent = reply.eventCount === 1 ? "1 event this session." : `${reply.eventCount} events this session.`;
    }
  },
  () => { /* leave the default text */ },
);
