using System.Net;
using System.Net.Sockets;
using Npgsql;

namespace Reeve.EntityFrameworkCore.AdvisoryLocks.IntegrationTests;

public sealed class FaultyProxy : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _host;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Link> _links = [];
    private readonly Task _accepting;
    private int _delayMilliseconds;
    private byte[] _delayedMarker = [];

    public FaultyProxy(string connectionString)
    {
        var upstream = new NpgsqlConnectionStringBuilder(connectionString);
        _host = upstream.Host!;
        _port = upstream.Port;
        _listener.Start();
        ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
        {
            Host = "127.0.0.1",
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port,
        }.ConnectionString;
        _accepting = AcceptAsync();
    }

    public string ConnectionString { get; }

    public void DelayAnswersContaining(string marker, TimeSpan delay)
    {
        Volatile.Write(ref _delayedMarker, System.Text.Encoding.ASCII.GetBytes(marker));
        Volatile.Write(ref _delayMilliseconds, (int)delay.TotalMilliseconds);
    }

    public void SeverOpenConnections()
    {
        lock (_links)
        {
            foreach (var link in _links)
            {
                link.Severed = true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try
        {
            await _accepting;
        }
        catch (Exception)
        {
        }

        lock (_links)
        {
            foreach (var link in _links)
            {
                link.Close();
            }
        }

        _stop.Dispose();
    }

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var server = new TcpClient { NoDelay = true };
            client.NoDelay = true;
            await server.ConnectAsync(_host, _port, _stop.Token);
            var link = new Link(client, server);
            lock (_links)
            {
                _links.Add(link);
            }

            _ = Task.Run(() => PumpAsync(link, client, server, towardsServer: true));
            _ = Task.Run(() => PumpAsync(link, server, client, towardsServer: false));
        }
    }

    private async Task PumpAsync(Link link, TcpClient from, TcpClient to, bool towardsServer)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            var source = from.GetStream();
            var target = to.GetStream();
            int read;
            while ((read = await source.ReadAsync(buffer, _stop.Token)) > 0)
            {
                if (towardsServer && link.Severed)
                {
                    break;
                }

                var delay = Volatile.Read(ref _delayMilliseconds);
                if (!towardsServer && delay > 0 && buffer.AsSpan(0, read).IndexOf(Volatile.Read(ref _delayedMarker)) >= 0)
                {
                    await Task.Delay(delay, _stop.Token);
                }

                await target.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
            }
        }
        catch (Exception)
        {
        }

        link.Close();
    }

    private sealed class Link(TcpClient client, TcpClient server)
    {
        public volatile bool Severed;

        public void Close()
        {
            client.Close();
            server.Close();
        }
    }
}
