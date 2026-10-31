using System.Text;
using System.Text.Json;
using Dla.Agent.Link;
using Dla.Agent.Media;

namespace Dla.Agent.Tests;

internal sealed class FakeMediaSource : IMediaSource
{
    public List<MediaState> States { get; set; } = [];
    public event Action? Changed;
    public Task<bool> InitializeAsync() => Task.FromResult(true);
    public Task<IReadOnlyList<MediaState>> SnapshotAsync() => Task.FromResult<IReadOnlyList<MediaState>>(States.ToList());
    public void Raise() => Changed?.Invoke();
    public void Dispose() { }
}

public class MediaEnricherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    private static MediaState Song(string title, string artist = "Kanye West", string playback = "playing", string app = "chrome.exe", string? kind = "music") =>
        new(app, title, artist, null, kind, playback);

    private static string Event(string? title, string url = "https://www.youtube.com/watch", int seconds = 60, string source = "extension", string id = "evt-1")
    {
        var domain = new Uri(url).Host.StartsWith("www.") ? new Uri(url).Host[4..] : new Uri(url).Host;
        var t = title is null ? "null" : JsonSerializer.Serialize(title);
        var app = source == "extension" ? "null" : "\"spotify.exe\"";
        return $$"""
        {"schema_version":"1","id":"{{id}}","start":"2026-10-30T10:00:00Z","end":"{{T0.AddSeconds(seconds):yyyy-MM-dd'T'HH:mm:ss'Z'}}","duration_seconds":{{seconds}},"source":"{{source}}","app":{{app}},"domain":"{{domain}}","url":"{{url}}","title":{{t}},"presence":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}
        """;
    }

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    [Fact]
    public void One_song_for_the_whole_period_is_attached_without_changing_time()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heartless")]);
        var input = Event("Heartless - Kanye West - YouTube");

        var lines = MediaEnricher.Enrich(input, timeline, T0.AddSeconds(60));

        var e = Parse(Assert.Single(lines));
        Assert.Equal("evt-1", e.GetProperty("id").GetString());
        Assert.Equal(60, e.GetProperty("duration_seconds").GetInt32());
        Assert.Equal("Heartless", e.GetProperty("media_title").GetString());
        Assert.Equal("Kanye West", e.GetProperty("artist").GetString());
        Assert.Equal(JsonValueKind.Null, e.GetProperty("album").ValueKind);
        Assert.Equal("music", e.GetProperty("media_type").GetString());
        Assert.Equal("playing", e.GetProperty("playback").GetString());
        Assert.Equal("chrome.exe", e.GetProperty("media_app").GetString());
        Assert.Equal("2026-10-30T10:00:00Z", e.GetProperty("start").GetString());
    }

    [Fact]
    public void A_song_change_in_the_same_page_splits_the_period_exactly()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heartless")]);
        timeline.Observe(T0.AddSeconds(30), [Song("Runaway")]);
        var input = Event(null, "https://music.youtube.com/watch");

        var lines = MediaEnricher.Enrich(input, timeline, T0.AddSeconds(60));

        Assert.Equal(2, lines.Count);
        var a = Parse(lines[0]);
        var b = Parse(lines[1]);
        Assert.Equal("evt-1", a.GetProperty("id").GetString());
        Assert.Equal("evt-1-m1", b.GetProperty("id").GetString());
        Assert.Equal("Heartless", a.GetProperty("media_title").GetString());
        Assert.Equal("Runaway", b.GetProperty("media_title").GetString());
        Assert.Equal(a.GetProperty("end").GetString(), b.GetProperty("start").GetString());
        Assert.Equal("2026-10-30T10:00:00Z", a.GetProperty("start").GetString());
        Assert.Equal("2026-10-30T10:01:00Z", b.GetProperty("end").GetString());
        Assert.Equal(60, a.GetProperty("duration_seconds").GetInt32() + b.GetProperty("duration_seconds").GetInt32());
        Assert.Equal(30, a.GetProperty("duration_seconds").GetInt32());
    }

    [Fact]
    public void Short_A_then_Short_B_in_one_tab_gives_two_events_even_without_a_tab_title()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-1), [Song("Short A", "Channel A", kind: "video")]);
        timeline.Observe(T0.AddSeconds(20), [Song("Short B", "Channel B", kind: "video")]);
        var input = Event(null, "https://www.youtube.com/shorts/*", 45);

        var lines = MediaEnricher.Enrich(input, timeline, T0.AddSeconds(45));

        Assert.Equal(2, lines.Count);
        Assert.Equal("Short A", Parse(lines[0]).GetProperty("media_title").GetString());
        Assert.Equal("short", Parse(lines[0]).GetProperty("media_type").GetString());
        Assert.Equal("Short B", Parse(lines[1]).GetProperty("media_title").GetString());
        Assert.Equal(45, lines.Sum(l => Parse(l).GetProperty("duration_seconds").GetInt32()));
        Assert.All(lines, l => Assert.Equal("https://www.youtube.com/shorts/*", Parse(l).GetProperty("url").GetString()));
    }

    [Fact]
    public void Media_from_another_tab_is_not_attached_to_an_unrelated_page()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heartless")]);
        var input = Event("Inbox - Mail", "https://mail.example.com/inbox");

        var lines = MediaEnricher.Enrich(input, timeline, T0.AddSeconds(60));

        Assert.Equal([input], lines);
    }

    [Fact]
    public void A_page_without_a_title_on_a_non_media_site_gets_nothing()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heartless")]);
        var input = Event(null, "https://example.com/page");
        Assert.Equal([input], MediaEnricher.Enrich(input, timeline, T0.AddSeconds(60)));
    }

    [Fact]
    public void Two_things_playing_at_once_with_no_title_is_ambiguous_and_gets_nothing()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("One", app: "chrome.exe"), Song("Two", app: "msedge.exe")]);
        var input = Event(null, "https://www.youtube.com/shorts/*");
        Assert.Equal([input], MediaEnricher.Enrich(input, timeline, T0.AddSeconds(60)));
    }

    [Fact]
    public void Paused_media_is_reported_as_paused()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heartless", playback: "paused")]);
        var lines = MediaEnricher.Enrich(Event("Heartless - YouTube"), timeline, T0.AddSeconds(60));
        Assert.Equal("paused", Parse(Assert.Single(lines)).GetProperty("playback").GetString());
    }

    [Fact]
    public void A_one_second_blip_is_absorbed_instead_of_creating_a_tiny_event()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heartless")]);
        timeline.Observe(T0.AddSeconds(30), [Song("Runaway")]);
        timeline.Observe(T0.AddSeconds(31), [Song("Heartless")]);
        var lines = MediaEnricher.Enrich(Event(null, "https://music.youtube.com/watch"), timeline, T0.AddSeconds(60));
        var e = Parse(Assert.Single(lines));
        Assert.Equal(60, e.GetProperty("duration_seconds").GetInt32());
    }

    [Fact]
    public void Desktop_apps_and_non_extension_events_are_left_alone()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heartless", app: "Spotify.exe")]);
        var input = Event("Heartless - YouTube");
        Assert.Equal([input], MediaEnricher.Enrich(input, timeline, T0.AddSeconds(60)));

        timeline.Observe(T0.AddSeconds(-4), [Song("Heartless")]);
        var agent = Event("Heartless", source: "agent");
        Assert.Equal([agent], MediaEnricher.Enrich(agent, timeline, T0.AddSeconds(60)));
    }

    [Fact]
    public void Nothing_playing_returns_the_original_line_untouched()
    {
        var input = Event("Heartless - YouTube");
        Assert.Equal([input], MediaEnricher.Enrich(input, new MediaTimeline(), T0.AddSeconds(60)));
        Assert.Equal(["not json"], MediaEnricher.Enrich("not json", new MediaTimeline(), T0));
    }

    [Fact]
    public void Text_is_cleaned_and_limited_to_the_contract_length()
    {
        var timeline = new MediaTimeline();
        timeline.Observe(T0.AddSeconds(-5), [Song("Heart\u0007less\n" + new string('x', 300))]);
        var lines = MediaEnricher.Enrich(Event(null, "https://music.youtube.com/watch"), timeline, T0.AddSeconds(60));
        var title = Parse(Assert.Single(lines)).GetProperty("media_title").GetString()!;
        Assert.Equal(200, title.Length);
        Assert.DoesNotContain('\u0007', title);
        Assert.DoesNotContain('\n', title);
    }
}

