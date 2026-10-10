using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ConnectionManager.Integration.Tests;

/// A tiny HTTP server on 127.0.0.1 that answers every request with 200 OK.
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    public LoopbackServer()
    {
        _listener.Start();
        _ = Task.Run(ServeAsync);
    }

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";

    private async Task ServeAsync()
    {
        var reply = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var stream = client.GetStream();
                _ = await stream.ReadAsync(new byte[4096], _stop.Token);
                await stream.WriteAsync(reply, _stop.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                // One broken client doesn't stop the server.
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
