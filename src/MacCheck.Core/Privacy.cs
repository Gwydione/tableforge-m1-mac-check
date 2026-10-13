using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace MacCheck.Core;

/// <summary>
/// Removes identifying text from anything Mac Check writes: the home folder, the user name and the computer name, plus
/// any /Users/&lt;name&gt; or C:\Users\&lt;name&gt; path and anything registered as a secret (the dddice guest token).
/// </summary>
public sealed class Redactor
{
    private readonly List<(string Value, string Replacement)> _exact = [];

    public Redactor()
    {
        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~");
        Add(Environment.UserName, "<user>");
        Add(Environment.MachineName, "<computer>");
    }

    public void Add(string? value, string replacement)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 3) return;
        lock (_exact)
        {
            if (_exact.Any(e => e.Value == value)) return;
            _exact.Add((value, replacement));
            _exact.Sort((a, b) => b.Value.Length.CompareTo(a.Value.Length)); // longest first: the home path before the user name
        }
    }

    public void AddSecret(string? secret) => Add(secret, "«redacted»");

    public string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        lock (_exact)
            foreach (var (value, replacement) in _exact)
                text = text.Replace(value, replacement, StringComparison.OrdinalIgnoreCase);
        text = Regex.Replace(text, @"/Users/[^/\s""']+", "/Users/<user>");
        text = Regex.Replace(text, @"(?i)[A-Z]:\\Users\\[^\\\s""']+", @"C:\Users\<user>");
        text = Regex.Replace(text, @"/home/[^/\s""']+", "/home/<user>");
        return text;
    }
}

/// <summary>The bounded diagnostic log: redacted, capped in size, kept in memory and written only into the results package.</summary>
public sealed class SessionLog(Redactor redactor)
{
    public const int MaxLines = 3000;
    private readonly List<string> _lines = [];
    private readonly DateTime _start = DateTime.UtcNow;
    public event Action<string>? LineAdded;

    public void Write(string message)
    {
        var line = $"{(DateTime.UtcNow - _start).TotalSeconds,8:0.0}  {redactor.Clean(message)}";
        lock (_lines)
        {
            if (_lines.Count >= MaxLines) return;
            _lines.Add(line);
            if (_lines.Count == MaxLines) _lines.Add("(log limit reached; later lines dropped)");
        }
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<string> Lines { get { lock (_lines) return [.. _lines]; } }
}

/// <summary>
/// System facts, read only after consent. Never the user name, computer name, serial number, hardware UUID, IP address
/// or home path. On macOS: product version via sysctl kern.osproductversion, the model identifier (for example
/// "Mac14,2", only with its own consent), and whether the app runs under Rosetta (sysctl.proc_translated).
/// </summary>
public static class SystemFacts
{
    public static Dictionary<string, string> Read(bool includeModel)
    {
        var facts = new Dictionary<string, string>
        {
            ["os"] = OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "other",
            ["osVersion"] = OperatingSystem.IsMacOS() ? Sysctl("kern.osproductversion") ?? Environment.OSVersion.Version.ToString() : Environment.OSVersion.Version.ToString(),
            ["processArchitecture"] = RuntimeInformation.ProcessArchitecture.ToString(),
            ["osArchitecture"] = RuntimeInformation.OSArchitecture.ToString(),
            [".net"] = Environment.Version.ToString(),
        };
        if (OperatingSystem.IsMacOS())
        {
            facts["osBuild"] = Sysctl("kern.osversion") ?? "?";
            var translated = SysctlInt("sysctl.proc_translated");
            facts["runningUnderRosetta"] = translated == 1 ? "yes" : translated == 0 ? "no" : "unknown";
            if (includeModel) facts["modelIdentifier"] = Sysctl("hw.model") ?? "?";
        }
        return facts;
    }

    [DllImport("libc", EntryPoint = "sysctlbyname")]
    private static extern int SysctlByName(string name, byte[]? oldp, ref nint oldlenp, IntPtr newp, nint newlen);

    private static string? Sysctl(string name)
    {
        try
        {
            nint len = 0;
            if (SysctlByName(name, null, ref len, IntPtr.Zero, 0) != 0 || len <= 0 || len > 256) return null;
            var buffer = new byte[len];
            if (SysctlByName(name, buffer, ref len, IntPtr.Zero, 0) != 0) return null;
            return Encoding.UTF8.GetString(buffer, 0, (int)len).TrimEnd('\0');
        }
        catch (Exception) { return null; }
    }

    private static int? SysctlInt(string name)
    {
        try
        {
            nint len = 4;
            var buffer = new byte[4];
            if (SysctlByName(name, buffer, ref len, IntPtr.Zero, 0) != 0) return null;
            return BitConverter.ToInt32(buffer, 0);
        }
        catch (Exception) { return null; }
    }
}
