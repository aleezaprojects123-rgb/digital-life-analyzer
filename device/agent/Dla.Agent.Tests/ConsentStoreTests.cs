using System.Text.Json;
using Dla.Agent.Consent;

namespace Dla.Agent.Tests;

public class ConsentStoreTests
{
    private static string Valid(int version = ConsentText.Version, string at = "2026-10-28T05:15:00Z") =>
        $$"""{ "consentVersion": {{version}}, "acceptedAtUtc": "{{at}}" }""";

    // ---- the four cases from the task: missing, corrupt, old version, valid ----

    [Fact]
    public void Missing_file_means_not_consented_and_reading_creates_nothing()
    {
        using var t = new TempData();
        var store = new ConsentStore(t.DataDir);

        Assert.False(store.HasValidConsent());
        Assert.Null(store.AcceptedAtUtc());
        Assert.False(Directory.Exists(t.DataDir));     // reading never creates the folder or the file
    }

    [Theory]
    [InlineData("")]                                              // empty
    [InlineData("   ")]
    [InlineData("{ this is not json")]                            // broken JSON
    [InlineData("null")]
    [InlineData("[]")]                                            // not an object
    [InlineData("\"consented\"")]
    [InlineData("{}")]                                            // no fields
    [InlineData("""{ "consentVersion": 1 }""")]                   // missing time
    [InlineData("""{ "acceptedAtUtc": "2026-10-28T05:15:00Z" }""")] // missing version
    [InlineData("""{ "consentVersion": "1", "acceptedAtUtc": "2026-10-28T05:15:00Z" }""")]  // version is text
    [InlineData("""{ "consentVersion": 1.5, "acceptedAtUtc": "2026-10-28T05:15:00Z" }""")]  // version not whole
    [InlineData("""{ "consentVersion": true, "acceptedAtUtc": "2026-10-28T05:15:00Z" }""")]
    [InlineData("""{ "consentVersion": null, "acceptedAtUtc": "2026-10-28T05:15:00Z" }""")]
    [InlineData("""{ "consentVersion": 1, "acceptedAtUtc": 1761628500 }""")]               // time is a number
    [InlineData("""{ "consentVersion": 1, "acceptedAtUtc": null }""")]
    [InlineData("""{ "consentVersion": 1, "acceptedAtUtc": "yesterday" }""")]              // not a time
    [InlineData("""{ "consentVersion": 1, "acceptedAtUtc": "2026-10-28T10:15:00+05:00" }""")] // not UTC
    [InlineData("""{ "consentVersion": 1, "acceptedAtUtc": "2026-10-28T05:15:00" }""")]     // no Z
    [InlineData("""{ "consentVersion": 1, "acceptedAtUtc": "2026-10-28T05:15:00Z" """)]     // cut off
    public void Corrupt_or_malformed_file_means_not_consented(string content)
    {
        using var t = new TempData();
        t.WriteConsentFile(content);
        Assert.False(new ConsentStore(t.DataDir).HasValidConsent());
    }

    [Fact]
    public void A_huge_or_binary_file_means_not_consented()
    {
        using var t = new TempData();
        t.WriteConsentFile(Valid() + new string(' ', 10_000));      // valid JSON, but far too big to be our file
        Assert.False(new ConsentStore(t.DataDir).HasValidConsent());

        Directory.CreateDirectory(t.DataDir);
        File.WriteAllBytes(t.ConsentFile, [0xFF, 0xFE, 0x00, 0x01, 0x02, 0x80, 0x81]);
        Assert.False(new ConsentStore(t.DataDir).HasValidConsent());
    }

