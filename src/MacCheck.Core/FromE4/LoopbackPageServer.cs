using System.Net;
using System.Net.Sockets;
using System.Text;

namespace E4.Dddice;

/// <summary>
/// Serves the one dice page on <c>http://127.0.0.1:&lt;random port&gt;/&lt;random path&gt;/dddice-host.html</c> — and nothing
/// else. 1.0.0 maps a WebView2 virtual host (<c>https://tableforge-app.local</c>) onto the install folder, which is a
/// WebView2-only API; a loopback page works with every engine NativeWebView uses, and loopback counts as a secure context in
/// Chromium and WebKit (E1). The page holds no secret: the token reaches it only as an argument of <c>window.tf.init</c>.
/// Any process on the machine could fetch the page, so the path carries a random component and everything else is a 404.
/// </summary>
public sealed class LoopbackPageServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly byte[] _page;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action<string>? _log;

    public LoopbackPageServer(string page, Action<string>? log = null)
    {
        _page = Encoding.UTF8.GetBytes(page);
        _log = log;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        PagePath = $"/{Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8))}/dddice-host.html";
        _ = AcceptLoop();
    }

    public int Port { get; }
    public string PagePath { get; }
    public string Origin => $"http://127.0.0.1:{Port}";
    public Uri PageUri => new(Origin + PagePath);
    public int Served { get; private set; }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Serve(client);
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var requestLine = await reader.ReadLineAsync(_stop.Token) ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(_stop.Token))) { } // headers: not needed

                var parts = requestLine.Split(' ');
                var ok = parts.Length == 3 && parts[0] == "GET" && parts[1] == PagePath;
                var body = ok ? _page : Encoding.ASCII.GetBytes("not found");
                var head = $"HTTP/1.1 {(ok ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(ok ? "text/html; charset=utf-8" : "text/plain")}\r\n" +
                           $"Content-Length: {body.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head), _stop.Token);
                await stream.WriteAsync(body, _stop.Token);
                if (ok) Served++;
                _log?.Invoke($"page server: {(ok ? "200" : "404")} {(parts.Length > 1 ? (ok ? "<dice page>" : parts[1].Split('?')[0]) : "?")}");
            }
            catch (Exception) { /* a client that goes away is not our problem */ }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
