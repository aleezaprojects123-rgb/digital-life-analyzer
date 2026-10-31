using System.Runtime.CompilerServices;
using Windows.Media;
using Windows.Media.Control;

namespace Dla.Agent.Media;

/// <summary>
/// Reads the Windows System Media Transport Controls: the list the volume flyout and lock screen show.
/// Chrome, Edge, Spotify, VLC and others publish title, artist, album and play state there.
/// Read only: it does not control playback, read thumbnails or look inside any app.
/// </summary>
public sealed class SmtcMediaSource : IMediaSource
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private readonly ConditionalWeakTable<object, object> _hooked = new();

    public event Action? Changed;

    public async Task<bool> InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (_, _) => { Hook(); Changed?.Invoke(); };
            _manager.CurrentSessionChanged += (_, _) => Changed?.Invoke();
            Hook();
            return true;
        }
        catch (Exception)
        {
            return false;   // no media controls on this PC: media metadata is simply absent
        }
    }

    private void Hook()
    {
        try
        {
            foreach (var session in _manager!.GetSessions())
            {
                if (_hooked.TryGetValue(session, out _)) continue;
                _hooked.Add(session, session);
                session.MediaPropertiesChanged += (_, _) => Changed?.Invoke();
                session.PlaybackInfoChanged += (_, _) => Changed?.Invoke();
            }
        }
        catch (Exception)
        {
            // a session vanished while we looked: the next change hooks the rest
        }
    }

    public async Task<IReadOnlyList<MediaState>> SnapshotAsync()
    {
        var list = new List<MediaState>();
        if (_manager is null) return list;

        IReadOnlyList<GlobalSystemMediaTransportControlsSession> sessions;
        try { sessions = _manager.GetSessions(); }
        catch (Exception) { return list; }

        foreach (var session in sessions)
        {
            try
            {
                var playback = session.GetPlaybackInfo().PlaybackStatus switch
                {
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => "playing",
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => "paused",
                    _ => null,
                };
                if (playback is null) continue;

                var props = await session.TryGetMediaPropertiesAsync();
                var kind = props.PlaybackType switch
                {
                    MediaPlaybackType.Music => "music",
                    MediaPlaybackType.Video => "video",
                    _ => null,
                };
                list.Add(new MediaState(session.SourceAppUserModelId, props.Title, props.Artist, props.AlbumTitle, kind, playback));
            }
            catch (Exception)
            {
                // this one session could not be read; the others still count
            }
        }
        return list;
    }

    public void Dispose() { }
}
