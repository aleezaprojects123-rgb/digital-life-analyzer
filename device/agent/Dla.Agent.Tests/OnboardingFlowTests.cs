using Dla.Agent.Consent;
using Dla.Agent.Ui;

namespace Dla.Agent.Tests;

public class OnboardingFlowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 5, 15, 0, TimeSpan.Zero);

    private static (OnboardingFlow flow, ConsentStore store, FakeLog log, Counter asked) Make(TempData t, ConsentDecision answer)
    {
        var store = new ConsentStore(t.DataDir);
        var log = new FakeLog();
        var asked = new Counter();
        var flow = new OnboardingFlow(new FileConsentGate(store), store, () => { asked.Value++; return answer; }, log, () => Now);
        return (flow, store, log, asked);
    }

    private sealed class Counter { public int Value; }

    // ---- Decline writes nothing ----

    [Fact]
    public void Decline_writes_nothing_no_file_no_folder_no_log_line()
    {
        using var t = new TempData();
        var (flow, store, log, asked) = Make(t, ConsentDecision.Declined);

        Assert.Equal(OnboardingOutcome.Declined, flow.Run());

        Assert.Equal(1, asked.Value);
        Assert.False(store.HasValidConsent());
        Assert.False(File.Exists(t.ConsentFile));
        Assert.False(Directory.Exists(t.DataDir));
        Assert.Empty(log.Lines);
    }

    [Fact]
    public void Opening_the_app_is_never_consent_it_always_asks_when_nothing_is_saved()
    {
        using var t = new TempData();
        var (flow, _, _, asked) = Make(t, ConsentDecision.Declined);
        flow.Run();
        flow.Run();
        Assert.Equal(2, asked.Value);                 // asked again each start, never assumed
        Assert.False(Directory.Exists(t.DataDir));
    }

    [Fact]
    public void Decline_leaves_a_corrupt_or_old_file_exactly_as_it_was()
    {
        using var t = new TempData();
        t.WriteConsentFile("{ broken");
        var (flow, store, _, _) = Make(t, ConsentDecision.Declined);

        Assert.Equal(OnboardingOutcome.Declined, flow.Run());
        Assert.Equal("{ broken", File.ReadAllText(t.ConsentFile));
        Assert.False(store.HasValidConsent());
    }

    [Fact]
    public void Closing_the_real_window_counts_as_decline_and_writes_nothing()
    {
        using var t = new TempData();
        var store = new ConsentStore(t.DataDir);
        var log = new FakeLog();
        var flow = new OnboardingFlow(new FileConsentGate(store), store, () =>
        {
            using var form = new ConsentForm();
            form.Close();                         // the user clicks the X
            return form.Decision;
        }, log, () => Now);

        Assert.Equal(OnboardingOutcome.Declined, flow.Run());
        Assert.False(Directory.Exists(t.DataDir));
        Assert.Empty(log.Lines);
    }

    // ---- Accept ----

    [Fact]
    public void Accept_saves_the_consent_and_logs_one_line()
    {
        using var t = new TempData();
        var (flow, store, log, asked) = Make(t, ConsentDecision.Accepted);

        Assert.Equal(OnboardingOutcome.Accepted, flow.Run());

        Assert.Equal(1, asked.Value);
        Assert.True(store.HasValidConsent());
        Assert.Equal(Now, store.AcceptedAtUtc());
        Assert.Single(log.Lines);
        Assert.Contains("accepted", log.Lines[0]);
    }

    [Fact]
    public void Accept_replaces_an_old_or_corrupt_file_with_a_valid_one()
    {
        using var t = new TempData();
        t.WriteConsentFile("""{ "consentVersion": 0, "acceptedAtUtc": "2020-01-01T00:00:00Z" }""");
        var (flow, store, _, _) = Make(t, ConsentDecision.Accepted);

        Assert.Equal(OnboardingOutcome.Accepted, flow.Run());
        Assert.True(store.HasValidConsent());
    }

    [Fact]
    public void If_saving_fails_it_is_treated_as_not_consented()
    {
        using var t = new TempData();
        File.WriteAllText(t.DataDir, "I am a file, not a folder");     // the data folder path is blocked by a file
        var store = new ConsentStore(t.DataDir);
        var log = new FakeLog();
        var flow = new OnboardingFlow(new FileConsentGate(store), store, () => ConsentDecision.Accepted, log, () => Now);

        Assert.Equal(OnboardingOutcome.SaveFailed, flow.Run());
        Assert.False(store.HasValidConsent());
        Assert.Empty(log.Lines);
    }

    // ---- valid consent already saved ----

    [Fact]
    public void With_valid_consent_the_window_is_not_shown_and_the_file_is_untouched()
    {
        using var t = new TempData();
        new ConsentStore(t.DataDir).SaveAcceptance(Now.AddDays(-3));
        var before = File.ReadAllText(t.ConsentFile);
        var (flow, _, log, asked) = Make(t, ConsentDecision.Declined);

        Assert.Equal(OnboardingOutcome.ConsentOnRecord, flow.Run());

        Assert.Equal(0, asked.Value);
        Assert.Equal(before, File.ReadAllText(t.ConsentFile));
        Assert.Single(log.Lines);
    }

    [Fact]
    public void A_saved_consent_of_an_older_text_version_asks_again()
    {
        using var t = new TempData();
        t.WriteConsentFile("""{ "consentVersion": 0, "acceptedAtUtc": "2020-01-01T00:00:00Z" }""");
        var (flow, _, _, asked) = Make(t, ConsentDecision.Declined);

        Assert.Equal(OnboardingOutcome.Declined, flow.Run());
        Assert.Equal(1, asked.Value);
    }
}
