namespace Dla.Agent;

/// <summary>What the start-up did. TrayStarted is true only if the tray was created and the program ran until Quit.</summary>
public readonly record struct AgentLaunchResult(OnboardingOutcome Outcome, bool TrayStarted);

/// <summary>
/// The whole start-up in one place:
///   1. onboarding (valid consent already saved, or ask the user and save only on Accept),
///   2. only if consent exists: AgentHost starts the tray, then the program runs until Quit.
/// Decline, closing the window, or a failed save: no tray is created and the method returns.
/// </summary>
public sealed class AgentLauncher
{
    private readonly OnboardingFlow _onboarding;
    private readonly AgentHost _host;
    private readonly Action<AgentHost> _runUntilQuit;
    private readonly IAgentLog? _log;
    private readonly Func<bool> _guardHeld;
    private readonly Func<int> _processId;

    /// <param name="runUntilQuit">Runs the message loop until the user chooses Quit. Called only after the tray exists.</param>
    /// <param name="log">If given, one status line is written once the agent has started (only possible after consent).</param>
    /// <param name="guardHeld">Whether this process holds the single-instance guard (for that line).</param>
    /// <param name="processId">This process's id (for that line).</param>
    public AgentLauncher(OnboardingFlow onboarding, AgentHost host, Action<AgentHost> runUntilQuit,
        IAgentLog? log = null, Func<bool>? guardHeld = null, Func<int>? processId = null)
    {
        _onboarding = onboarding;
        _host = host;
        _runUntilQuit = runUntilQuit;
        _log = log;
        _guardHeld = guardHeld ?? (() => SingleInstance.ProcessGuardHeld);
        _processId = processId ?? (() => Environment.ProcessId);
    }

    public AgentLaunchResult Launch()
    {
        var outcome = _onboarding.Run();

        if (outcome is OnboardingOutcome.Declined or OnboardingOutcome.SaveFailed)
            return new AgentLaunchResult(outcome, TrayStarted: false);

        // Consent is on record or was just accepted. AgentHost still checks the gate itself before creating the tray.
        if (!_host.TryStart())
            return new AgentLaunchResult(outcome, TrayStarted: false);

        // One line so a tester can check later which process ran and that it held the guard. Only reached after consent.
        _log?.Write($"Agent started. Process id {_processId()}. Single-instance guard acquired: {(_guardHeld() ? "yes" : "no")}.");

        _runUntilQuit(_host);
        return new AgentLaunchResult(outcome, TrayStarted: true);
    }
}
