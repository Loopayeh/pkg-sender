using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace LoopDPI.Core;

/// <summary>
/// Self-update helper, mirroring pkg-viewer/updater.py.
/// Installed builds only: checks GitHub releases, downloads the Setup
/// installer, runs it silent and exits so Setup can overwrite the exe.
/// </summary>
public static class UpdateService
{
    public const string AppVersion = "v1.2.5";
    public const string UpdateRepo = "Loopayeh/pkg-sender";

    public sealed record Asset(string Name, string Url, long Size);
    public sealed record Release(string Tag, string Name, string Body, List<Asset> Assets);

    public static int[] ParseVersion(string? s)
    {
        s = (s ?? "").Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];
        var out_ = new List<int>();
        foreach (var p in s.Split('.'))
        {
            string d = new string(p.TakeWhile(char.IsDigit).ToArray());
            out_.Add(int.TryParse(d, out int v) ? v : 0);
        }
        return out_.Count == 0 ? new[] { 0 } : out_.ToArray();
    }

    public static bool IsNewer(string latestTag, string current)
    {
        var a = ParseVersion(latestTag);
        var b = ParseVersion(current);
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x > y;
        }
        return false;
    }

    public static async Task<Release?> FetchLatestAsync(string? repo = null)
    {
        repo ??= Environment.GetEnvironmentVariable("PKGSENDER_UPDATE_REPO") ?? UpdateRepo;
        string api = Environment.GetEnvironmentVariable("PKGSENDER_UPDATE_API")
            ?? $"https://api.github.com/repos/{repo}/releases/latest";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
            http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
            http.DefaultRequestHeaders.Add("User-Agent", repo + " updater");
            http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
            using var resp = await http.GetAsync(api);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (!root.TryGetProperty("tag_name", out var tagEl)) return null;
            string tag = tagEl.GetString() ?? "";
            if (string.IsNullOrEmpty(tag)) return null;
            string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? tag) : tag;
            string body = root.TryGetProperty("body", out var bEl) ? (bEl.GetString() ?? "") : "";
            var assets = new List<Asset>();
            if (root.TryGetProperty("assets", out var aEl) && aEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in aEl.EnumerateArray())
                {
                    string url = a.TryGetProperty("browser_download_url", out var uEl) ? (uEl.GetString() ?? "") : "";
                    if (string.IsNullOrEmpty(url)) continue;
                    string an = a.TryGetProperty("name", out var anEl) ? (anEl.GetString() ?? "") : "";
                    long sz = a.TryGetProperty("size", out var sEl) && sEl.TryGetInt64(out long s) ? s : 0;
                    assets.Add(new Asset(an, url, sz));
                }
            }
            return new Release(tag, name, body, assets);
        }
        catch { return null; }
    }

    public static Asset? PickSetupAsset(Release? info)
    {
        var assets = info?.Assets ?? new();
        foreach (var a in assets)
        {
            string n = (a.Name ?? "").ToLowerInvariant();
            if (n.StartsWith("pkgsender-setup") && n.EndsWith(".exe") && !string.IsNullOrEmpty(a.Url))
                return a;
        }
        return assets.FirstOrDefault(a =>
            (a.Name ?? "").ToLowerInvariant().Contains("setup") && a.Name.ToLowerInvariant().EndsWith(".exe"));
    }

    public static async Task DownloadAsync(string url, string dest, Action<long, long>? progress = null)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add("User-Agent", "updater");
        using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long total = resp.Content.Headers.ContentLength ?? 0;
        using var src = await resp.Content.ReadAsStreamAsync();
        using var dst = File.Create(dest);
        byte[] buf = new byte[256 * 1024];
        long got = 0;
        int r;
        while ((r = await src.ReadAsync(buf)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, r));
            got += r;
            try { progress?.Invoke(got, total); } catch { }
        }
    }

    /// <summary>Install dir when running as installed exe, else null.</summary>
    public static string? InstallDir()
    {
        try
        {
            string d = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
            if (File.Exists(Path.Combine(d, "unins000.exe")))
                return d;
        }
        catch { }
        return null;
    }

    public static bool IsInstalled() => InstallDir() != null;

    /// <summary>Launch Setup installer silent and return true. Caller must exit immediately.</summary>
    public static bool RunSetupAndExit(string setupPath)
    {
        try
        {
            Process.Start(new ProcessStartInfo(setupPath,
                "/SP- /SILENT /NORESTART /CLOSEAPPLICATIONS") { UseShellExecute = true });
            return true;
        }
        catch { return false; }
    }
}
