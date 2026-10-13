using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using E4.Dddice;

namespace MacCheck.Core;

/// <summary>
/// Optional E1: TableForge 1.0.0's dice page in Safari itself. The page is the 1.0.0 page with its one bridge line swapped
/// (exactly as E4 does), posting to this loopback server instead of a WebView, plus a small auto-run script: it creates
/// its own temporary dddice guest and private room in the browser (the same two REST calls TableForge makes), rolls
/// d20, 2d6 and d100, and reports the settled faces. No token ever passes from the app to Safari or into a URL.
/// The server accepts only POSTs of known event kinds, at most 16 KB, on a random path; everything else is 404.
/// </summary>
public sealed class E1SafariServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _page;
    private readonly string _nonce = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12));
    public event Action<JsonElement>? EventReceived;
    public int Rejected { get; private set; }

    public static readonly HashSet<string> Kinds = ["e1:start", "e1:guest", "e1:ready", "e1:roll", "e1:done", "e1:error", "roll:started", "roll:finished", "rollError", "initFailed", "sdkLoadFailed", "connectionState", "warmed", "warmSkipped", "pageLoaded", "ready"];

    public E1SafariServer()
    {
        _page = Encoding.UTF8.GetBytes(BuildPage());
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Accept();
    }

    public int Port { get; }
    public Uri PageUri => new($"http://127.0.0.1:{Port}/{_nonce}/e1.html");

    public static string BuildPage()
    {
        var page = HostPage.Original();
        const string bridge = "window.chrome.webview.postMessage(m)";
        if (page.Split(bridge).Length != 2) throw new InvalidOperationException("1.0.0 page bridge not found exactly once");
        page = page.Replace(bridge, "window.__e1post(m)", StringComparison.Ordinal);
        const string runner = """
<script>
  // Mac Check E1 auto-run (not part of the 1.0.0 page). Reports to the app that opened this tab, nothing else.
  window.__e1post = m => { try { fetch('event', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(m) }); } catch (e) { } };
  const say = t => { const el = document.getElementById('e1status'); if (el) el.textContent = t; };
  (async () => {
    const api = 'https://dddice.com/api/1.0/';
    try {
      window.__e1post({ kind: 'e1:start', ua: navigator.userAgent, secure: window.isSecureContext, webgl2: !!document.createElement('canvas').getContext('webgl2') });
      say('Connecting to dddice…');
      const post = async (path, body, token) => { const r = await fetch(api + path, { method: 'POST', headers: Object.assign({ 'Content-Type': 'application/json', Accept: 'application/json' }, token ? { Authorization: 'Bearer ' + token } : {}), body: JSON.stringify(body) }); if (!r.ok) throw new Error(path + ' HTTP ' + r.status); return r.json(); };
      const token = (await post('user', {})).data;
      const room = (await post('room', { name: 'TableForge Mac Check', is_public: false }, token)).data.slug;
      window.__e1post({ kind: 'e1:guest' });
      const ready = new Promise(res => { const orig = window.__e1post; window.__e1post = m => { orig(m); if (m && m.kind === 'ready') res(); }; });
      window.tf.init(token, room, 'dddice-bees');
      await Promise.race([ready, new Promise((_, rej) => setTimeout(() => rej(new Error('not ready in 40 s')), 40000))]);
      window.__e1post({ kind: 'e1:ready' });
      const sets = [[{ type: 'd20' }], [{ type: 'd6' }, { type: 'd6' }], [{ type: 'd10x' }, { type: 'd10' }]];
      for (let i = 0; i < sets.length; i++) {
        say('Rolling ' + (i + 1) + ' of ' + sets.length + '…');
        const id = 'e1-' + i + '-' + Math.random().toString(16).slice(2);
        const t0 = performance.now();
        const finished = new Promise((res, rej) => { const orig = window.__e1post; window.__e1post = m => { orig(m); if (m && m.kind === 'roll:finished' && m.roll && m.roll.externalId === id) res(m.roll); if (m && m.kind === 'rollError') rej(new Error(m.message)); }; setTimeout(() => rej(new Error('roll timeout')), 30000); });
        window.tf.clear();
        window.tf.roll(sets[i].map(d => Object.assign({ theme: 'dddice-bees' }, d)), id);
        const roll = await finished;
        window.__e1post({ kind: 'e1:roll', index: i, ms: Math.round(performance.now() - t0), values: roll.values, total: roll.total });
        await new Promise(r => setTimeout(r, 1200));
      }
      window.__e1post({ kind: 'e1:done' });
      say('Done — you can close this tab and go back to TableForge Mac Check.');
    } catch (e) { window.__e1post({ kind: 'e1:error', message: String((e && e.message) || e) }); say('Something went wrong — that is useful too. Close this tab and go back to Mac Check.'); }
  })();
</script>
<div id="e1status" style="position:fixed;left:12px;top:10px;font:16px -apple-system,sans-serif;color:#333;background:#fffd;padding:6px 10px;border-radius:6px">Starting…</div>
""";
        return page.Replace("</body>", runner + "</body>", StringComparison.Ordinal);
    }

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_stop.Token); } catch (Exception) { return; }
            _ = Serve(c);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var (method, path, body) = await ReadRequest(stream);
                byte[] response;
                string type = "text/plain";
                var status = "404 Not Found";
                if (method == "GET" && path == $"/{_nonce}/e1.html") { response = _page; type = "text/html; charset=utf-8"; status = "200 OK"; }
                else if (method == "POST" && path == $"/{_nonce}/event" && body is not null && TryEvent(body, out var ev))
                {
                    EventReceived?.Invoke(ev);
                    response = "ok"u8.ToArray();
                    status = "200 OK";
                }
                else if (method == "POST" && body is null) { Rejected++; response = "too large"u8.ToArray(); status = "413 Payload Too Large"; }
                else { if (method == "POST") Rejected++; response = "not found"u8.ToArray(); }
                var head = $"HTTP/1.1 {status}\r\nContent-Type: {type}\r\nContent-Length: {response.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(response);
            }
            catch (Exception) { }
        }
    }

    private static bool TryEvent(string body, out JsonElement ev)
    {
        ev = default;
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("kind", out var k) || k.ValueKind != JsonValueKind.String || !Kinds.Contains(k.GetString()!)) return false;
            ev = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static async Task<(string Method, string Path, string? Body)> ReadRequest(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int headerEnd = -1;
        while (headerEnd < 0 && buffer.Length < 8192)
        {
            var n = await stream.ReadAsync(chunk);
            if (n == 0) break;
            buffer.Write(chunk, 0, n);
            headerEnd = IndexOf(buffer.GetBuffer(), (int)buffer.Length, "\r\n\r\n"u8);
        }
        if (headerEnd < 0) return ("", "", null);
        var all = buffer.GetBuffer();
        var head = Encoding.ASCII.GetString(all, 0, headerEnd);
        var first = head.Split("\r\n")[0].Split(' ');
        var length = 0;
        foreach (var line in head.Split("\r\n").Skip(1))
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) int.TryParse(line[15..].Trim(), out length);
        if (length > 16 * 1024) return (first[0], first.Length > 1 ? first[1] : "", null);
        var have = (int)buffer.Length - headerEnd - 4;
        var body = new byte[length];
        Array.Copy(all, headerEnd + 4, body, 0, Math.Min(have, length));
        while (have < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(have, length - have));
            if (n == 0) break;
            have += n;
        }
        return (first[0], first.Length > 1 ? first[1] : "", Encoding.UTF8.GetString(body, 0, Math.Min(have, length)));
    }

    private static int IndexOf(byte[] data, int length, ReadOnlySpan<byte> pattern)
    {
        for (var i = 0; i + pattern.Length <= length; i++)
            if (data.AsSpan(i, pattern.Length).SequenceEqual(pattern)) return i;
        return -1;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
