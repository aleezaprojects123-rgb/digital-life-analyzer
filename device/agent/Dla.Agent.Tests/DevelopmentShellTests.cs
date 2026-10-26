using System.Diagnostics;
using System.Reflection;

namespace Dla.Agent.Tests;

/// <summary>Runs the real built program and checks it does what the development shell promises.</summary>
public class DevelopmentShellTests
{
    private static string AgentExe => Path.Combine(AppContext.BaseDirectory, "Dla.Agent.exe");

    [Fact]
    public void Running_the_shell_prints_one_line_exits_by_itself_and_leaves_no_process()
    {
        Assert.True(File.Exists(AgentExe), $"built program not found at {AgentExe}");

        var info = new ProcessStartInfo(AgentExe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var process = Process.Start(info)!;
        var pid = process.Id;
        var output = process.StandardOutput.ReadToEnd();

        Assert.True(process.WaitForExit(10_000), "the shell did not exit on its own within 10 seconds");
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(
            "Development shell: no UI and no tracking started. Waiting for the future onboarding and consent screen.",
            output.Trim());
        Assert.Equal(string.Empty, process.StandardError.ReadToEnd());

        // nothing keeps running: the process is gone
        Assert.Null(Process.GetProcesses().FirstOrDefault(p => p.Id == pid && p.ProcessName == "Dla.Agent"));
    }

    [Fact]
    public void The_program_refers_to_nothing_that_could_track_store_or_send_data()
    {
        var agent = typeof(AgentHost).Assembly;
        var references = agent.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        foreach (var name in references)
        {
            foreach (var forbidden in new[] { "Sqlite", "Sql", "Onnx", "Http", "Sockets", "WebClient", "Mail", "Registry", "Hooks" })
                Assert.DoesNotContain(forbidden, name, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_program_uses_no_input_window_or_screen_apis()
    {
        // Look at every method the program calls. None may be a keyboard, mouse, window, screen or clipboard API.
        var agent = typeof(AgentHost).Assembly;
        var forbiddenTypes = new[] { "SendKeys", "Clipboard", "Screen", "Cursor", "Control.MousePosition", "Keyboard", "SetWindowsHookEx", "GetForegroundWindow", "GetLastInputInfo" };
        var allText = string.Join("\n", agent.GetTypes().SelectMany(t =>
            t.GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
             .Select(m => $"{t.FullName}.{m.Name}")));
        foreach (var f in forbiddenTypes) Assert.DoesNotContain(f, allText);
    }

    [Fact]
    public void The_only_classes_are_the_consent_gate_the_host_the_future_tray_and_the_future_context()
    {
        var names = typeof(AgentHost).Assembly.GetTypes()
            .Where(t => !t.Name.StartsWith('<') && !t.Name.Contains("DisplayClass"))
            .Select(t => t.Name).OrderBy(n => n).ToArray();
        Assert.Equal(["AgentApplicationContext", "AgentHost", "DefaultConsentGate", "IConsentGate", "ITray", "Program", "TrayController"], names);
    }
}
