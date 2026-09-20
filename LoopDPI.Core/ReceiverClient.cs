using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LoopDPI.Core;

/// <summary>
/// HTTP client for the pkg-receiver payload file endpoints:
/// stat / mkdir / write / done.
/// </summary>
public sealed class ReceiverClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string[] _bases;
    private string _sticky;

    public ReceiverClient(string psIp, int timeoutSeconds = 0)
    {
        _bases = new[] { $"http://{psIp}:12800", $"http://{psIp}:9090" };
        _sticky = _bases[0];
        var h = new HttpClientHandler { UseProxy = false };
        _http = new HttpClient(h);
        if (timeoutSeconds > 0)
            _http.Timeout = TimeSpan.FromSeconds(timeoutSeconds);
        else
            _http.Timeout = Timeout.InfiniteTimeSpan;
    }

    private HttpRequestMessage NewRequest(HttpMethod method, string baseUrl, string path) =>
        new HttpRequestMessage(method, baseUrl + path);

    /// <summary>Try the last-working port first, fall back to the other.</summary>
    private IEnumerable<string> Ordered()
    {
        yield return _sticky;
        foreach (var b in _bases)
            if (b != _sticky)
                yield return b;
    }

    private static string Q(string remotePath) => "?path=" + Uri.EscapeDataString(remotePath);

    public async Task<(bool Ok, bool Exists, long Size, string Reply)> StatAsync(string remotePath, CancellationToken ct = default)
    {
        string lastErr = "";
        foreach (var b in Ordered())
        {
        try
        {
            using var req = NewRequest(HttpMethod.Get, b, "/api/files/stat" + Q(remotePath));
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode)
                return (false, false, 0, body);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                bool exists = root.TryGetProperty("exists", out var e) && e.GetBoolean();
                long size = root.TryGetProperty("size", out var s) && s.TryGetInt64(out var v) ? v : 0;
                _sticky = b;
                return (true, exists, size, body);
            }
            catch
            {
                return (false, false, 0, "bad stat reply: " + body);
            }
        }
        catch (Exception ex)
        {
            lastErr = ex.Message;
        }
        }
        return (false, false, 0, lastErr);
    }

    public async Task<(bool Ok, string Reply)> MkdirAsync(string remotePath, CancellationToken ct = default)
    {
        string lastErr = "";
        foreach (var b in Ordered())
        {
        try
        {
            string json = "{\"path\":\"" + JsonEscape(remotePath) + "\"}";
            using var req = NewRequest(HttpMethod.Post, b, "/api/files/mkdir");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                _sticky = b;
                return (true, body);
            }
            lastErr = body;
        }
        catch (Exception ex)
        {
            lastErr = ex.Message;
        }
        }
        return (false, lastErr);
    }

    public async Task<(bool Ok, string Reply)> WriteChunkAsync(
        string remotePath, long offset, byte[] buf, int count, CancellationToken ct = default)
    {
        string lastErr = "";
        foreach (var b in Ordered())
        {
        try
        {
            using var req = NewRequest(HttpMethod.Post, b,
                "/api/files/write" + Q(remotePath) + "&offset=" + offset);
            req.Content = new ByteArrayContent(buf, 0, count);
            req.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                _sticky = b;
                return (true, body);
            }
            lastErr = body;
        }
        catch (Exception ex)
        {
            lastErr = ex.Message;
        }
        }
        return (false, lastErr);
    }

    public async Task<(bool Ok, string Reply)> DoneAsync(string remotePath, long size, CancellationToken ct = default)
    {
        string lastErr = "";
        foreach (var b in Ordered())
        {
        try
        {
            string json = "{\"path\":\"" + JsonEscape(remotePath) + "\",\"size\":" + size + "}";
            using var req = NewRequest(HttpMethod.Post, b, "/api/files/done");
            req.Content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await _http.SendAsync(req, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);
            if (resp.IsSuccessStatusCode)
            {
                _sticky = b;
                return (true, body);
            }
            lastErr = body;
        }
        catch (Exception ex)
        {
            lastErr = ex.Message;
        }
        }
        return (false, lastErr);
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    public void Dispose() => _http.Dispose();
}
