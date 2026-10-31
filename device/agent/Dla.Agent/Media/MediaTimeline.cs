namespace Dla.Agent.Media;

/// <summary>
/// A short memory of what media was playing and when, so a browser period that has just ended can be matched with it.
/// It lives in memory only (never written anywhere by itself) and forgets anything older than a day.
/// </summary>
public sealed class MediaTimeline
{
    public sealed record Segment(DateTimeOffset Start, DateTimeOffset? End, MediaState State);

    private static readonly TimeSpan Keep = TimeSpan.FromHours(24);
    private const int MaxSegments = 5000;

    private readonly object _gate = new();
    private List<Segment> _segments = [];

    /// <summary>The full list of media at <paramref name="now"/>. A state no longer listed ends; a new one starts.</summary>
    public void Observe(DateTimeOffset now, IReadOnlyList<MediaState> states)
    {
        var current = states.Where(s => s.HasContent).Select(s => s.Normalized()).Distinct().ToList();
        lock (_gate)
        {
            var open = new List<MediaState>();
            for (var i = 0; i < _segments.Count; i++)
            {
                var seg = _segments[i];
                if (seg.End is not null) continue;
                if (current.Contains(seg.State)) open.Add(seg.State);
                else _segments[i] = seg with { End = now };
            }
            foreach (var state in current)
                if (!open.Contains(state)) _segments.Add(new Segment(now, null, state));

            _segments.RemoveAll(s => s.End is { } end && now - end > Keep);
            if (_segments.Count > MaxSegments) _segments = _segments.Skip(_segments.Count - MaxSegments).ToList();
        }
    }

    /// <summary>Segments overlapping [from, to]; an open segment counts as running until <paramref name="now"/>.</summary>
    public IReadOnlyList<Segment> Overlapping(DateTimeOffset from, DateTimeOffset to, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _segments
                .Where(s => s.Start < to && (s.End ?? now) > from)
                .Select(s => s with { End = s.End ?? now })
                .ToList();
        }
    }

    public int Count { get { lock (_gate) return _segments.Count; } }
}
