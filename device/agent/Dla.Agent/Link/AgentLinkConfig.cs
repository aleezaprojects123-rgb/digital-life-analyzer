using System.Text.Json;
using System.Text.RegularExpressions;

namespace Dla.Agent.Link;

/// <summary>
/// The one place for the link's settings: the port and the list of extensions allowed to ask for the status.
///
/// The port is a constant because the extension's manifest will hard-code the same address.
/// The allowlist is read from %LocalAppData%\DigitalLifeAnalyzer\agent-link.json:
///
///   { "allowedExtensionIds": [ "abcdefghijklmnopabcdefghijklmnop" ] }
///
/// DEFAULT IS EMPTY, so nothing is allowed until someone adds an extension ID. A missing, unreadable or corrupt
/// file, and any entry that does not look like a real extension ID, means "not allowed". The file is never
/// created by the agent.
/// </summary>
public sealed partial class AgentLinkConfig
{
    public const int DefaultPort = 49721;
    public const string FileName = "agent-link.json";
    private const int MaxFileBytes = 16 * 1024;

    private const string ChromeScheme = "chrome-extension://";   // Chrome and Edge
    private const string FirefoxScheme = "moz-extension://";     // Firefox-style

    [GeneratedRegex("^[a-p]{32}$")]
    private static partial Regex ChromeIdShape();

    [GeneratedRegex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")]
    private static partial Regex FirefoxIdShape();

    public AgentLinkConfig(int port, IEnumerable<string> allowedExtensionIds)
    {
        Port = port;
        AllowedExtensionIds = allowedExtensionIds.Where(IsPlausibleId).ToHashSet(StringComparer.Ordinal);
    }

    public int Port { get; }

    /// <summary>The allowed extension IDs, all lowercase and well formed.</summary>
    public IReadOnlySet<string> AllowedExtensionIds { get; }

    public static string FilePath(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>The default port, and the allowlist from the file (empty if there is no usable file).</summary>
    public static AgentLinkConfig Load(string dataDir) => new(DefaultPort, ReadIds(FilePath(dataDir)));

    private static bool IsPlausibleId(string id) => ChromeIdShape().IsMatch(id) || FirefoxIdShape().IsMatch(id);

    private static string[] ReadIds(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0 || info.Length > MaxFileBytes) return [];

            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return [];
            if (!doc.RootElement.TryGetProperty("allowedExtensionIds", out var list) || list.ValueKind != JsonValueKind.Array) return [];

            return list.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!.Trim().ToLowerInvariant())
                .Where(IsPlausibleId)
                .Distinct()
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            return [];   // unreadable or corrupt: nothing is allowed
        }
    }

    /// <summary>
    /// True only if the Origin header is exactly "chrome-extension://&lt;id&gt;" (or "moz-extension://&lt;uuid&gt;")
    /// and that id is in the allowlist. Web pages (https://..., http://localhost..., "null"), a missing header,
    /// a trailing slash, upper case, or any other shape are all refused.
    /// </summary>
    /// <param name="origin">The same text back, to be used in the CORS header. Empty when not allowed.</param>
    public bool TryGetAllowedOrigin(string? originHeader, out string origin)
    {
        origin = "";
        if (string.IsNullOrEmpty(originHeader) || originHeader.Length > 128) return false;

        string id;
        if (originHeader.StartsWith(ChromeScheme, StringComparison.Ordinal))
        {
            id = originHeader[ChromeScheme.Length..];
            if (!ChromeIdShape().IsMatch(id)) return false;
        }
        else if (originHeader.StartsWith(FirefoxScheme, StringComparison.Ordinal))
        {
            id = originHeader[FirefoxScheme.Length..];
            if (!FirefoxIdShape().IsMatch(id)) return false;
        }
        else
        {
            return false;
        }

        if (!AllowedExtensionIds.Contains(id)) return false;
        origin = originHeader;
        return true;
    }
}
