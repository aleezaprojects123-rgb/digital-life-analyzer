using Microsoft.Win32;

namespace Dla.Agent.Supervision;

/// <summary>
/// Backoff for the crash watchdog: 2s, 5s, 30s, 30s. The 5th crash within 2 minutes starts cooldown mode:
/// retry every 5 minutes, indefinitely, until a run stays up for 2 minutes.
/// </summary>
public sealed class RestartPolicy
{
    public static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30)];
    public static readonly TimeSpan RapidWindow = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(5);
    public const int RapidLimit = 5;

    private readonly Func<DateTimeOffset> _now;
    private readonly List<DateTimeOffset> _crashes = [];
    private DateTimeOffset? _startedAt;

    public RestartPolicy(Func<DateTimeOffset>? now = null) => _now = now ?? (() => DateTimeOffset.Now);

    public bool InCooldown { get; private set; }

    public void OnStarted() => _startedAt = _now();

    /// <returns>How long to wait before launching the agent again.</returns>
    public TimeSpan OnCrash()
    {
        var now = _now();
        if (_startedAt is { } started && now - started >= RapidWindow)
        {
            _crashes.Clear();   // the last run was stable; this is a fresh failure
            InCooldown = false;
        }
        _crashes.RemoveAll(t => now - t > RapidWindow);
        _crashes.Add(now);

        if (InCooldown) return Cooldown;
        if (_crashes.Count >= RapidLimit)
        {
            InCooldown = true;
            return Cooldown;
        }
        return Backoff[Math.Min(_crashes.Count - 1, Backoff.Length - 1)];
    }
}

/// <summary>
/// Per-user auto-start through HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// It needs no admin rights, is applied at every login of this Windows user only, and is the
/// standard location that Windows Settings > Apps > Startup lists, so users can see and disable it.
/// </summary>
public sealed class AutoStart
{
    public const string DefaultSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "DigitalLifeAnalyzer";

    private readonly string _subKey;
    private readonly string _valueName;

    public AutoStart(string subKey = DefaultSubKey, string valueName = DefaultValueName)
    {
        _subKey = subKey;
        _valueName = valueName;
    }

    public void Enable(string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey);
        key.SetValue(_valueName, $"\"{exePath}\"", RegistryValueKind.String);
    }

    public void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }

    public string? GetCommand()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey);
        return key?.GetValue(_valueName) as string;
    }

    public bool IsEnabled => GetCommand() is not null;
}
