using System.Text.Json;
using Dla.Agent.Media;

namespace Dla.Agent.Tests;

public class MediaTitleMatchTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 16, 27, 50, TimeSpan.Zero);

    private const string SmtcKuchizuke = "Kuchizuke Diamond - WEAVER「Yamada-kun and the Seven Witches OP Full Lyrics」";
    private const string TabKuchizukeAscii = "Kuchizuke Diamond - WEAVER - Yamada-kun and the Seven Witches OP Full Lyrics - YouTube";
    private const string TabKuchizukeBrackets = "Kuchizuke Diamond - WEAVER「Yamada-kun and the Seven Witches OP Full Lyrics」 - YouTube";

    private static string Event(string? title, string url = "https://www.youtube.com/watch", int seconds = 17)
    {
        var t = title is null ? "null" : JsonSerializer.Serialize(title, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $$"""{"schema_version":"1","id":"evt-k","start":"2026-10-30T16:27:50Z","end":"{{T0.AddSeconds(seconds):yyyy-MM-dd'T'HH:mm:ss'Z'}}","duration_seconds":{{seconds}},"source":"extension","app":null,"domain":"youtube.com","url":"{{url}}","title":{{t}},"presence":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}""";
    }

    private static IReadOnlyList<string> Run(string tabTitle, string smtcTitle, string artist = "VE4MIX", string playback = "paused")
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [new MediaState("chrome.exe", smtcTitle, artist, null, "music", playback)]);
        return MediaEnricher.Enrich(Event(tabTitle), timeline, T0.AddSeconds(17));
    }

    [Theory]
    [InlineData(TabKuchizukeAscii)]
    [InlineData(TabKuchizukeBrackets)]
    [InlineData("Kuchizuke  Diamond — WEAVER (Yamada-kun and the Seven Witches OP Full Lyrics) – YouTube")]
    [InlineData("(3) Kuchizuke Diamond - WEAVER「Yamada-kun and the Seven Witches OP Full Lyrics」 - YouTube")]
    public void The_exact_kuchizuke_pair_and_harmless_formatting_variants_match(string tabTitle)
    {
        var line = Assert.Single(Run(tabTitle, SmtcKuchizuke));
        var e = JsonDocument.Parse(line).RootElement;
        Assert.Equal(SmtcKuchizuke, e.GetProperty("media_title").GetString());
        Assert.Equal("VE4MIX", e.GetProperty("artist").GetString());
        Assert.Equal("music", e.GetProperty("media_type").GetString());
        Assert.Equal("paused", e.GetProperty("playback").GetString());
        Assert.Equal("chrome.exe", e.GetProperty("media_app").GetString());
        Assert.Equal(17, e.GetProperty("duration_seconds").GetInt32());
    }

    [Theory]
    [InlineData("Know Your Work Is Original With Grammarly's Plagiarism Checker", "Grammarly")]
    [InlineData("Yamada-kun and the Seven Witches Episode 1 English Dub", "Crunchyroll")]
    [InlineData("Heartless", "Kanye West")]
    public void Unrelated_or_advert_titles_are_not_attached(string smtcTitle, string artist)
    {
        var tab = Event(TabKuchizukeAscii);
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [new MediaState("chrome.exe", smtcTitle, artist, null, "music", "playing")]);
        var lines = MediaEnricher.Enrich(tab, timeline, T0.AddSeconds(17));
        Assert.Equal([tab], lines);
    }

    [Theory]
    [InlineData("碧氷拓海 - YouTube", "碧氷拓海", "Wataru Maeguchi - Topic")]
    [InlineData("Shawn Mendes, Camila Cabello - Señorita - YouTube", "Shawn Mendes, Camila Cabello - Señorita", "ShawnMendesVEVO")]
    [InlineData("Kanye West - Heartless (8D Audio Elite) - YouTube", "Kanye West - Heartless (8D Audio Elite)", "8D Audio Elite")]
    public void Existing_successful_matches_still_work(string tabTitle, string smtcTitle, string artist)
    {
        var e = JsonDocument.Parse(Assert.Single(Run(tabTitle, smtcTitle, artist, "playing"))).RootElement;
        Assert.Equal(smtcTitle, e.GetProperty("media_title").GetString());
        Assert.Equal(artist, e.GetProperty("artist").GetString());
    }
}