public class MediaCollectorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 30, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Nothing_is_collected_while_recording_is_not_allowed()
    {
        var source = new FakeMediaSource { States = [new MediaState("chrome.exe", "Heartless", "Kanye West", null, "music", "playing")] };
        var allowed = false;
        using var collector = new MediaCollector(source, () => allowed, clock: () => T0);

        await collector.StartForTestAsync();
        Assert.Equal(0, collector.Timeline.Count);

        allowed = true;
        await collector.RefreshAsync();
        Assert.Equal(1, collector.Timeline.Count);

        allowed = false;
        await collector.RefreshAsync();
        Assert.Empty(collector.Timeline.Overlapping(T0.AddMinutes(1), T0.AddMinutes(2), T0.AddMinutes(2)));
    }

    [Fact]
    public async Task A_change_event_updates_the_timeline()
    {
        var source = new FakeMediaSource();
        var now = T0;
        using var collector = new MediaCollector(source, () => true, clock: () => now);
        await collector.StartForTestAsync();

        source.States = [new MediaState("chrome.exe", "Runaway", "Kanye West", null, "music", "playing")];
        now = T0.AddSeconds(5);
        await collector.RefreshAsync();

        Assert.Equal(1, collector.Timeline.Count);
    }
}

public class MediaThroughTheLinkTests
{
    private const int Port = 49998;
    private static readonly AgentLinkConfig Config = LinkTestData.Config(Port, LinkTestData.ChromeId);
    private static readonly FixedStatus Status = new(new AgentStatus(true, true, 1));

