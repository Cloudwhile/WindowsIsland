using System.Net;
using System.Net.Sockets;
using System.Text;

namespace WindowsIsland.PluginSecurityTests;

internal sealed class LoopbackCanary : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _lifetime = new();
    public int Connections;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public LoopbackCanary()
    {
        _listener.Start();
        _ = Task.Run(AcceptAsync);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                using var client = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                Interlocked.Increment(ref Connections);
                using var stream = client.GetStream();
                var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nOK");
                await stream.WriteAsync(response, _lifetime.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
    }

    public void Dispose() { _lifetime.Cancel(); _listener.Stop(); }
}
