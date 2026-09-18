using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace LoopDPI.Core;

/// <summary>Minimal RPI-compatible client for LoopDPI on the PS5 (port 12800).</summary>
public static class ConsoleClient
{
    private static HttpClient NewClient(int seconds = 15)
    {
        var h = new HttpClientHandler { UseProxy = false };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(seconds) };
        return c;
    }

    public static async Task<bool> IsOnlineAsync(string psIp)
    {
        try
        {
            using var c = NewClient(3);
            var body = await c.GetStringAsync($"http://{psIp}:12800/api");
            return body.Contains("Unsupported method") && body.Contains("fail");
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(bool Ok, string Reply)> PushAsync(string psIp, string url, string? name = null, string? iconUrl = null)
    {
        try
        {
            using var c = NewClient();
            // LoopDPI + RPI both accept this shape; URL travels encoded.
            string enc = Uri.EscapeDataString(url.Replace("https://", "http://"));
            string json = $"{{\"type\":\"direct\",\"packages\":[\"{enc}\"]}}";
            if (!string.IsNullOrWhiteSpace(name))
                json = $"{{\"type\":\"direct\",\"packages\":[\"{enc}\"],\"name\":\"{JsonEscape(name)}\"}}";
            if (!string.IsNullOrWhiteSpace(iconUrl))
            {
                // Insert icon_url before the closing brace.
                json = json[..^1] + $",\"icon_url\":\"{JsonEscape(iconUrl)}\"}}";
            }
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await c.PostAsync($"http://{psIp}:12800/api/install", content);
            string body = await resp.Content.ReadAsStringAsync();
            return (body.Contains("\"success\""), body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static async Task<(bool Supported, bool Busy)> GetStatusAsync(string psIp)
    {
        try
        {
            using var c = NewClient(10);
            string body = await c.GetStringAsync($"http://{psIp}:12800/api/status");
            // New payload: {"busy":true,"active":1}. Old ones answer the
            // WebUI HTML here -> no "busy" key -> unsupported.
            if (!body.Contains("busy"))
                return (false, false);
            return (true, body.Contains("\"busy\":true"));
        }
        catch
        {
            return (false, false);
        }
    }

    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
