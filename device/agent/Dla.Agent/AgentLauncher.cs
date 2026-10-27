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

    /// <param name="runUntilQuit">Runs the message loop until the user chooses Quit. Called only after the tray exists.</param>
    public AgentLauncher(OnboardingFlow onboarding, AgentHost host, Action<AgentHost> runUntilQuit)
    {
        _onboarding = onboarding;
        _host = host;
        _runUntilQuit = runUntilQuit;
    }

    public AgentLaunchResult Launch()
    {
        var outcome = _onboarding.Run();

        if (outcome is OnboardingOutcome.Declined or OnboardingOutcome.SaveFailed)
            return new AgentLaunchResult(outcome, TrayStarted: false);

        // Consent is on record or was just accepted. AgentHost still checks the gate itself before creating the tray.
        if (!_host.TryStart())
            return new AgentLaunchResult(outcome, TrayStarted: false);

        _runUntilQuit(_host);
        return new AgentLaunchResult(outcome, TrayStarted: true);
    }
}
