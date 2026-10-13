using System.Text.Json;

namespace E4.Dddice;

/// <summary>
/// Checks what arrives over the JavaScript bridge before anything acts on it. 1.0.0 parses every message with
/// <c>JsonDocument.Parse</c> inside the WebView2 event, so a malformed one would throw there; here a message must be a JSON
/// object under a size limit with one of the kinds the 1.0.0 page sends, or it is dropped and counted.
/// NativeWebView does not say which frame or origin a message came from; navigation is locked to the page's own origin and
/// the page creates no frames, which is what makes the bridge trustworthy (see REPORT §11).
/// </summary>
public static class PageMessages
{
    public const int MaxLength = 64 * 1024;

    /// <summary>Every kind the 1.0.0 page posts.</summary>
    public static readonly IReadOnlySet<string> Kinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "pageLoaded", "ready", "sdkLoadFailed", "initFailed", "connectionState",
        "roll:started", "roll:finished", "rollError", "warmed", "warmSkipped",
    };

    public static bool TryValidate(string? body, out string kind, out string reason)
    {
        kind = "";
        if (body is null) { reason = "empty"; return false; }
        if (body.Length > MaxLength) { reason = $"too long ({body.Length} chars)"; return false; }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { reason = "not a JSON object"; return false; }
            if (!doc.RootElement.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String) { reason = "no kind"; return false; }
            kind = k.GetString()!;
            if (!Kinds.Contains(kind)) { reason = $"unknown kind '{Truncate(kind)}'"; return false; }
            if (kind is "roll:started" or "roll:finished" && !RollIsWellFormed(doc.RootElement)) { reason = "malformed roll"; return false; }
            reason = "";
            return true;
        }
        catch (JsonException) { reason = "not JSON"; return false; }
    }

    // The tracker reads roll.values[].type/value; a roll that lacks them would throw inside it.
    private static bool RollIsWellFormed(JsonElement root)
    {
        if (!root.TryGetProperty("roll", out var roll)) return false;
        if (roll.ValueKind == JsonValueKind.Null) return true; // the page posts roll: null for events without a roll
        if (roll.ValueKind != JsonValueKind.Object || !roll.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) return false;
        foreach (var v in values.EnumerateArray())
            if (v.ValueKind != JsonValueKind.Object || !v.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String
                || !v.TryGetProperty("value", out var n) || n.ValueKind != JsonValueKind.Number || !n.TryGetInt32(out _))
                return false;
        return true;
    }

    private static string Truncate(string s) => s.Length <= 40 ? s : s[..40] + "…";
}

/// <summary>
/// The diagnostic log. Every line goes through redaction of the secrets registered with it (the guest token), so even a
/// mistake elsewhere cannot write one to the screen, a file or a screenshot. Lines are also kept for the results files.
/// </summary>
public sealed class DiagnosticLog
{
    private readonly List<string> _secrets = [];
    private readonly List<string> _lines = [];
    private readonly object _gate = new();
    private readonly DateTime _start = DateTime.UtcNow;

    public event Action<string>? LineAdded;

    public void RegisterSecret(string secret)
    {
        if (string.IsNullOrEmpty(secret)) return;
        lock (_gate) if (!_secrets.Contains(secret)) _secrets.Add(secret);
    }

    public int RedactionCount { get; private set; }

    public void Write(string message)
    {
        string line;
        lock (_gate)
        {
            foreach (var s in _secrets)
                if (message.Contains(s, StringComparison.Ordinal)) { message = message.Replace(s, "«redacted»", StringComparison.Ordinal); RedactionCount++; }
            line = $"{(DateTime.UtcNow - _start).TotalSeconds,8:0.000}  {message}";
            _lines.Add(line);
        }
        LineAdded?.Invoke(line);
    }

    public IReadOnlyList<string> Lines { get { lock (_gate) return [.. _lines]; } }

    /// <summary>True when any registered secret appears in <paramref name="text"/> (for checking files and pages; never prints it).</summary>
    public bool ContainsSecret(string text) { lock (_gate) return _secrets.Any(s => text.Contains(s, StringComparison.Ordinal)); }
}
