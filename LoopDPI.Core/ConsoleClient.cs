using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace LoopDPI.Core;

/// <summary>Minimal RPI-compatible client for LoopDPI on the PS5 (port 12800, PS4 fallback 9090).</summary>
public static class ConsoleClient
{
    private static readonly int[] Ports = { 12800, 9090 };

    private static HttpClient NewClient(int seconds = 15)
    {
        var h = new HttpClientHandler { UseProxy = false };
        var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(seconds) };
        return c;
    }

    /// <summary>GET path from the first reachable port (12800, then 9090). Null if none.</summary>
    private static async Task<string?> GetAnyAsync(string psIp, string path, int seconds = 15)
    {
        foreach (int p in Ports)
        {
            try
            {
                using var c = NewClient(seconds);
                return await c.GetStringAsync($"http://{psIp}:{p}{path}");
            }
            catch
            {
            }
        }
        return null;
    }

    /// <summary>POST json to the first port that answers 2xx. Returns body or null.</summary>
    private static async Task<string?> PostAnyAsync(string psIp, string path, string json, int seconds = 15)
    {
        foreach (int p in Ports)
        {
            try
            {
                using var c = NewClient(seconds);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var resp = await c.PostAsync($"http://{psIp}:{p}{path}", content);
                string body = await resp.Content.ReadAsStringAsync();
                if (resp.IsSuccessStatusCode)
                    return body;
            }
            catch
            {
            }
        }
        return null;
    }

    public static async Task<bool> IsOnlineAsync(string psIp)
    {
        string? body = await GetAnyAsync(psIp, "/api", 3);
        return body != null && body.Contains("Unsupported method") && body.Contains("fail");
    }

    public static async Task<(bool Ok, string Reply)> PushAsync(string psIp, string url, string? name = null, string? iconUrl = null)
    {
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
        string? body = await PostAnyAsync(psIp, "/api/install", json);
        if (body == null)
            return (false, "no reply on 12800/9090");
        return (body.Contains("\"success\""), body);
    }

    public static async Task<(bool Supported, bool Busy)> GetStatusAsync(string psIp)
    {
        string? body = await GetAnyAsync(psIp, "/api/status", 10);
        if (body == null)
            return (false, false);
        // New payload: {"busy":true,"active":1}. Old ones answer the
        // WebUI HTML here -> no "busy" key -> unsupported.
        if (!body.Contains("busy"))
            return (false, false);
        return (true, body.Contains("\"busy\":true"));
    }

    /// <summary>
    /// Ask the receiver to pull a PC file into /data/homebrew itself
    /// (Images tab copy, now also from the PC app). The PC file server
    /// must serve the URL (/pkg/{id} covers every registered file).
    /// resume = continue a partial file instead of starting over.
    /// </summary>
    public static async Task<(bool Ok, string Reply)> PullAsync(string psIp, string url, string remotePath, bool resume = false)
    {
        string json = $"{{\"url\":\"{JsonEscape(url)}\",\"path\":\"{JsonEscape(remotePath)}\",\"mode\":\"{(resume ? "resume" : "overwrite")}\"}}";
        string? body = await PostAnyAsync(psIp, "/api/files/pull", json);
        if (body == null)
            return (false, "no reply on 12800/9090");
        return (body.Contains("started") || body.Contains("\"ok\""), body);
    }

    /// <summary>
    /// Pull-copy progress from GET /api/status: (active, name, got, want, paused).
    /// </summary>
    public static async Task<(bool Active, string Name, long Got, long Want, bool Paused)> GetPullAsync(string psIp)
    {
        string? body = await GetAnyAsync(psIp, "/api/status", 10);
        if (body == null)
            return (false, "", 0, -1, false);
        bool active = body.Contains("\"pull\":true");
        string name = StrField(body, "pullName");
        long got = LongField(body, "pullGot");
        long want = LongField(body, "pullWant");
        bool paused = body.Contains("\"pullPaused\":true");
        return (active, name, got, want, paused);
    }

    /// <summary>Pause/unpause the receiver's running pull copy.</summary>
    public static async Task<bool> PullPauseAsync(string psIp, bool paused)
    {
        string json = $"{{\"paused\":{(paused ? 1 : 0)}}}";
        string? body = await PostAnyAsync(psIp, "/api/pull/pause", json, 10);
        return body != null && body.Contains("\"ok\"");
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
        string? body = await GetAnyAsync(psIp,
            "/api/files/stat?path=" + Uri.EscapeDataString(remotePath), 10);
        if (body == null)
            return (false, -1);
        return (body.Contains("\"exists\":true"), LongField(body, "size"));
    }
    private static string JsonEscape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", " ").Replace("\n", " ");
}
