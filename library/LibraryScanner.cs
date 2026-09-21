using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using LoopDPI.Core;

namespace PkgSender;

/// <summary>Recursive game scan (*.pkg/.exfat/.ffpfsc/.ffpkg + game folders) with a JSON cache.</summary>
public static class LibraryScanner
{
    private static readonly string[] GameExts = { ".pkg", ".exfat", ".ffpfsc", ".ffpkg" };    public static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PkgSender", "library.json");

    private sealed record CachedGame(long Size, long Mtime, string Title, string ContentId, string TitleId, string Platform, string Description, string IconB64, string Format, bool IsFolder, string Version, string ContentType, bool IsDlc);
    private sealed record CacheFile(List<string> Roots, Dictionary<string, CachedGame> Games, int Version);

    private const int CacheVersion = 6; // bump when parsing changes (icons/formats)

    public static List<string> LoadRoots()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
                if (c?.Roots != null)
                    return c.Roots.Where(Directory.Exists).ToList();
            }
        }
        catch
        {
        }
        return new List<string>();
    }

    public static void SaveRoots(List<string> roots)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            var games = LoadCacheGames();
            File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheFile(roots, games, CacheVersion)));
        }
        catch
        {
        }
    }

    private static Dictionary<string, CachedGame> LoadCacheGames()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                var c = JsonSerializer.Deserialize<CacheFile>(File.ReadAllText(CachePath));
                if (c?.Games != null && c.Version == CacheVersion)
                    return c.Games;
            }
        }
        catch
        {
        }
        return new Dictionary<string, CachedGame>();
    }

    private static PkgInfo? ReadOrCache(Dictionary<string, CachedGame> cache, string key,
        long size, long mtime, Func<PkgInfo?> read, Action report)
    {
        try
        {
            if (cache.TryGetValue(key, out var c) && c.Size == size && c.Mtime == mtime)
                return Restore(c, size > 0 ? size : EstimateSize(key));
            report();
            var info = read();
            if (info == null)
                return null;
            string b64 = "";
            try
            {
                if (info.IconData is { Length: > 0 and < 786432 })
                    b64 = Convert.ToBase64String(info.IconData);
            }
            catch
            {
            }
            cache[key] = new CachedGame(size, mtime, info.Title, info.ContentId, info.TitleId,
                info.Platform, info.Description, b64, info.Format, info.IsFolder,
                info.Version, info.ContentType, info.IsDlc);
            return info;
        }
        catch
        {
            return null;
        }
    }

    private static PkgInfo Restore(CachedGame c, long size)
    {
        byte[]? icon = null;
        try
        {
            if (!string.IsNullOrEmpty(c.IconB64))
                icon = Convert.FromBase64String(c.IconB64);
        }
        catch
        {
        }
        return new PkgInfo
        {
            Title = c.Title,
            ContentId = c.ContentId,
            TitleId = c.TitleId,
            Platform = c.Platform,
            Description = c.Description,
            PackageSize = size,
            Format = string.IsNullOrEmpty(c.Format) ? "pkg" : c.Format,
            IsFolder = c.IsFolder,
            IconData = icon,
            Version = c.Version ?? "",
            ContentType = c.ContentType ?? "",
            IsDlc = c.IsDlc,
        };
    }

    /// <summary>
    /// Instant library from cache (no disk reads beyond stat): used at
    /// startup so the list shows immediately without rescanning.
    /// </summary>
    public static List<ScannedGame> LoadCachedScans()
    {
        var result = new List<ScannedGame>();
        var cache = LoadCacheGames();
        foreach (var kv in cache)
        {
            try
            {
                string key = kv.Key;
                var c = kv.Value;
                if (c.IsFolder)
                {
                    if (!Directory.Exists(key))
                        continue;
                    result.Add(new ScannedGame(key, Restore(c, EstimateSize(key))));
                }
                else
                {
                    var fi = new FileInfo(key);
                    if (!fi.Exists || fi.Length != c.Size ||
                        fi.LastWriteTimeUtc.Ticks != c.Mtime)
                        continue;
                    result.Add(new ScannedGame(key, Restore(c, c.Size)));
                }
            }
            catch
            {
            }
        }
        return result;
    }

    /// <summary>Forget saved game info (roots are kept); next scan reads everything fresh.</summary>
    public static void ClearGameCache()
    {
        try
        {
            var roots = LoadRoots();
            Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
            File.WriteAllText(CachePath, JsonSerializer.Serialize(
                new CacheFile(roots, new Dictionary<string, CachedGame>(), CacheVersion)));
        }
        catch
        {
        }
    }

    private static long EstimateSize(string key)
    {
        // Folder cache stores size 0 (content shifts); recompute cheap stat.
        // Tolerant walk: skips denied dirs instead of aborting.
        try
        {
            if (!Directory.Exists(key))
                return 0;
            long total = 0;
            var stack = new Stack<string>();
            stack.Push(key);
            while (stack.Count > 0)
            {
                string dir = stack.Pop();
                string[] entries;
                try
                {
                    entries = Directory.GetFileSystemEntries(dir);
                }
                catch
                {
                    continue;
                }
                foreach (var e in entries)
                {
                    try
                    {
                        var attr = File.GetAttributes(e);
                        if ((attr & FileAttributes.ReparsePoint) != 0)
                            continue;
                        if ((attr & FileAttributes.Directory) != 0)
                            stack.Push(e);
                        else
                            total += new FileInfo(e).Length;
                    }
                    catch
                    {
                    }
                }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// Iterative full-tree walk (drive-safe): per-directory error isolation,
    /// skips system/recycle/reparse dirs, collects game files by extension
    /// and game folders (sce_sys/param.json) at any depth. Does not descend
    /// into game folders themselves.
    /// </summary>
    private static void Walk(string root, List<string> files, List<string> folders, IProgress<string>? progress)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        int dirs = 0;
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch
            {
                continue; // denied / gone / too long — skip just this dir
            }
            if (++dirs % 500 == 0)
                progress?.Report($"Walking… {dirs} folders");
            foreach (var e in entries)
            {
                bool isDir;
                try
                {
                    var attr = File.GetAttributes(e);
                    if ((attr & FileAttributes.ReparsePoint) != 0)
                        continue; // junctions/symlinks: no loops
                    if ((attr & FileAttributes.System) != 0)
                        continue;
                    isDir = (attr & FileAttributes.Directory) != 0;
                }
                catch
                {
                    continue;
                }
                if (isDir)
                {
                    string name = Path.GetFileName(e);
                    if (name.StartsWith("$") || name.Equals("System Volume Information", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (GameReader.IsGameFolder(e))
                        folders.Add(e); // game folder: collect, do not descend
                    else
                        stack.Push(e);
                }
                else if (GameExts.Contains(Path.GetExtension(e).ToLowerInvariant()))
                {
                    files.Add(e);
                }
            }
        }
    }

    public sealed record ScannedGame(string Path, PkgInfo Info);

    public static async Task<List<ScannedGame>> ScanAsync(List<string> roots, IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var cache = LoadCacheGames();
            var result = new List<ScannedGame>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<string>();
            var folders = new List<string>();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root))
                    continue;
                progress?.Report($"Walking {root}…");
                Walk(root, files, folders, progress);
            }
            int done = 0;
            int total = files.Count + folders.Count;
            foreach (var f in files)
            {
                if (!seen.Add(f))
                    continue;
                done++;
                try
                {
                    var fi = new FileInfo(f);
                    var info = ReadOrCache(cache, f, fi.Length, fi.LastWriteTimeUtc.Ticks,
                        () => GameReader.Read(f),
                        () => progress?.Report($"Reading {done}/{total}: {Path.GetFileName(f)}"));
                    if (info != null)
                        result.Add(new ScannedGame(f, info));
                }
                catch
                {
                }
            }
            foreach (var d in folders)
            {
                if (!seen.Add(d))
                    continue;
                done++;
                try
                {
                    string pj = Path.Combine(d, "sce_sys", "param.json");
                    var pi = new FileInfo(pj);
                    long key = pi.Exists ? pi.LastWriteTimeUtc.Ticks : 0;
                    var info = ReadOrCache(cache, d, 0, key,
                        () => GameReader.Read(d),
                        () => progress?.Report($"Reading {done}/{total}: {Path.GetFileName(d)}/"));
                    if (info != null)
                        result.Add(new ScannedGame(d, info));
                }
                catch
                {
                }
            }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                // Drop renamed/deleted entries so stale stubs never come back.
                foreach (var k in cache.Keys.Where(k => !seen.Contains(k)).ToList())
                    cache.Remove(k);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(new CacheFile(roots, cache, CacheVersion)));
            }
            catch
            {
            }
            return result.OrderBy(g => g.Info.Title).ToList();
        });
    }
}
