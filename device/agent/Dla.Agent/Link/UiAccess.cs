using System.Security.Cryptography;
using System.Text;

namespace Dla.Agent.Link;

/// <summary>
/// How the separate Digital Life Analyzer UI proves it may read reports from the local agent.
///
/// While the agent runs it holds one random secret (256 bits). It writes the secret to %LocalAppData%\DigitalLifeAnalyzer\ui-access.token,
/// which only the same Windows user can read, and removes the file when the link stops. The UI reads that file and sends the secret in the
/// X-DLA-UI-Token header. A new secret is made every time the agent starts, so an old copy is useless.
///
/// The secret opens the four read-only report GET routes and nothing else (see LocalStatusLink.Evaluate). It is never an extension id and
/// never counts for the extension routes, so it cannot post events or read /status. Web pages cannot use it: they do not know it, and a
/// request that also carries an Origin header is refused.
/// </summary>
public sealed class UiAccess
{
    public const string TokenHeader = "X-DLA-UI-Token";
    public const string FileName = "ui-access.token";

    private readonly byte[] _token;

    /// <summary>The secret as it is written to the file and sent in the header.</summary>
    public string Token { get; }

    public string FilePath { get; }

    public UiAccess(string token, string filePath)
    {
        Token = token;
        FilePath = filePath;
        _token = Encoding.ASCII.GetBytes(token);
    }

    public static string FilePathIn(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Makes a new secret and writes it for the UI. Returns null (and the UI gets no access) if the file cannot be written.</summary>
    public static UiAccess? Create(string dataDir, IAgentLog? log = null)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var path = FilePathIn(dataDir);
        try
        {
            Directory.CreateDirectory(dataDir);
            var temp = path + ".tmp";
            File.WriteAllText(temp, token, Encoding.ASCII);
            File.Move(temp, path, overwrite: true);
            return new UiAccess(token, path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log?.Write("The access file for the Digital Life Analyzer window could not be written; it cannot read reports until the agent restarts.");
            return null;
        }
    }

    /// <summary>True only for exactly the current secret. The comparison takes the same time wherever the first difference is.</summary>
    public bool Matches(string candidate)
    {
        if (candidate.Length != Token.Length) return false;
        return CryptographicOperations.FixedTimeEquals(_token, Encoding.ASCII.GetBytes(candidate));
    }

    /// <summary>Removes the file so a stopped agent leaves no secret behind. Best effort.</summary>
    public void Remove()
    {
        try { File.Delete(FilePath); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
