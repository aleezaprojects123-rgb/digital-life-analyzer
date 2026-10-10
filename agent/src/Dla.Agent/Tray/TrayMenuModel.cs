using System.Diagnostics;

namespace Dla.Agent.Tray;

public enum TrayAction { PauseResume, OpenDashboard }

/// <summary>
/// What the right-click tray menu contains. Consent, terms and withdrawal are NOT here: consent belongs on the website.
/// There is deliberately no Quit, no Settings and no Consent and privacy entry.
///
/// THIRD ACTION: pending. Aleeza has not yet named it, so the menu has two entries until she does. Add it here
/// (and in AgentContext, which builds the real menu from this list) and update the test that pins the list.
/// </summary>
public static class TrayMenuModel
{
    public static IReadOnlyList<(TrayAction Action, string Label)> Entries(bool paused) =>
    [
        (TrayAction.PauseResume, paused ? "Resume" : "Pause"),
        (TrayAction.OpenDashboard, "Open Dashboard"),
    ];
}

/// <summary>Opens the official DLA dashboard in the user's default browser. Only a plain https address is ever opened.</summary>
public static class DashboardLink
{
    /// <summary>
    /// The official dashboard address. NOT SET: the website does not exist in this repository and its address has not
    /// been provided. While empty, "Open Dashboard" tells the user the dashboard is not available yet.
    /// </summary>
    public const string OfficialUrl = "";

    public static bool IsConfigured => TryGetUri(OfficialUrl, out _);

    /// <summary>Accepts only an absolute https URL with a host and no embedded credentials.</summary>
    public static bool TryGetUri(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme != Uri.UriSchemeHttps) return false;
        if (string.IsNullOrEmpty(parsed.Host) || !string.IsNullOrEmpty(parsed.UserInfo)) return false;
        uri = parsed;
        return true;
    }

    /// <returns>True if the browser was asked to open the address.</returns>
    public static bool TryOpen(string? url, Action<string>? launcher = null)
    {
        if (!TryGetUri(url, out var uri)) return false;
        (launcher ?? DefaultLauncher)(uri.AbsoluteUri);
        return true;
    }

    private static void DefaultLauncher(string address) =>
        Process.Start(new ProcessStartInfo(address) { UseShellExecute = true });
}