    [Fact]
    public void A_folder_where_the_file_should_be_means_not_consented_and_does_not_crash()
    {
        using var t = new TempData();
        Directory.CreateDirectory(t.ConsentFile);                    // consent.json is a directory
        Assert.False(new ConsentStore(t.DataDir).HasValidConsent());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void An_older_version_means_not_consented(int version)
    {
        using var t = new TempData();
        t.WriteConsentFile(Valid(version));
        Assert.False(new ConsentStore(t.DataDir).HasValidConsent());
    }

    [Fact]
    public void A_newer_version_also_means_not_consented_to_stay_on_the_safe_side()
    {
        using var t = new TempData();
        t.WriteConsentFile(Valid(ConsentText.Version + 1));
        Assert.False(new ConsentStore(t.DataDir).HasValidConsent());
    }

    [Fact]
    public void A_valid_file_for_the_current_version_means_consented()
    {
        using var t = new TempData();
        t.WriteConsentFile(Valid());
        var store = new ConsentStore(t.DataDir);

        Assert.True(store.HasValidConsent());
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 5, 15, 0, TimeSpan.Zero), store.AcceptedAtUtc());
    }

    [Fact]
    public void Extra_unknown_fields_do_not_make_a_valid_file_invalid()
    {
        using var t = new TempData();
        t.WriteConsentFile("""{ "consentVersion": 1, "acceptedAtUtc": "2026-10-28T05:15:00Z", "note": "hello" }""");
        Assert.True(new ConsentStore(t.DataDir).HasValidConsent());
    }

    // ---- saving ----

    [Fact]
    public void Saving_writes_exactly_the_two_documented_fields_in_UTC()
    {
        using var t = new TempData();
        var store = new ConsentStore(t.DataDir);

        // a local-time value must be written as UTC with a trailing Z
        store.SaveAcceptance(new DateTimeOffset(2026, 10, 28, 10, 15, 0, TimeSpan.FromHours(5)));

        using var doc = JsonDocument.Parse(File.ReadAllText(t.ConsentFile));
        var props = doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(["consentVersion", "acceptedAtUtc"], props);
        Assert.Equal(ConsentText.Version, doc.RootElement.GetProperty("consentVersion").GetInt32());
        Assert.Equal("2026-10-28T05:15:00Z", doc.RootElement.GetProperty("acceptedAtUtc").GetString());
        Assert.True(store.HasValidConsent());
    }

    [Fact]
    public void Saving_creates_the_folder_leaves_no_temporary_file_and_replaces_an_old_file()
    {
        using var t = new TempData();
        t.WriteConsentFile(Valid(0));                                  // an old, invalid consent
        var store = new ConsentStore(t.DataDir);
        Assert.False(store.HasValidConsent());

        store.SaveAcceptance(DateTimeOffset.UtcNow);

        Assert.True(store.HasValidConsent());
        Assert.Equal([t.ConsentFile], t.FilesCreated());              // only consent.json, no .tmp left over
    }

    [Fact]
    public void The_default_folder_is_LocalAppData_DigitalLifeAnalyzer()
    {
        var dir = ConsentStore.DefaultDataDir();
        Assert.Equal("DigitalLifeAnalyzer", Path.GetFileName(dir));
        Assert.Equal(Environment.GetEnvironmentVariable("LOCALAPPDATA"), Path.GetDirectoryName(dir));
        Assert.Equal("consent.json", Path.GetFileName(new ConsentStore(dir).FilePath));
    }

    // ---- the real gate ----

    [Fact]
    public void The_file_gate_follows_the_file_and_withdrawing_takes_effect_at_once()
    {
        using var t = new TempData();
        var store = new ConsentStore(t.DataDir);
        var gate = new FileConsentGate(store);

        Assert.False(gate.HasConsent);                  // default: no file, no consent
        store.SaveAcceptance(DateTimeOffset.UtcNow);
        Assert.True(gate.HasConsent);
        File.Delete(t.ConsentFile);                     // withdrawing consent
        Assert.False(gate.HasConsent);
        t.WriteConsentFile("{ broken");
        Assert.False(gate.HasConsent);
    }

    [Fact]
    public void The_old_default_gate_still_says_no()
    {
        Assert.False(new DefaultConsentGate().HasConsent);
    }
}
