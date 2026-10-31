using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Dla.Agent.Link;

/// <summary>
/// Puts the id of the current recording session on an accepted browser event. The agent owns the session (it runs for the
/// whole recording period; the browser's service worker restarts every half minute or so), so a value sent by the
/// browser is never trusted: it is replaced.
/// </summary>
internal static class SessionStamp
{
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Apply(string eventJson, string sessionId)
    {
        try
        {
            if (JsonNode.Parse(eventJson) is not JsonObject root) return eventJson;
            root["session_id"] = sessionId;
            return root.ToJsonString(Compact);
        }
        catch (JsonException)
        {
            return eventJson;
        }
    }
}
