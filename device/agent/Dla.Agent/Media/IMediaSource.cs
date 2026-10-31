namespace Dla.Agent.Media;

/// <summary>Where the list of playing media comes from. The real one is Windows; tests use a fake.</summary>
public interface IMediaSource : IDisposable
{
    /// <summary>Raised when something may have changed (new song, play/pause, a session came or went).</summary>
    event Action? Changed;

    Task<bool> InitializeAsync();

    /// <summary>Every session that is playing or paused right now.</summary>
    Task<IReadOnlyList<MediaState>> SnapshotAsync();
}
