using System.Text;
using System.Text.Json;
using Dla.Agent.Link;
using Dla.Agent.Media;

namespace Dla.Agent.Tests;

public class UniversalBrowserEventTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private static string Event(string platform, string activity, string title = "Heartless - YouTube", string url = "https://www.youtube.com/watch", string domain = "youtube.com", int seconds = 30) =>
        $$"""{"schema_version":"1","id":"evt-1","start":"2026-10-30T10:00:00Z","end":"{{T0.AddSeconds(seconds):yyyy-MM-dd'T'HH:mm:ss'Z'}}","duration_seconds":{{seconds}},"source":"extension","platform":"{{platform}}","domain":"{{domain}}","url":"{{url}}","title":"{{title}}","activity_type":"{{activity}}","media_title":null,"artist":null,"album":null,"media_type":null,"playback":null,"media_app":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}""";

    [Theory]
    [InlineData("youtube", "video")]
    [InlineData("youtube", "short_video")]
    [InlineData("youtube", "search")]
    [InlineData("youtube", "music")]
    [InlineData("unknown", "webpage")]
    [InlineData("whatsapp", "messaging")]
    [InlineData("instagram", "reel")]
    public void Known_platforms_and_activity_types_are_accepted(string platform, string activity)
    {
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(Event(platform, activity), out _, out var error), error);
    }

    [Theory]
    [InlineData("myspace", "video", "invalid_platform")]
    [InlineData("youtube", "made_up", "invalid_activity_type")]
    public void Values_outside_the_vocabulary_are_refused(string platform, string activity, string expected)
    {
        Assert.False(LocalStatusLink.TryValidateExtensionEvent(Event(platform, activity), out _, out var error));
        Assert.Equal(expected, error);
    }

    [Fact]
    public void An_older_event_without_platform_or_activity_type_is_still_accepted()
    {
        var older = Event("youtube", "video").Replace("\"platform\":\"youtube\",", "").Replace(",\"activity_type\":\"video\"", "");
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(older, out _, out var error), error);
    }

    [Fact]
    public void Wrong_types_for_the_new_fields_are_refused()
    {
        var bad = Event("youtube", "video").Replace("\"platform\":\"youtube\"", "\"platform\":5");
        Assert.False(LocalStatusLink.TryValidateExtensionEvent(bad, out _, out var error));
        Assert.Equal("invalid_platform", error);
    }

    [Fact]
    public void Enrichment_keeps_platform_and_activity_and_fills_only_the_media_fields()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [new MediaState("chrome.exe", "Heartless", "Kanye West", null, "music", "playing")]);
        var e = JsonDocument.Parse(Assert.Single(MediaEnricher.Enrich(Event("youtube", "video"), timeline, T0.AddSeconds(30)))).RootElement;
        Assert.Equal("youtube", e.GetProperty("platform").GetString());
        Assert.Equal("video", e.GetProperty("activity_type").GetString());
        Assert.Equal("Heartless", e.GetProperty("media_title").GetString());
        Assert.Equal("Kanye West", e.GetProperty("artist").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("album").ValueKind);
        Assert.Equal(JsonValueKind.Null, e.GetProperty("category").ValueKind);
    }

    [Fact]
    public void Without_media_the_event_is_stored_unchanged_with_null_media_fields()
    {
        var input = Event("unknown", "webpage", title: "Example Domain", url: "https://example.com/", domain: "example.com");
        var lines = MediaEnricher.Enrich(input, new MediaTimeline(), T0.AddSeconds(30));
        Assert.Equal([input], lines);
        Assert.Equal(JsonValueKind.Null, JsonDocument.Parse(lines[0]).RootElement.GetProperty("media_title").ValueKind);
    }

    [Fact]
    public void A_media_split_keeps_every_piece_valid_contiguous_and_typed()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [new MediaState("chrome.exe", "Song A", "Artist A", null, "music", "playing")]);
        timeline.Observe(T0.AddSeconds(15), [new MediaState("chrome.exe", "Song B", "Artist B", null, "music", "playing")]);
        var lines = MediaEnricher.Enrich(Event("youtube", "music", url: "https://music.youtube.com/watch", title: "Song - YouTube Music"), timeline, T0.AddSeconds(30));
        Assert.Equal(2, lines.Count);
        foreach (var l in lines)
        {
            Assert.True(LocalStatusLink.TryValidateExtensionEvent(l, out _, out var error), error);
            Assert.Equal("music", JsonDocument.Parse(l).RootElement.GetProperty("activity_type").GetString());
        }
        Assert.Equal(JsonDocument.Parse(lines[0]).RootElement.GetProperty("end").GetString(), JsonDocument.Parse(lines[1]).RootElement.GetProperty("start").GetString());
    }

    [Fact]
    public void An_instagram_reel_is_valid_and_never_gets_media_from_an_unrelated_tab()
    {
        var reel = Event("instagram", "reel", title: "Alice on Instagram: \\\"Study with me\\\"", url: "https://www.instagram.com/reel/*/", domain: "instagram.com");
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(reel, out _, out var error), error);

        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [new MediaState("chrome.exe", "Heartless", "Kanye West", null, "music", "playing")]);   // another tab is playing a song
        Assert.Equal([reel], MediaEnricher.Enrich(reel, timeline, T0.AddSeconds(30)));
    }

    [Fact]
    public void The_content_count_field_passes_through_the_agent_untouched()
    {
        var withMeta = Event("instagram", "reel", title: "Alice on Instagram", url: "https://www.instagram.com/reel/*", domain: "instagram.com")
            .Replace("\"media_title\":null", "\"content_count\":1,\"session_id\":null,\"media_title\":null");
        Assert.True(LocalStatusLink.TryValidateExtensionEvent(withMeta, out _, out var error), error);
        Assert.Equal([withMeta], MediaEnricher.Enrich(withMeta, new MediaTimeline(), T0.AddSeconds(30)));

        var sink = new FakeEventSink();
        var raw = LinkTestData.Request(49999, method: "POST", path: "/events", origin: "ORIGIN", extra: $"Content-Length: {Encoding.UTF8.GetByteCount(withMeta)}\r\n");
        var answer = LocalStatusLink.Evaluate(raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)],LinkTestData.Config(49999, LinkTestData.ChromeId), new FixedStatus(new AgentStatus(true, true, 1)), withMeta, sink);
        Assert.Equal(200, answer.Status);
        Assert.Contains("\"content_count\":1", Assert.Single(sink.Events));
    }

    [Fact]
    public void An_event_split_at_a_media_change_counts_its_content_item_once()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [new MediaState("chrome.exe", "Song A", "Artist A", null, "music", "playing")]);
        timeline.Observe(T0.AddSeconds(10), [new MediaState("chrome.exe", "Song A", "Artist A", null, "music", "paused")]);
        timeline.Observe(T0.AddSeconds(20), [new MediaState("chrome.exe", "Song A", "Artist A", null, "music", "playing")]);
        var counted = Event("youtube", "video", title: "Song A - YouTube").Replace("\"media_title\":null", "\"content_count\":1,\"media_title\":null");

        var lines = MediaEnricher.Enrich(counted, timeline, T0.AddSeconds(30));

        Assert.True(lines.Count >= 2, "the playing and paused parts are separate events");
        var counts = lines.Select(l => JsonDocument.Parse(l).RootElement.GetProperty("content_count").GetInt32()).ToList();
        Assert.Equal(1, counts.Sum());
        Assert.Equal(1, counts[0]);
        Assert.All(counts.Skip(1), c => Assert.Equal(0, c));
        Assert.Equal(30, lines.Sum(l => JsonDocument.Parse(l).RootElement.GetProperty("duration_seconds").GetInt32()));
    }

    [Fact]
    public void An_event_without_a_count_is_split_without_inventing_one()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [new MediaState("chrome.exe", "Song A", "Artist A", null, "music", "playing")]);
        timeline.Observe(T0.AddSeconds(15), [new MediaState("chrome.exe", "Song B", "Artist B", null, "music", "playing")]);
        var lines = MediaEnricher.Enrich(Event("youtube", "music", url: "https://music.youtube.com/watch", title: "Song - YouTube Music"), timeline, T0.AddSeconds(30));
        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.False(JsonDocument.Parse(l).RootElement.TryGetProperty("content_count", out _)));
    }
}
