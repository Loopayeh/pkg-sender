using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.ReactiveUI;

namespace PkgSender;

internal static class Program
{
    // Set by the installer launch ("PkgSender.exe --first-install"):
    // forces the About (support links) dialog on this run, even if a
    // previous install already set AboutShown in settings.json.
    internal static bool ForceAbout;

    [STAThread]
    public static int Main(string[] args)
    {
        LoopDPI.Core.UpdateService.CleanupOldBinary();
        foreach (var a in args)
            if (a.Equals("--first-install", StringComparison.OrdinalIgnoreCase))
                ForceAbout = true;
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--selftest", StringComparison.OrdinalIgnoreCase))
                return SelfTest(args[i + 1]);
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Headless check: serve every .pkg in <dir> as /pkg/{id}, download each
    // back fully + one byte-range, compare. No console needed.
    private static int SelfTest(string dir)
    {
        var files = Directory.GetFiles(dir, "*.pkg").OrderBy(f => f).ToArray();
        if (files.Length == 0)
        {
            Console.WriteLine("no .pkg files in " + dir);
            return 2;
        }
        var map = new Dictionary<string, string>();
        for (int i = 0; i < files.Length; i++)
            map[i.ToString()] = files[i];
        using var server = new LoopDPI.Core.RangeFileServer(map, 19898);
        server.Start();
        using var http = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
        bool ok = true;
        for (int i = 0; i < files.Length; i++)
        {
            string url = $"http://127.0.0.1:19898/pkg/{i}";
            byte[] want = File.ReadAllBytes(files[i]);
            byte[] got;
            try
            {
                got = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{i}] FULL FAIL: {ex.Message}");
                ok = false;
                continue;
            }
            bool full = got.SequenceEqual(want);
            Console.WriteLine($"[{i}] {Path.GetFileName(files[i])} full={got.Length} match={full}");
            ok &= full;
            try
            {
                using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
                req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(10, 109);
                using var resp = http.SendAsync(req).GetAwaiter().GetResult();
                byte[] part = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                bool range = (int)resp.StatusCode == 206 && part.SequenceEqual(want.Skip(10).Take(100).ToArray());
                Console.WriteLine($"[{i}] range 10-109: {(int)resp.StatusCode} match={range}");
                ok &= range;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{i}] RANGE FAIL: {ex.Message}");
                ok = false;
            }
        }
        Console.WriteLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
        return ok ? 0 : 1;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();

    public static string FormatSize(long bytes)
    {
        if (bytes <= 0)
            return "";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double size = bytes;
        int u = 0;
        while (size >= 1024 && u < units.Length - 1)
        {
            size /= 1024;
            u++;
        }
        return $"{size:0.#} {units[u]}";
    }
}
