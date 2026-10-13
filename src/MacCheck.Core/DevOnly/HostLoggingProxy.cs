using System.Net;
using System.Net.Sockets;
using System.Text;

namespace MacCheck.Core.DevOnly;

/// <summary>
/// DEVELOPMENT ONLY (self-test with MACCHECK_LOG_HOSTS=1 on Windows): a loopback CONNECT proxy for the embedded browser
/// that records which host names it connects to, so a run can show there is no traffic beyond dddice. Relays bytes only;
/// TLS stays end-to-end. Never active in a tester run.
/// </summary>
public sealed class HostLoggingProxy : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly HashSet<string> _hosts = [];

    public HostLoggingProxy()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Accept();
    }

    public int Port { get; }
    public IReadOnlyCollection<string> Hosts { get { lock (_hosts) return [.. _hosts.Order()]; } }

    private async Task Accept()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient c;
            try { c = await _listener.AcceptTcpClientAsync(_stop.Token); } catch (Exception) { return; }
            _ = Tunnel(c);
        }
    }

    private async Task Tunnel(TcpClient client)
    {
        using (client)
        {
            try
            {
                var s = client.GetStream();
                var head = new StringBuilder();
                var one = new byte[1];
                while (head.Length < 8192 && await s.ReadAsync(one) == 1)
                {
                    head.Append((char)one[0]);
                    if (head.Length >= 4 && head.ToString(head.Length - 4, 4) == "\r\n\r\n") break;
                }
                var first = head.ToString().Split("\r\n")[0].Split(' ');
                if (first.Length < 2 || first[0] != "CONNECT") { await s.WriteAsync("HTTP/1.1 405 Method Not Allowed\r\n\r\n"u8.ToArray()); return; }
                var hp = first[1].Split(':');
                lock (_hosts) _hosts.Add(first[1]);
                using var up = new TcpClient();
                await up.ConnectAsync(hp[0], int.Parse(hp[1]), _stop.Token);
                await s.WriteAsync("HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray());
                var us = up.GetStream();
                await Task.WhenAny(s.CopyToAsync(us, _stop.Token), us.CopyToAsync(s, _stop.Token));
            }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
