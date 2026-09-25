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
/// Windows installed builds: checks GitHub releases, downloads the Setup
/// installer, runs it silent and exits so Setup can overwrite the exe.
/// Linux tar.gz builds: downloads the tar.gz and swaps the binary in place.
/// </summary>
public static class UpdateService
{
    public const string AppVersion = "v1.2.8";
    public const string UpdateRepo = "Loopayeh/pkg-sender";

    /// <summary>How this build is packaged; only TarGz self-updates on Linux.</summary>
    public enum LinuxFlavor { None, TarGz, Deb, AppImage }

    public static LinuxFlavor Flavor()
    {
        if (!OperatingSystem.IsLinux()) return LinuxFlavor.None;
        string p = Environment.ProcessPath ?? "";
        if (p.EndsWith(".AppImage", StringComparison.OrdinalIgnoreCase)) return LinuxFlavor.AppImage;
        // Official .deb layout: /usr/lib/pkgsender/pkgsender (CI linux-deb.yml).
        if (p.StartsWith("/usr/lib/pkgsender/", StringComparison.OrdinalIgnoreCase)) return LinuxFlavor.Deb;
        return LinuxFlavor.TarGz;
    }

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
        if (OperatingSystem.IsLinux())
        {
            // PkgSender-<ver>-linux-x64.tar.gz (same naming scheme as the APK).
            foreach (var a in assets)
            {
                string n = (a.Name ?? "").ToLowerInvariant();
                if (n.Contains("linux") && n.EndsWith(".tar.gz") && !string.IsNullOrEmpty(a.Url))
                    return a;
            }
            return null;
        }
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

    /// <summary>
    /// Linux tar.gz self-update: extract the new PkgSender binary from the
    /// downloaded tar.gz next to the current one (old binary kept as
    /// PkgSender.old, removed on next start), relaunch and return true.
    /// Caller must exit immediately.
    /// </summary>
    public static bool RunTarGzUpdateAndExit(string tarGzPath)
    {
        try
        {
            string exe = Environment.ProcessPath ?? "";
            string dir = Path.GetDirectoryName(exe) ?? "";
            if (string.IsNullOrEmpty(exe) || Flavor() != LinuxFlavor.TarGz) return false;
            string tmp = Path.Combine(Path.GetTempPath(), "pkgsender_upd_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            // tar keeps the stored exec bit, so no chmod pass needed.
            using (var proc = Process.Start(new ProcessStartInfo("tar",
                $"-xzf \"{tarGzPath}\" -C \"{tmp}\" PkgSender") { UseShellExecute = false }))
            {
                if (proc == null || !proc.WaitForExit(120000) || proc.ExitCode != 0) return false;
            }
            string fresh = Path.Combine(tmp, "PkgSender");
            if (!File.Exists(fresh)) return false;
            string old = exe + ".old";
            try { File.Delete(old); } catch { }
            File.Move(exe, old);
            File.Move(fresh, exe);
            try { Directory.Delete(tmp, true); } catch { }
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = dir });
            return true;
        }
        catch { return false; }
    }

    /// <summary>Remove the binary left behind by a previous tar.gz self-update.</summary>
    public static void CleanupOldBinary()
    {
        try
        {
            if (!OperatingSystem.IsLinux()) return;
            string old = (Environment.ProcessPath ?? "") + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
    }
}