    private static StatusAnswer Post(string body, IEventSink sink, Func<string, IReadOnlyList<string>>? enrich, Action<string>? forget = null)
    {
        var raw = LinkTestData.Request(Port, method: "POST", path: "/events", origin: "ORIGIN", extra: $"Content-Length: {Encoding.UTF8.GetByteCount(body)}\r\n");
        var header = raw[..raw.IndexOf("\r\n\r\n", StringComparison.Ordinal)];
        return LocalStatusLink.Evaluate(header, Config, Status, body, sink, null, forget, enrich);
    }

    private const string Body = """{"schema_version":"1","id":"evt-9","start":"2026-10-30T10:00:00Z","end":"2026-10-30T10:01:00Z","duration_seconds":60,"source":"extension","app":null,"domain":"youtube.com","url":"https://www.youtube.com/watch","title":"Heartless - YouTube","presence":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}""";

    [Fact]
    public void Split_lines_are_written_together_and_reported_ok()
    {
        var sink = new FakeEventSink();
        var answer = Post(Body, sink, b => [b, b.Replace("evt-9", "evt-9-m1")]);
        Assert.Equal(200, answer.Status);
        Assert.Equal(2, sink.Events.Count);
    }

    [Fact]
    public void A_failed_write_of_split_lines_returns_500_and_forgets_the_original_id()
    {
        string? forgotten = null;
        var answer = Post(Body, new FailingEventSink(), b => [b, b.Replace("evt-9", "evt-9-m1")], id => forgotten = id);
        Assert.Equal(500, answer.Status);
        Assert.Equal("evt-9", forgotten);
    }

    [Fact]
    public void The_500_answer_is_sent_as_a_real_500()
    {
        var response = typeof(LocalStatusLink).GetMethod("BuildResponse", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var bytes = (byte[])response.Invoke(null, [new StatusAnswer(500, "{\"error\":\"write_failed\"}", null)])!;
        Assert.StartsWith("HTTP/1.1 500", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void Real_file_sink_keeps_non_ascii_text_readable_and_writes_all_lines_at_once()
    {
        var path = Path.Combine(Path.GetTempPath(), $"dla-media-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sink = new JsonlEventSink(path);
            Assert.True(sink.AppendEvents(["{\"media_title\":\"Beyoncé\"}", "{\"media_title\":\"b\"}"]));
            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Length);
            Assert.Contains("Beyoncé", lines[0]);
        }
        finally { File.Delete(path); }
    }
}
