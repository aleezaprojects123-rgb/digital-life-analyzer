using Dla.Agent.Tray;

namespace Dla.Agent.Tests;

public class TrayMenuTests
{
    [Fact]
    public void Menu_has_only_pause_resume_and_open_dashboard_until_the_third_action_is_named()
    {
        var entries = TrayMenuModel.Entries(paused: false);
        Assert.Equal([TrayAction.PauseResume, TrayAction.OpenDashboard], entries.Select(e => e.Action));
        Assert.Equal(["Pause", "Open Dashboard"], entries.Select(e => e.Label));
    }

    [Fact]
    public void The_first_entry_says_Resume_while_paused_and_Pause_otherwise()
    {
        Assert.Equal("Resume", TrayMenuModel.Entries(paused: true)[0].Label);
        Assert.Equal("Pause", TrayMenuModel.Entries(paused: false)[0].Label);
    }

    [Fact]
    public void Menu_never_offers_consent_terms_settings_or_quit()
    {
        foreach (var paused in new[] { true, false })
            foreach (var (_, label) in TrayMenuModel.Entries(paused))
            {
                var l = label.ToLowerInvariant();
                foreach (var banned in new[] { "consent", "privacy", "terms", "settings", "quit", "exit", "withdraw" })
                    Assert.DoesNotContain(banned, l);
            }
        Assert.DoesNotContain(Enum.GetNames<TrayAction>(), n => n.Contains("Consent", StringComparison.OrdinalIgnoreCase));
    }
}

public class DashboardLinkTests
{
    [Theory]
    [InlineData("https://dla.example.com/dashboard")]
    [InlineData("https://example.com")]
    public void Plain_https_addresses_are_accepted(string url) => Assert.True(DashboardLink.TryGetUri(url, out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("http://dla.example.com/dashboard")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://example.com")]
    [InlineData("cmd.exe /c calc")]
    [InlineData("C:\\Windows\\System32\\calc.exe")]
    [InlineData("https://user:password@example.com/")]
    [InlineData("dla://activate?code=abc")]
    public void Anything_else_is_refused_and_never_launched(string? url)
    {
        var launched = new List<string>();
        Assert.False(DashboardLink.TryGetUri(url, out _));
        Assert.False(DashboardLink.TryOpen(url, launched.Add));
        Assert.Empty(launched);
    }

    [Fact]
    public void A_good_address_is_handed_to_the_launcher_exactly_once()
    {
        var launched = new List<string>();
        Assert.True(DashboardLink.TryOpen("https://dla.example.com/dashboard", launched.Add));
        Assert.Equal(["https://dla.example.com/dashboard"], launched);
    }

    [Fact]
    public void The_official_address_is_not_set_yet_so_the_menu_cannot_open_a_made_up_site()
    {
        Assert.Equal("", DashboardLink.OfficialUrl);
        Assert.False(DashboardLink.IsConfigured);
    }
}
