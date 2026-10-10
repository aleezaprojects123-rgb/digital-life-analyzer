using Dla.Agent.Consent;
using Dla.Agent.Lifecycle;
using Dla.Agent.Supervision;
using Microsoft.Win32;

namespace Dla.Agent.Tests;

public class NotActivatedTests
{
    private const string ScratchKey = @"Software\DLA-Tests\NotActivated";

    private static void CleanUp() => Registry.CurrentUser.DeleteSubKeyTree(@"Software\DLA-Tests", throwOnMissingSubKey: false);

    [Fact]
    public void Starting_without_website_consent_removes_auto_start_writes_clean_markers_and_invents_no_consent()
    {
        using var d = new TempDir();
        try
        {
            var autoStart = new AutoStart(ScratchKey, "DlaTest");
            autoStart.Enable(@"C:\old\Dla.Agent.exe");           // left behind by an earlier local acceptance
            var log = new LifecycleLog(d.Path);

            Dla.Agent.Tray.AgentContext.RecordNotActivated(log, "s1", autoStart);

            Assert.False(autoStart.IsEnabled);
            Assert.Equal([MarkerKind.Start, MarkerKind.CleanShutdown], log.ReadAll().Select(m => m.Kind));
            Assert.True(log.HasCleanShutdown("s1"));                          // so the supervisor does not restart it
            Assert.False(File.Exists(Path.Combine(d.Path, "consent.json")));  // it never creates a consent record
        }
        finally { CleanUp(); }
    }

    [Fact]
    public void Not_activated_with_no_run_entry_is_fine()
    {
        using var d = new TempDir();
        try
        {
            var autoStart = new AutoStart(ScratchKey, "DlaTest");
            Dla.Agent.Tray.AgentContext.RecordNotActivated(new LifecycleLog(d.Path), "s1", autoStart);
            Assert.False(autoStart.IsEnabled);
        }
        finally { CleanUp(); }
    }

    [Fact]
    public void The_website_consent_writer_is_not_public()
    {
        // Only the (not yet built) activation handshake may call it; nothing shipped can.
        var method = typeof(ConsentStore).GetMethod("RecordWebsiteConsent",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(method);
        Assert.False(method!.IsPublic);
    }
}
