namespace Dla.Agent.Link;

/// <summary>
/// The agent's link to the browser extension. Today the only implementation is a local, read-only status
/// endpoint (<see cref="LocalStatusLink"/>). Keeping it behind this interface lets us swap in Native Messaging later
/// without touching the rest of the agent.
///
/// A link is created and started only by AgentHost, and only after the user has consented.
/// </summary>
public interface IAgentLink : IDisposable
{
    bool IsListening { get; }

    /// <returns>True if the link is up. False if it could not start (for example the port is taken). Never throws for that.</returns>
    bool Start();

    void Stop();
}
