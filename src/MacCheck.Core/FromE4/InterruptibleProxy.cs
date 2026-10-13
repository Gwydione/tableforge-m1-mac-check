using System.Net;
using System.Net.Sockets;
using System.Text;

namespace E4.Dddice;

/// <summary>
/// TEST ONLY. A loopback HTTP CONNECT proxy for the embedded browser's own traffic (WebView2 <c>--proxy-server</c>), so a
/// network outage can be simulated for that one browser without touching the machine's network: <see cref="SetDown"/>
/// closes every open tunnel (the realtime WebSocket drops at once) and refuses new ones until it is set up again.
/// It relays bytes only; TLS stays end-to-end between the browser and dddice, so the proxy never sees a token.
/// </summary>
public sealed class InterruptibleProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly List<TcpClient> _open = [];
    private readonly object _gate = new();
    private readonly Action<string>? _log;
    private volatile bool _down;

    public InterruptibleProxy(Action<string>? log = null)
    {
        _log = log;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop();
    }

    public int Port { get; }
    public bool IsDown => _down;
    public int TunnelsOpened { get; private set; }
    public int Refused { get; private set; }

    public void SetDown(bool down)
    {
        _down = down;
        if (!down) { _log?.Invoke("proxy: network UP"); return; }
        List<TcpClient> victims;
        lock (_gate) { victims = [.. _open]; _open.Clear(); }
        foreach (var c in victims) { try { c.Client.LingerState = new LingerOption(true, 0); c.Close(); } catch { } }
        _log?.Invoke($"proxy: network DOWN (closed {victims.Count} tunnels)");
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Tunnel(client);
        }
    }

    private async Task Tunnel(TcpClient client)
    {
        TcpClient? upstream = null;
        try
        {
            var stream = client.GetStream();
            var header = await ReadHeader(stream);
            var first = header.Split("\r\n")[0].Split(' ');
            if (_down || first.Length < 2 || first[0] != "CONNECT")
            {
                if (_down) Refused++;
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"));
                client.Close();
                return;
            }
            var hostPort = first[1].Split(':');
            upstream = new TcpClient();
            await upstream.ConnectAsync(hostPort[0], int.Parse(hostPort[1]), _stop.Token);
            lock (_gate) { _open.Add(client); _open.Add(upstream); }
            TunnelsOpened++;
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 Connection Established\r\n\r\n"));
            var up = upstream.GetStream();
            await Task.WhenAny(stream.CopyToAsync(up, _stop.Token), up.CopyToAsync(stream, _stop.Token));
        }
        catch (Exception) { /* closed by SetDown or by either side */ }
        finally
        {
            lock (_gate) { _open.Remove(client); if (upstream is not null) _open.Remove(upstream); }
            client.Close();
            upstream?.Close();
        }
    }

    private static async Task<string> ReadHeader(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (buffer.Count < 8192 && await stream.ReadAsync(one) == 1)
        {
            buffer.Add(one[0]);
            var n = buffer.Count;
            if (n >= 4 && buffer[n - 4] == '\r' && buffer[n - 3] == '\n' && buffer[n - 2] == '\r' && buffer[n - 1] == '\n') break;
        }
        return Encoding.ASCII.GetString([.. buffer]);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        SetDown(true);
    }
}
