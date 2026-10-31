using System.Reflection;
using System.Text.Json;
using Dla.Agent.Link;

namespace Dla.Agent.Tests;

public class SessionStampTests
{
    private const string Event = """{"schema_version":"1","id":"evt-1","start":"2026-10-31T00:10:32Z","end":"2026-10-31T00:10:44Z","duration_seconds":12,"source":"extension","platform":"instagram","domain":"instagram.com","url":"https://www.instagram.com/reel/*","title":null,"activity_type":"reel","content_count":1,"session_id":null,"category":null,"confidence":null,"uncertain":false,"label_source":null}""";

    private static JsonElement Parse(string line) => JsonDocument.Parse(line).RootElement.Clone();

    [Fact]
    public void The_session_id_is_put_on_the_event_and_nothing_else_changes()
    {
        var stamped = Parse(SessionStamp.Apply(Event, "abc123"));
        Assert.Equal("abc123", stamped.GetProperty("session_id").GetString());
        Assert.Equal(1, stamped.GetProperty("content_count").GetInt32());
        Assert.Equal(12, stamped.GetProperty("duration_seconds").GetInt32());
        Assert.Equal("evt-1", stamped.GetProperty("id").GetString());
    }

    [Fact]
    public void A_session_id_sent_by_the_browser_is_replaced_not_trusted()
    {
        var forged = Event.Replace("\"session_id\":null", "\"session_id\":\"forged\"");
        Assert.Equal("real", Parse(SessionStamp.Apply(forged, "real")).GetProperty("session_id").GetString());
    }

    [Fact]
    public void Something_that_is_not_a_json_object_is_returned_unchanged()
    {
        Assert.Equal("not json", SessionStamp.Apply("not json", "x"));
        Assert.Equal("[1,2]", SessionStamp.Apply("[1,2]", "x"));
    }

    private static string Accepted(LocalStatusLink link, string body) =>
        ((IReadOnlyList<string>)typeof(LocalStatusLink).GetMethod("AcceptedEvent", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(link, [body])!)[0];

    [Fact]
    public void One_link_keeps_one_session_for_all_its_events_and_another_run_gets_another()
    {
        var config = LinkTestData.Config(49997, LinkTestData.ChromeId);
        var status = new FixedStatus(new AgentStatus(true, true, 1));
        using var first = new LocalStatusLink(config, status, sink: new FakeEventSink());
        using var second = new LocalStatusLink(config, status, sink: new FakeEventSink());

        var a = Parse(Accepted(first, Event)).GetProperty("session_id").GetString();
        var b = Parse(Accepted(first, Event.Replace("evt-1", "evt-2"))).GetProperty("session_id").GetString();
        var c = Parse(Accepted(second, Event)).GetProperty("session_id").GetString();
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.InRange(a!.Length, 8, 64);
    }
}
