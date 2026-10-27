using System.Text;
using System.Text.Json;
using Dla.Agent.Consent;

namespace Dla.Agent.Link;

/// <summary>
/// The whole content of the status answer. Three flags and a number, nothing about what the user did.
///
/// "Consented" is true only while the user has accepted AND tracking is not paused, so an extension that reads
/// only this one value stays silent whenever recording is not allowed. "TrackingEnabled" never says ON without
/// consent either. "ConsentVersion" is the terms version the user accepted, or 0 when there is no valid consent.
/// </summary>
public readonly record struct AgentStatus(bool Consented, bool TrackingEnabled, int ConsentVersion)
{
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteBoolean("consented", Consented);
            json.WriteBoolean("trackingEnabled", TrackingEnabled);
            json.WriteNumber("consentVersion", ConsentVersion);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

public interface IStatusSource
{
    AgentStatus GetStatus();
}

/// <summary>
/// Builds the status from the real consent gate and the agent's pause state, fresh on every call.
/// The consent file is re-read each time, so a withdrawn consent shows up here at once.
/// </summary>
public sealed class AgentStatusProvider : IStatusSource
{
    private readonly IConsentGate _gate;
    private readonly AgentState _state;

    public AgentStatusProvider(IConsentGate gate, AgentState state)
    {
        _gate = gate;
        _state = state;
    }

    public AgentStatus GetStatus()
    {
        var hasConsent = _gate.HasConsent;
        var recording = hasConsent && _state.TrackingEnabled;
        return new AgentStatus(Consented: recording, TrackingEnabled: recording, ConsentVersion: hasConsent ? ConsentText.Version : 0);
    }
}
