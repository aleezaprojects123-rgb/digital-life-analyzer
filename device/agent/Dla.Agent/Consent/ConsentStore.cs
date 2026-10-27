using System.Globalization;
using System.Text.Json;

namespace Dla.Agent.Consent;

/// <summary>
/// Reads and writes the saved consent: %LocalAppData%\DigitalLifeAnalyzer\consent.json
///
///   { "consentVersion": 1, "acceptedAtUtc": "2026-10-28T05:15:00Z" }
///
/// The file is created ONLY by <see cref="SaveAcceptance"/>, which runs only after the user pressed Accept.
/// Anything unexpected counts as NOT consented: a missing file, an unreadable or corrupt file, a wrong field
/// type, a missing field, or any version other than the current one (older OR newer).
/// </summary>
public sealed class ConsentStore
{
    public const string FolderName = "DigitalLifeAnalyzer";
    public const string FileName = "consent.json";
    private const int MaxFileBytes = 4096;   // a real consent file is about 70 bytes; anything huge is not one

    public ConsentStore(string dataDir) => DataDir = dataDir;

    public string DataDir { get; }
    public string FilePath => Path.Combine(DataDir, FileName);

    /// <summary>%LocalAppData%\DigitalLifeAnalyzer (honors the LOCALAPPDATA environment variable).</summary>
    public static string DefaultDataDir()
    {
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (string.IsNullOrWhiteSpace(localAppData))
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, FolderName);
    }

    /// <summary>True only if a well-formed consent file for the current text version exists.</summary>
    public bool HasValidConsent() => TryRead(out var version, out _) && version == ConsentText.Version;

    /// <summary>The time the user accepted, or null if there is no valid consent.</summary>
    public DateTimeOffset? AcceptedAtUtc() =>
        TryRead(out var version, out var at) && version == ConsentText.Version ? at : null;

    /// <summary>Saves that the user accepted the current text. Call only after an explicit Accept.</summary>
    public void SaveAcceptance(DateTimeOffset nowUtc)
    {
        Directory.CreateDirectory(DataDir);

        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            json.WriteStartObject();
            json.WriteNumber("consentVersion", ConsentText.Version);
            json.WriteString("acceptedAtUtc", nowUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
            json.WriteEndObject();
        }

        // write to a temporary file and rename it, so a crash can never leave a half-written consent file
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, buffer.ToArray());
        File.Move(temp, FilePath, overwrite: true);
    }

    private bool TryRead(out int version, out DateTimeOffset acceptedAt)
    {
        version = 0;
        acceptedAt = default;
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) return false;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;

            if (!root.TryGetProperty("consentVersion", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out version))
                return false;

            if (!root.TryGetProperty("acceptedAtUtc", out var t) || t.ValueKind != JsonValueKind.String) return false;
            var text = t.GetString();
            if (text is null || !text.EndsWith('Z')) return false;   // must be UTC
            if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out acceptedAt))
                return false;

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return false;   // unreadable or corrupt: not consented
        }
    }
}
