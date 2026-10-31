using Microsoft.Win32;

namespace Dla.Agent.Startup;

/// <summary>The one place a Windows start-up entry is stored. Real one: the current user's Run key. Tests use a fake.</summary>
public interface IStartupEntry
{
    string? Read();
    void Write(string command);
    void Delete();
}

/// <summary>What AgentHost needs: make sure DLA starts when this user signs in to Windows, or make sure it does not.</summary>
public interface IStartupRegistration
{
    bool Ensure();
    void Remove();
}

/// <summary>
/// The ONE start-with-Windows mechanism: a single value in HKCU\Software\Microsoft\Windows\CurrentVersion\Run named
/// "DigitalLifeAnalyzer". It is per user (no administrator rights, no service, no scheduled task, no Startup-folder file), it is the
/// entry Windows itself lists under Settings > Apps > Startup where the user can switch it off, and removing the value is all it takes
/// to undo it. Windows starts the program after sign-in; the program's own single-instance guard then makes sure only one runs.
/// </summary>
public sealed class RunKeyEntry : IStartupEntry
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "DigitalLifeAnalyzer";

    private readonly string _subKey;
    private readonly string _valueName;

    /// <param name="subKey">Only tests change this (to a throwaway key), never the product.</param>
    public RunKeyEntry(string subKey = RunKeyPath, string valueName = DefaultValueName)
    {
        _subKey = subKey;
        _valueName = valueName;
    }

    public string? Read()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: false);
        return key?.GetValue(_valueName) as string;
    }

    public void Write(string command)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_subKey, writable: true);
        key.SetValue(_valueName, command, RegistryValueKind.String);
    }

    public void Delete()
    {
        using var key = Registry.CurrentUser.OpenSubKey(_subKey, writable: true);
        key?.DeleteValue(_valueName, throwOnMissingValue: false);
    }
}

/// <summary>
/// Registers or removes the start-up entry. It registers only a real, installed-style program file named Dla.Agent.exe (not the
/// "dotnet" host that runs a build), and only the exact quoted path of the running program, so the entry always starts the same
/// program that wrote it. Writing is skipped when the entry is already right, so it is safe to call at every start. It never throws.
/// </summary>
public sealed class StartupRegistration : IStartupRegistration
{
    public const string ProgramFileName = "Dla.Agent.exe";

    private readonly IStartupEntry _entry;
    private readonly Func<string?> _programPath;
    private readonly IAgentLog? _log;

    public StartupRegistration(IStartupEntry entry, Func<string?>? programPath = null, IAgentLog? log = null)
    {
        _entry = entry;
        _programPath = programPath ?? (() => Environment.ProcessPath);
        _log = log;
    }

    /// <summary>The exact value that starts this program: its full path in quotes. null when this is not the real program file.</summary>
    public string? Command()
    {
        var path = _programPath();
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (!string.Equals(Path.GetFileName(path), ProgramFileName, StringComparison.OrdinalIgnoreCase)) return null;
        return $"\"{Path.GetFullPath(path)}\"";
    }

    /// <returns>True when the entry is in place and correct afterwards.</returns>
    public bool Ensure()
    {
        try
        {
            var command = Command();
            if (command is null)
            {
                _log?.Write("Start with Windows was not set up: this is not the Dla.Agent.exe program file.");
                return false;
            }
            if (_entry.Read() == command) return true;   // already right: nothing is written
            _entry.Write(command);
            _log?.Write("Start with Windows is on (one entry for this user).");
            return true;
        }
        catch (Exception ex)
        {
            _log?.Write($"Start with Windows could not be set up ({ex.GetType().Name}).");
            return false;
        }
    }

    public void Remove()
    {
        try
        {
            if (_entry.Read() is null) return;
            _entry.Delete();
            _log?.Write("Start with Windows was turned off.");
        }
        catch (Exception ex)
        {
            _log?.Write($"Start with Windows could not be turned off ({ex.GetType().Name}).");
        }
    }
}
