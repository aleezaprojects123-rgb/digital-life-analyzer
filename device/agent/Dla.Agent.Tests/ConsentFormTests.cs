using System.Windows.Forms;
using Dla.Agent.Consent;
using Dla.Agent.Ui;

namespace Dla.Agent.Tests;

/// <summary>Tests the consent window's logic without ever showing it on screen.</summary>
public class ConsentFormTests
{
    /// <summary>
    /// Raises the button's Click event directly. (Button.PerformClick does nothing on a window that was never shown,
    /// so it would make these tests pass for the wrong reason.) This ignores the Enabled state on purpose, which
    /// also proves the click handlers have their own guards.
    /// </summary>
    private static void Click(Button button) =>
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(button, [EventArgs.Empty]);

    [Fact]
    public void The_window_starts_with_the_box_unticked_accept_disabled_and_the_decision_declined()
    {
        using var form = new ConsentForm();

        Assert.False(form.AgreeBox.Checked);
        Assert.Equal("I have read and agree", form.AgreeBox.Text);
        Assert.False(form.AcceptControl.Enabled);
        Assert.True(form.DeclineControl.Enabled);
        Assert.Equal(ConsentDecision.Declined, form.Decision);
        Assert.Equal("Accept", form.AcceptControl.Text);
        Assert.Equal("Decline", form.DeclineControl.Text);
    }

    [Fact]
    public void Ticking_the_box_enables_accept_and_unticking_disables_it_again()
    {
        using var form = new ConsentForm();
        form.AgreeBox.Checked = true;
        Assert.True(form.AcceptControl.Enabled);
        form.AgreeBox.Checked = false;
        Assert.False(form.AcceptControl.Enabled);
    }

    [Fact]
    public void Pressing_accept_without_ticking_the_box_does_nothing()
    {
        using var form = new ConsentForm();
        Click(form.AcceptControl);                      // even a click that gets through is ignored without the tick
        Assert.Equal(ConsentDecision.Declined, form.Decision);
    }

    [Fact]
    public void Ticking_then_pressing_accept_gives_accepted()
    {
        using var form = new ConsentForm();
        form.AgreeBox.Checked = true;
        Click(form.AcceptControl);
        Assert.Equal(ConsentDecision.Accepted, form.Decision);
    }

    [Fact]
    public void Pressing_decline_gives_declined_even_after_ticking_the_box()
    {
        using var form = new ConsentForm();
        form.AgreeBox.Checked = true;
        Click(form.DeclineControl);
        Assert.Equal(ConsentDecision.Declined, form.Decision);
    }

    [Fact]
    public void Closing_the_window_without_choosing_counts_as_decline_even_with_the_box_ticked()
    {
        using var form = new ConsentForm();
        form.AgreeBox.Checked = true;                  // ticked but never pressed Accept
        form.Close();
        Assert.Equal(ConsentDecision.Declined, form.Decision);
    }

    [Fact]
    public void There_is_no_default_button_so_enter_can_never_accept_and_escape_declines()
    {
        using var form = new ConsentForm();
        Assert.Null(form.AcceptButton);
        Assert.Same(form.DeclineControl, form.CancelButton);
    }

    [Fact]
    public void The_terms_are_shown_read_only_and_scrollable()
    {
        using var form = new ConsentForm();
        Assert.True(form.TermsBox.ReadOnly);
        Assert.True(form.TermsBox.Multiline);
        Assert.Equal(ScrollBars.Vertical, form.TermsBox.ScrollBars);
        Assert.Equal(ConsentText.WindowTitle, form.Text);
        Assert.False(form.MaximizeBox);
    }
}

public class ConsentTextTests
{
    private static readonly string Body = ConsentText.Body;

    [Fact]
    public void The_version_is_a_constant_and_starts_at_1()
    {
        Assert.Equal(1, ConsentText.Version);
    }

    [Fact]
    public void The_text_says_what_is_recorded_and_that_app_names_and_idle_are_not_active_yet()
    {
        Assert.Contains("domain", Body);
        Assert.Contains("cleaned-up web address", Body);
        Assert.Contains("page title", Body);
        Assert.Contains("NOT ACTIVE YET", Body);
        Assert.Contains("names of the apps you use", Body);
        Assert.Contains("away or idle", Body);
        Assert.Contains("planned features. They do not run today", Body);
        Assert.Contains("incognito", Body);
    }

    [Fact]
    public void The_text_matches_CLAUDE_md_on_page_text_it_is_never_stored_and_never_uploaded()
    {
        Assert.Contains("Later versions may read page or document text on this PC only to work out a category.", Body);
        Assert.Contains("That text is never stored and never uploaded; only the category label is kept.", Body);
        Assert.DoesNotContain("and logged", Body);
        Assert.DoesNotContain("In the future the app may read", Body);   // the old wording is gone
    }

    [Fact]
    public void The_text_promises_an_audit_note_each_time_page_text_is_read_with_time_and_site_or_app_but_no_text()
    {
        Assert.Contains("Each time it is read, a note that it was read (time and site or app, no text) is added to your audit log.", Body);
    }

    [Fact]
    public void The_page_text_section_is_marked_NOT_ACTIVE_YET_like_app_names_and_idle()
    {
        var lines = Body.Replace("\r\n", "\n").Split('\n');
        Assert.Contains("TEXT ON THE PAGE (NOT ACTIVE YET)", lines);
        Assert.Contains("NOT ACTIVE YET", lines);              // the existing section for app names and idle is still there
        Assert.Contains("planned features. They do not run today", Body);
    }

    [Fact]
    public void The_text_says_data_stays_on_this_PC_for_30_days_and_only_encrypted_summaries_ever_leave()
    {
        Assert.Contains("stay on this PC", Body);
        Assert.Contains("30 days", Body);
        Assert.Contains("unless you turn on sync", Body);
        Assert.Contains("only encrypted daily summaries", Body);
        Assert.Contains("never your individual records", Body);
    }

    [Fact]
    public void The_text_is_honest_that_sync_is_not_end_to_end_encrypted_as_the_encryption_decision_requires()
    {
        Assert.Contains("not end-to-end encryption", Body);
        Assert.Contains("The server can read the summaries", Body);
    }

    [Fact]
    public void The_text_promises_no_screenshots_keystrokes_or_typed_text_and_that_tracking_is_optional()
    {
        Assert.Contains("No screenshots", Body);
        Assert.Contains("No keystrokes and no typed text", Body);
        Assert.Contains("optional", Body);
        Assert.Contains("pause or stop it from the tray icon", Body);
        Assert.Contains("If you decline or close this window, nothing is recorded and nothing is saved", Body);
        Assert.Contains("Opening the app is not consent", Body);
    }

    [Fact]
    public void The_text_never_claims_tracking_is_on()
    {
        Assert.DoesNotContain("Tracking: ON", Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("is being recorded", Body, StringComparison.OrdinalIgnoreCase);
    }
}
