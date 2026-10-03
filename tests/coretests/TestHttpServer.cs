using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;

namespace LoopDPI.Core.Tests;

/// <summary>
/// Tiny throwaway HTTP server for updater tests: serves canned responses
/// on 127.0.0.1, no outside network touched.
/// </summary>
internal sealed class TestHttpServer : IDisposable
{
    private readonly TcpListener _listener;
    public string BaseUrl { get; }

    public TestHttpServer(Func<string, (int status, string body)> respond)
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUrl = $"http://127.0.0.1:{port}";
        _ = AcceptLoopAsync(respond);
    }

    public static (int status, string body) Json(string body) => (200, body);

    private async Task AcceptLoopAsync(Func<string, (int, string)> respond)
    {
        while (true)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(); }
            catch { return; }
            _ = HandleAsync(client, respond);
        }
    }

    private static async Task HandleAsync(TcpClient client, Func<string, (int, string)> respond)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var buf = new byte[64 * 1024];
                int got = 0;
                // Read until end of request headers (bodies are not used here).
                while (!Encoding.ASCII.GetString(buf, 0, got).Contains("\r\n\r\n"))
                {
                    int r = await stream.ReadAsync(buf.AsMemory(got));
                    if (r == 0) break;
                    got += r;
                }
                string req = Encoding.ASCII.GetString(buf, 0, got);
                string path = req.Split(' ')[1..2].FirstOrDefault() ?? "/";
                var (status, body) = respond(path);
                byte[] payload = Encoding.UTF8.GetBytes(body);
                string reason = status switch { 200 => "OK", 404 => "Not Found", _ => "Error" };
                string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json\r\n" +
                              $"Content-Length: {payload.Length}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
            }
            catch { }
        }
    }

    public void Dispose() => _listener.Stop();
}
