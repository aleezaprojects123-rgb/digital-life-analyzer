using Dla.Agent.Consent;

namespace Dla.Agent.Tests;

/// <summary>How an older saved consent is handled when the consent text version changes.</summary>
public class ConsentVersionTests
{
    [Fact]
    public void A_user_who_accepted_the_old_text_is_asked_again_and_the_new_acceptance_is_kept_without_asking_a_third_time()
    {
        using var data = new TempData();
        data.WriteConsentFile("""{ "consentVersion": 1, "acceptedAtUtc": "2026-10-29T17:39:30Z" }""");   // exactly what version 1 saved
        var store = new ConsentStore(data.DataDir);
        var asked = 0;
        OnboardingFlow Flow() => new(new FileConsentGate(store), store, () => { asked++; return ConsentDecision.Accepted; }, new FakeLog(), () => new DateTimeOffset(2026, 11, 1, 8, 0, 0, TimeSpan.Zero));

        Assert.Equal(OnboardingOutcome.Accepted, Flow().Run());             // the old consent no longer counts, so the new text is shown
        Assert.Equal(1, asked);
        Assert.Equal(OnboardingOutcome.ConsentOnRecord, Flow().Run());      // and now the saved acceptance is for the current text
        Assert.Equal(1, asked);
        Assert.True(store.HasValidConsent());
    }

    [Fact]
    public void While_only_the_old_consent_is_on_record_nothing_starts_and_start_with_Windows_is_removed()
    {
        using var data = new TempData();
        data.WriteConsentFile("""{ "consentVersion": 1, "acceptedAtUtc": "2026-10-29T17:39:30Z" }""");
        var startup = new FakeRegistration();
        var created = 0;
        var host = new AgentHost(new FileConsentGate(new ConsentStore(data.DataDir)), () => { created++; return new FakeTray(); }, () => { created++; return new FakeLink(); },
            startup: startup);

        Assert.False(host.TryStart());
        Assert.Equal(0, created);
        Assert.Equal((0, 1), (startup.Ensures, startup.Removes));
    }
}
