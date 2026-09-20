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

    /// <summary>
    /// Ask the receiver to pull a PC file into /data/homebrew itself
    /// (Images tab copy, now also from the PC app). The PC file server
    /// must serve the URL (/pkg/{id} covers every registered file).
    /// </summary>
    public static async Task<(bool Ok, string Reply)> PullAsync(string psIp, string url, string remotePath)
    {
        try
        {
            using var c = NewClient();
            string json = $"{{\"url\":\"{JsonEscape(url)}\",\"path\":\"{JsonEscape(remotePath)}\"}}";
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var resp = await c.PostAsync($"http://{psIp}:12800/api/files/pull", content);
            string body = await resp.Content.ReadAsStringAsync();
            return (body.Contains("started") || body.Contains("\"ok\""), body);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Pull-copy progress from GET /api/status: (active, name, got, want).
    /// </summary>
    public static async Task<(bool Active, string Name, long Got, long Want)> GetPullAsync(string psIp)
    {
        try
        {
            using var c = NewClient(10);
            string body = await c.GetStringAsync($"http://{psIp}:12800/api/status");
            bool active = body.Contains("\"pull\":true");
            string name = StrField(body, "pullName");
            long got = LongField(body, "pullGot");
            long want = LongField(body, "pullWant");
            return (active, name, got, want);
        }
        catch
        {
            return (false, "", 0, -1);
        }
    }

    private static string StrField(string body, string key)
    {
        try
        {
            string pat = "\"" + key + "\":\"";
            int i = body.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0)
                return "";
            i += pat.Length;
            int j = body.IndexOf('"', i);
            return j < 0 ? "" : body[i..j];
        }
        catch
        {
            return "";
        }
    }

    private static long LongField(string body, string key)
    {
        try
        {
            string pat = "\"" + key + "\":";
            int i = body.IndexOf(pat, StringComparison.Ordinal);
            if (i < 0)
                return -1;
            i += pat.Length;
            int j = i;
            while (j < body.Length && (char.IsDigit(body[j]) || body[j] == '-'))
                j++;
            return long.TryParse(body[i..j], out long v) ? v : -1;
        }
        catch
        {
            return -1;
        }
    }
    /// <summary>
    /// Remote file size via GET /api/files/stat?path= : (exists, size).
    /// Used to verify a pull copy really landed byte-complete.
    /// </summary>
    public static async Task<(bool Exists, long Size)> StatAsync(string psIp, string remotePath)
    {
        try
        {
            using var c = NewClient(10);
            string body = await c.GetStringAsync(
                $"http://{psIp}:12800/api/files/stat?path={Uri.EscapeDataString(remotePath)}");
            return (body.Contains("\"exists\":true"), LongField(body, "size"));
        }
        catch
        {
            return (false, -1);
        }
    }
    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
