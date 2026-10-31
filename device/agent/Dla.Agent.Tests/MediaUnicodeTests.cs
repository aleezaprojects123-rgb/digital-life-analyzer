using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Dla.Agent.Link;
using Dla.Agent.Media;

namespace Dla.Agent.Tests;

public class MediaUnicodeAndTimingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);
    private const string Japanese = "口づけダイヤモンド";
    private const string Senorita = "Señorita";

    private static MediaState Chrome(string title, string artist = "VE4MIX", string playback = "playing") =>
        new("chrome.exe", title, artist, null, "music", playback);

    private static string Event(string? title, string url = "https://www.youtube.com/watch", int seconds = 60)
    {
        var domain = new Uri(url).Host.Replace("www.", "");
        var t = title is null
            ? "null"
            : JsonSerializer.Serialize(title, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return $$"""{"schema_version":"1","id":"evt-u","start":"2026-10-30T10:00:00Z","end":"{{T0.AddSeconds(seconds):yyyy-MM-dd'T'HH:mm:ss'Z'}}","duration_seconds":{{seconds}},"source":"extension","app":null,"domain":"{{domain}}","url":"{{url}}","title":{{t}},"presence":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}""";
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    [Theory]
    [InlineData(Japanese)]
    [InlineData(Senorita)]
    [InlineData("Weaver「口づけダイヤモンド」 - Señorita 🎵")]
    public void Unicode_media_text_survives_enrichment_and_the_real_file_byte_for_byte(string title)
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-2), [Chrome(title, artist: "Wataru Maeguchi - Topic")]);
        var lines = MediaEnricher.Enrich(Event(title + " - YouTube"), timeline, T0.AddSeconds(60));
        Assert.Equal(title, Parse(Assert.Single(lines)).GetProperty("media_title").GetString());

        var path = Path.Combine(Path.GetTempPath(), $"dla-utf8-{Guid.NewGuid():N}.jsonl");
        try
        {
            Assert.True(new JsonlEventSink(path).AppendEvents(lines));
            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
            var text = new UTF8Encoding(false, true).GetString(bytes);   // throws on invalid UTF-8
            Assert.Equal(title, Parse(text.TrimEnd('\n')).GetProperty("media_title").GetString());
            if (title == Japanese) Assert.True(IndexOf(bytes, Encoding.UTF8.GetBytes(Japanese)) >= 0, "real UTF-8 bytes, not escapes");
            Assert.DoesNotContain("├", text);
            Assert.DoesNotContain("Ã", text);
        }
        finally { File.Delete(path); }
    }

    private static int IndexOf(byte[] haystack, byte[] needle) =>
        haystack.AsSpan().IndexOf(needle);

    [Fact]
    public void The_http_body_decoding_keeps_unicode()
    {
        var body = Event(Japanese + " " + Senorita);
        var bytes = Encoding.UTF8.GetBytes(body);
        Assert.Equal(body, Encoding.UTF8.GetString(bytes));
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(Encoding.UTF8.GetString(bytes), out _, out _));
    }

    [Fact]
    public void A_session_that_already_exists_when_the_collector_starts_is_attached()
    {
        var source = new FakeMediaSource { States = [Chrome(Japanese)] };
        var now = T0;
        using var collector = new MediaCollector(source, () => true, clock: () => now);
        collector.StartForTestAsync().GetAwaiter().GetResult();

        now = T0.AddSeconds(30);
        var e = Parse(Assert.Single(collector.Enrich(Event(Japanese + " - YouTube", seconds: 30))));
        Assert.Equal(Japanese, e.GetProperty("media_title").GetString());
    }

    [Fact]
    public void Media_that_appears_two_seconds_after_the_period_started_still_covers_the_whole_period()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(2), [Chrome(Japanese)]);
        var e = Parse(Assert.Single(MediaEnricher.Enrich(Event(Japanese + " - YouTube", seconds: 40), timeline, T0.AddSeconds(40))));
        Assert.Equal(40, e.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(Japanese, e.GetProperty("media_title").GetString());
    }

    [Fact]
    public void Media_that_appears_ten_seconds_late_splits_without_losing_time()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(10), [Chrome(Japanese)]);
        var lines = MediaEnricher.Enrich(Event(Japanese + " - YouTube", seconds: 40), timeline, T0.AddSeconds(40));
        Assert.Equal(2, lines.Count);
        Assert.Equal(JsonValueKind.Undefined, Probe(lines[0], "media_title"));
        Assert.Equal(Japanese, Parse(lines[1]).GetProperty("media_title").GetString());
        Assert.Equal(40, lines.Sum(l => Parse(l).GetProperty("duration_seconds").GetInt32()));
    }

    private static JsonValueKind Probe(string line, string name) =>
        Parse(line).TryGetProperty(name, out var v) ? v.ValueKind : JsonValueKind.Undefined;

    [Fact]
    public void Two_songs_one_after_the_other_in_the_same_tab_become_two_events()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0, [Chrome("Kuchizuke Diamond - WEAVER", "VE4MIX")]);
        timeline.Observe(T0.AddSeconds(25), [Chrome(Japanese, "Wataru Maeguchi - Topic")]);
        var lines = MediaEnricher.Enrich(Event(null, "https://music.youtube.com/watch"), timeline, T0.AddSeconds(60));
        Assert.Equal(2, lines.Count);
        Assert.Equal("VE4MIX", Parse(lines[0]).GetProperty("artist").GetString());
        Assert.Equal("Wataru Maeguchi - Topic", Parse(lines[1]).GetProperty("artist").GetString());
    }

    [Fact]
    public void An_advert_title_is_not_attached_to_a_video_page()
    {
        var ad = new MediaTimeline();
        ad.Observe(T0.AddSeconds(-1), [Chrome("Know Your Work Is Original With Grammarly's Plagiarism Checker", "Grammarly")]);
        var tab = Event("Kuchizuke Diamond - WEAVER「Yamada-kun and the Seven Witches OP Full Lyrics」 - YouTube");
        Assert.Equal([tab], MediaEnricher.Enrich(tab, ad, T0.AddSeconds(60)));
    }

    [Fact]
    public void The_reason_for_no_match_is_reported_without_any_title()
    {
        var reasons = new List<string>();
        var ad = new MediaTimeline();
        ad.Observe(T0.AddSeconds(-1), [Chrome("Some advert")]);
        MediaEnricher.Enrich(Event("Kuchizuke Diamond - YouTube"), ad, T0.AddSeconds(60), reasons.Add);
        MediaEnricher.Enrich(Event("Kuchizuke Diamond - YouTube"), new MediaTimeline(), T0.AddSeconds(60), reasons.Add);
        Assert.Equal(["browser_media_but_title_did_not_match", "no_browser_media_in_period"], reasons);
        Assert.DoesNotContain(reasons, r => r.Contains("Kuchizuke", StringComparison.OrdinalIgnoreCase) || r.Contains("advert", StringComparison.OrdinalIgnoreCase));   // reason codes only, never a title
    }

    [Fact]
    public async Task A_change_that_arrives_while_a_refresh_is_running_is_not_lost()
    {
        var source = new FakeMediaSource { States = [Chrome("First")] };
        var now = T0;
        using var collector = new MediaCollector(source, () => true, clock: () => now);
        await collector.StartForTestAsync();
        source.States = [Chrome(Japanese)];
        now = T0.AddSeconds(5);
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => collector.RefreshAsync()));
        var segs = collector.Timeline.Overlapping(T0.AddSeconds(6), T0.AddSeconds(7), T0.AddSeconds(10));
        Assert.Equal(Japanese, Assert.Single(segs).State.Title);
    }
}
