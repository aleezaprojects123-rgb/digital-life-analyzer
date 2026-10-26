using Dla.Agent.Consent;
using Dla.Agent.Tray;

namespace Dla.Agent.Tests;

/// <summary>A tray that only counts what was asked of it. No real icon is ever created in these tests.</summary>
internal sealed class FakeTray : ITray
{
    public int ShowCalls, DisposeCalls;
    public bool ShowThrows;
    public event EventHandler? QuitRequested;

    public void Show()
    {
        ShowCalls++;
        if (ShowThrows) throw new InvalidOperationException("boom");
    }

    public void Dispose() => DisposeCalls++;
    public void RaiseQuit() => QuitRequested?.Invoke(this, EventArgs.Empty);
}

internal sealed class FakeGate : IConsentGate
{
    public bool HasConsent { get; set; }
}

public class DefaultConsentGateTests
{
    [Fact]
    public void Default_gate_always_says_not_consented()
    {
        var gate = new DefaultConsentGate();
        for (var i = 0; i < 5; i++) Assert.False(gate.HasConsent);
    }
}

public class AgentHostTests
{
    [Fact]
    public void Without_consent_nothing_is_started_and_no_tray_is_ever_created()
    {
        var created = 0;
        var host = new AgentHost(new FakeGate { HasConsent = false }, () => { created++; return new FakeTray(); });

        Assert.False(host.TryStart());
        Assert.False(host.IsRunning);
        Assert.Null(host.Tray);
        Assert.Equal(0, created);
    }

    [Fact]
    public void Asking_again_without_consent_still_creates_nothing()
    {
        var created = 0;
        var host = new AgentHost(new FakeGate(), () => { created++; return new FakeTray(); });
        for (var i = 0; i < 10; i++) Assert.False(host.TryStart());
        Assert.Equal(0, created);
    }

    [Fact]
    public void The_default_gate_keeps_the_host_from_starting()
    {
        var created = 0;
        var host = new AgentHost(new DefaultConsentGate(), () => { created++; return new FakeTray(); });
        Assert.False(host.TryStart());
        Assert.Equal(0, created);
    }

    [Fact]
    public void With_consent_the_tray_is_created_once_and_shown()
    {
        var tray = new FakeTray();
        var created = 0;
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => { created++; return tray; });

        Assert.True(host.TryStart());
        Assert.True(host.IsRunning);
        Assert.Same(tray, host.Tray);
        Assert.Equal((1, 1), (created, tray.ShowCalls));
    }

    [Fact]
    public void Starting_twice_never_creates_a_second_tray()
    {
        var created = 0;
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => { created++; return new FakeTray(); });
        Assert.True(host.TryStart());
        Assert.True(host.TryStart());
        Assert.Equal(1, created);
    }

    [Fact]
    public void Consent_is_checked_at_the_moment_of_starting_not_remembered()
    {
        var gate = new FakeGate { HasConsent = false };
        var created = 0;
        var host = new AgentHost(gate, () => { created++; return new FakeTray(); });

        Assert.False(host.TryStart());      // not yet accepted
        gate.HasConsent = true;             // the user accepts on the future onboarding screen
        Assert.True(host.TryStart());
        Assert.Equal(1, created);
    }

    [Fact]
    public void Stop_removes_the_tray_and_allows_a_clean_restart_only_with_consent()
    {
        var tray = new FakeTray();
        var gate = new FakeGate { HasConsent = true };
        var host = new AgentHost(gate, () => tray);
        host.TryStart();

        host.Stop();
        Assert.False(host.IsRunning);
        Assert.Equal(1, tray.DisposeCalls);

        gate.HasConsent = false;
        Assert.False(host.TryStart());
    }

    [Fact]
    public void A_tray_that_fails_to_show_is_disposed_and_the_host_stays_stopped()
    {
        var tray = new FakeTray { ShowThrows = true };
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => tray);

        Assert.Throws<InvalidOperationException>(() => host.TryStart());
        Assert.False(host.IsRunning);
        Assert.Equal(1, tray.DisposeCalls);
    }

    [Fact]
    public void Disposing_the_host_removes_a_running_tray()
    {
        var tray = new FakeTray();
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => tray);
        host.TryStart();
        host.Dispose();
        Assert.Equal(1, tray.DisposeCalls);
    }
}

public class AgentApplicationContextTests
{
    [Fact]
    public void The_tray_only_context_cannot_be_created_for_an_agent_that_has_not_started()
    {
        var host = new AgentHost(new DefaultConsentGate(), () => new FakeTray());
        Assert.False(host.TryStart());
        Assert.Throws<InvalidOperationException>(() => new AgentApplicationContext(host));
    }

    [Fact]
    public void Quit_in_the_tray_ends_the_context_and_disposing_it_removes_the_tray()
    {
        var tray = new FakeTray();
        var host = new AgentHost(new FakeGate { HasConsent = true }, () => tray);
        host.TryStart();

        using var context = new AgentApplicationContext(host);
        var exited = false;
        context.ThreadExit += (_, _) => exited = true;

        tray.RaiseQuit();
        Assert.True(exited);

        context.Dispose();
        Assert.Equal(1, tray.DisposeCalls);
    }
}
