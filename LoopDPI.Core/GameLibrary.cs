using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LoopDPI.Core;

/// <summary>
/// Library reader for all PKG Sender formats: .pkg (existing PkgReader),
/// game folders (sce_sys/param.json + icon0.png, incl. Lizard AMPR assets),
/// .exfat images (minimal FAT walk), .ffpfsc/.ffpkg (metadata stub:
/// title-id from file name, no cover — full parse needs PFS/UFS stacks).
/// Sending never depends on parsing: raw bytes/folders go over as-is.
/// </summary>
public static class GameReader
{
    public static PkgInfo? Read(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return FolderReader.Read(path);
            string low = path.ToLowerInvariant();
            if (low.EndsWith(".pkg"))
            {
                using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                var info = PkgReader.Read(fs);
                return info == null ? null : WithFormat(info, "pkg");
            }
            if (low.EndsWith(".exfat"))
            {
                var native = ExfatReader.Read(path);
                if (native != null && !string.IsNullOrWhiteSpace(native.Title))
                    return native;
                // Fallback: pkg-viewer python parser (handles more images + icon).
                var bridged = PythonHeader.TryRead(path);
                if (bridged != null)
                    return bridged;
                return ImageStub.Read(path);
            }
            if (low.EndsWith(".ffpfsc") || low.EndsWith(".ffpkg"))
            {
                // Native C# only guesses title-id from the file name;
                // prefer the pkg-viewer bridge (real param.json + icon).
                var bridged = PythonHeader.TryRead(path);
                if (bridged != null)
                    return bridged;
                return ImageStub.Read(path);
            }
        }
        catch
        {
        }
        return null;
    }

    private static PkgInfo WithFormat(PkgInfo info, string fmt) => new()
    {
        Title = info.Title,
        ContentId = info.ContentId,
        TitleId = info.TitleId,
        ContentType = info.ContentType,
        Platform = info.Platform,
        Description = info.Description,
        PackageSize = info.PackageSize,
        Format = fmt,
        IsFolder = info.IsFolder,
        IconData = info.IconData,
        Params = info.Params,
        Version = info.Version,
        IsDlc = info.IsDlc,
    };

    public static bool IsGameFolder(string dir)
    {
        try
        {
            return File.Exists(Path.Combine(dir, "sce_sys", "param.json"));
        }
        catch
        {
            return false;
        }
    }

    private static readonly Regex TitleIdRx =
        new(@"\b(PPSA|PPCS|CUSA|NP[A-Z]{2}|BL[A-Z]{2}|BC[A-Z]{2})\d{5}\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string TitleIdFromName(string path)
    {
        var m = TitleIdRx.Match(Path.GetFileName(path));
        return m.Success ? m.Value.ToUpperInvariant() : "";
    }
}

/// <summary>Shared param.json (PS5) meta extraction. Mirrors pkgviewer logic.</summary>
public static class ParamJson
{
    public sealed record Meta(string Title, string TitleId, string ContentId, string Version);

    public static Meta Parse(byte[] json)
    {
        string title = "", titleId = "", contentId = "", version = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.TryGetProperty("titleId", out var t) && t.ValueKind == JsonValueKind.String)
                titleId = t.GetString() ?? "";
            if (root.TryGetProperty("contentId", out var c) && c.ValueKind == JsonValueKind.String)
                contentId = c.GetString() ?? "";
            if (root.TryGetProperty("contentVersion", out var v) && v.ValueKind == JsonValueKind.String)
                version = v.GetString() ?? "";
            if (root.TryGetProperty("localizedParameters", out var lp) && lp.ValueKind == JsonValueKind.Object)
            {
                string lang = "en-US";
                if (lp.TryGetProperty("defaultLanguage", out var dl) && dl.ValueKind == JsonValueKind.String)
                    lang = dl.GetString() ?? lang;
                if (lp.TryGetProperty(lang, out var le) && le.ValueKind == JsonValueKind.Object &&
                    le.TryGetProperty("titleName", out var tn) && tn.ValueKind == JsonValueKind.String)
                    title = tn.GetString() ?? "";
                if (string.IsNullOrEmpty(title))
                {
                    foreach (var prop in lp.EnumerateObject())
                    {
                        if (prop.Value.ValueKind == JsonValueKind.Object &&
                            prop.Value.TryGetProperty("titleName", out var t2) && t2.ValueKind == JsonValueKind.String)
                        {
                            title = t2.GetString() ?? "";
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
        }
        return new Meta(title, titleId, contentId, version);
    }
}

/// <summary>PS5 app dump folder: sce_sys/param.json + icon0.png + Lizard AMPR scan.</summary>
public static class FolderReader
{
    private static readonly byte[][] AmprMagics = new[]
    {
        "AMPRPAK4"u8.ToArray(), "AMPRDAT3"u8.ToArray(), "AMPRIDX3"u8.ToArray(),
        "AMPRCRC1"u8.ToArray(), "AMPRCFG1"u8.ToArray(),
    };

    public static PkgInfo? Read(string dir)
    {
        string pj = Path.Combine(dir, "sce_sys", "param.json");
        if (!File.Exists(pj))
            return null;

        ParamJson.Meta meta;
        try
        {
            meta = ParamJson.Parse(File.ReadAllBytes(pj));
        }
        catch
        {
            return null;
        }

        byte[]? icon = null;
        try
        {
            string ic = Path.Combine(dir, "sce_sys", "icon0.png");
            if (File.Exists(ic))
            {
                var b = File.ReadAllBytes(ic);
                if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50)
                    icon = b;
            }
        }
        catch
        {
        }

        long total = 0;
        int nfiles = 0, ampr = 0;
        long amprBytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                nfiles++;
                try
                {
                    var fi = new FileInfo(f);
                    total += fi.Length;
                    if (fi.Length > 1024 * 1024 && IsAmpr(f))
                    {
                        ampr++;
                        amprBytes += fi.Length;
                    }
                }
                catch
                {
                }
            }
        }
        catch
        {
        }

        string title = string.IsNullOrEmpty(meta.Title) ? new DirectoryInfo(dir).Name : meta.Title;
        string desc = ampr > 0
            ? $"[Folder + Lizard {ampr} AMPR] {title}"
            : $"[PS5 folder] {title}";

        var pars = new List<PkgParam>();
        if (!string.IsNullOrEmpty(meta.TitleId))
            pars.Add(new PkgParam("TITLE_ID", meta.TitleId));
        if (!string.IsNullOrEmpty(meta.ContentId))
            pars.Add(new PkgParam("CONTENT_ID", meta.ContentId));
        if (!string.IsNullOrEmpty(meta.Version))
            pars.Add(new PkgParam("VERSION", meta.Version));
        pars.Add(new PkgParam("FILES", nfiles.ToString()));
        if (ampr > 0)
            pars.Add(new PkgParam("AMPR", $"{ampr} containers"));

        return new PkgInfo
        {
            Title = title,
            ContentId = meta.ContentId,
            TitleId = meta.TitleId,
            Platform = "PS5",
            Description = desc,
            PackageSize = total,
            Format = "folder",
            IsFolder = true,
            IconData = icon,
            Params = pars,
        };
    }

    private static bool IsAmpr(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            Span<byte> head = stackalloc byte[8];
            if (fs.Read(head) != 8)
                return false;
            foreach (var m in AmprMagics)
            {
                bool same = true;
                for (int i = 0; i < 8; i++)
                    if (head[i] != m[i])
                    {
                        same = false;
                        break;
                    }
                if (same)
                    return true;
            }
        }
        catch
        {
        }
        return false;
    }
}

/// <summary>Metadata stub for .ffpfsc (compressed PFS) and .ffpkg (UFS2):
/// title-id guessed from file name, size from disk, no cover.</summary>
public static class ImageStub
{
    public static PkgInfo Read(string path)
    {
        string low = path.ToLowerInvariant();
        string fmt = low.EndsWith(".ffpfsc") ? "ffpfsc"
            : low.EndsWith(".exfat") ? "exfat"
            : "ffpkg";
        string tid = GameReader.TitleIdFromName(path);
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch
        {
        }
        string title = string.IsNullOrEmpty(tid) ? Path.GetFileName(path) : tid;
        return new PkgInfo
        {
            Title = title,
            ContentId = "",
            TitleId = tid,
            Platform = "PS5",
            Description = $"[PS5 {fmt} image] {title}",
            PackageSize = size,
            Format = fmt,
            IconData = null,
            Params = string.IsNullOrEmpty(tid)
                ? new List<PkgParam>()
                : new List<PkgParam> { new PkgParam("TITLE_ID", tid) },
        };
    }
}

/// <summary>Minimal read-only exFAT walker: locates sce_sys/param.json +
/// icon0.png inside PS5 dump images. No writes, caps all reads.</summary>
public static class ExfatReader
{
    private const uint EndOfChain = 0xFFFFFFF8;

    private sealed class Fs
    {
        public Stream F = null!;
        public int SectorSize;
        public int ClusterSectors;
        public long FatBase;
        public long HeapBase;
    }

    public static PkgInfo? Read(string path)
    {
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            long size = 0;
            try { size = fs.Length; } catch { }
            return Read(fs, path, size);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Parse an exFAT image from any seekable stream (phone SAF included).</summary>
    public static PkgInfo? Read(Stream input, string nameForIds, long size)
    {
        try
        {
            var v = OpenFs(input);
            if (v == null)
                return null;

            var sce = FindDir(v, ReadRoot(v), "sce_sys");
            if (sce == null)
                return null;
            var pj = FindFile(v, sce.Value.Cluster, sce.Value.Contiguous, "param.json");
            if (pj == null)
                return null;
            byte[] json = ReadFile(v, pj.Value, 2 * 1024 * 1024);
            if (json.Length == 0)
                return null;
            var meta = ParamJson.Parse(json);

            byte[]? icon = null;
            var ic = FindFile(v, sce.Value.Cluster, sce.Value.Contiguous, "icon0.png");
            if (ic != null)
            {
                var b = ReadFile(v, ic.Value, 8 * 1024 * 1024);
                if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50)
                    icon = b;
            }

            string tid = meta.TitleId;
            if (string.IsNullOrEmpty(tid))
                tid = GameReader.TitleIdFromName(nameForIds);
            string title = string.IsNullOrEmpty(meta.Title)
                ? (string.IsNullOrEmpty(tid) ? Path.GetFileName(nameForIds) : tid)
                : meta.Title;

            var pars = new List<PkgParam>();
            if (!string.IsNullOrEmpty(tid))
                pars.Add(new PkgParam("TITLE_ID", tid));
            if (!string.IsNullOrEmpty(meta.ContentId))
                pars.Add(new PkgParam("CONTENT_ID", meta.ContentId));
            if (!string.IsNullOrEmpty(meta.Version))
                pars.Add(new PkgParam("VERSION", meta.Version));

            return new PkgInfo
            {
                Title = title,
                ContentId = meta.ContentId,
                TitleId = tid,
                Platform = "PS5",
                Description = $"[PS5 exfat image] {title}",
                PackageSize = size,
                Format = "exfat",
                IconData = icon,
                Params = pars,
            };
        }
        catch
        {
            return null;
        }
    }

    private readonly record struct DirPos(uint Cluster, bool Contiguous);

    /// <summary>Diagnostic: "dir/name (len=bytes contig=X)" for root + sce_sys.</summary>
    public static List<string> ListAll(string path)
    {
        var names = new List<string>();
        try
        {
            using var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var v = OpenFs(fs);
            if (v == null)
                return names;
            uint root = ReadRoot(v);
            Collect(v, root, false, "", names);
            var sce = FindDir(v, root, "sce_sys");
            if (sce != null)
                Collect(v, sce.Value.Cluster, sce.Value.Contiguous, "sce_sys/", names);
        }
        catch
        {
        }
        return names;
    }

    private static void Collect(Fs v, uint dir, bool contig, string prefix, List<string> names)
    {
        var recs = ReadDir(v, dir, contig);
        for (int i = 0; i + 1 < recs.Count; i++)
        {
            if (recs[i][0] != 0x85)
                continue;
            int secondary = recs[i][1];
            if (i + 1 >= recs.Count || recs[i + 1][0] != 0xC0)
                continue;
            var stream = recs[i + 1];
            int nameLen = stream[3];
            bool c = (stream[1] & 0x02) != 0;
            uint first = RdU32(stream, 20);
            ulong len = RdU64(stream, 24);
            bool isDir = (recs[i][4] & 0x10) != 0;
            var nb = new StringBuilder();
            for (int k = 0; k < secondary - 1 && i + 2 + k < recs.Count; k++)
            {
                if (recs[i + 2 + k][0] != 0xC1)
                    break;
                for (int j = 0; j < 15 && nb.Length < nameLen; j++)
                    nb.Append((char)(recs[i + 2 + k][2 + j * 2] |
                                      (recs[i + 2 + k][3 + j * 2] << 8)));
            }
            names.Add($"{prefix}{nb} (dir={isDir} len={len} cl={first} contig={c} sec={secondary})");
        }
    }

    private static uint ReadRoot(Fs v)
    {
        Span<byte> vbr = stackalloc byte[512];
        v.F.Position = 0;
        v.F.Read(vbr);
        return RdU32(vbr, 0x60);
    }

    private static Fs? OpenFs(Stream fs)
    {
        var v = new Fs { F = fs };
        Span<byte> vbr = stackalloc byte[512];
        fs.Position = 0;
        if (fs.Read(vbr) != 512)
            return null;
        if (Encoding.ASCII.GetString(vbr.Slice(3, 8)) != "EXFAT   ")
            return null;
        ulong partOff = RdU64(vbr, 0x40);
        uint fatOff = RdU32(vbr, 0x50);
        uint heapOff = RdU32(vbr, 0x58);
        int sectorShift = vbr[0x6C];
        int clusterShift = vbr[0x6D];
        if (sectorShift < 9 || sectorShift > 12 || clusterShift > 25)
            return null;
        v.SectorSize = 1 << sectorShift;
        v.ClusterSectors = 1 << clusterShift;
        v.FatBase = (long)((partOff + fatOff) * (ulong)v.SectorSize);
        v.HeapBase = (long)((partOff + heapOff) * (ulong)v.SectorSize);
        return v;
    }
    private readonly record struct FilePos(uint Cluster, bool Contiguous, ulong Length);

    private static uint RdU32(ReadOnlySpan<byte> b, int o) =>
        (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));

    private static ulong RdU64(ReadOnlySpan<byte> b, int o)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--)
            v = (v << 8) | b[o + i];
        return v;
    }

    private static long ClusterAddr(Fs v, uint cluster) =>
        v.HeapBase + (long)(cluster - 2) * v.ClusterSectors * v.SectorSize;

    private static uint FatNext(Fs v, uint cluster)
    {
        Span<byte> b = stackalloc byte[4];
        v.F.Position = v.FatBase + (long)cluster * 4;
        if (v.F.Read(b) != 4)
            return EndOfChain;
        return (uint)(b[0] | (b[1] << 8) | (b[2] << 16) | (b[3] << 24));
    }

    private static byte[] ReadBytes(Fs v, long addr, int len)
    {
        byte[] b = new byte[len];
        v.F.Position = addr;
        int got = 0;
        while (got < len)
        {
            int n = v.F.Read(b, got, len - got);
            if (n == 0)
                break;
            got += n;
        }
        return got == len ? b : b[..got];
    }

    /// <summary>Read one directory's entries (follows chain, 10k entry cap).</summary>
    private static List<byte[]> ReadDir(Fs v, uint start, bool contiguous)
    {
        var recs = new List<byte[]>();
        uint c = start;
        int clusters = 0;
        long clusterBytes = (long)v.ClusterSectors * v.SectorSize;
        while (true)
        {
            if (++clusters > 4096)
                break;
            byte[] data = ReadBytes(v, ClusterAddr(v, c), (int)Math.Min(clusterBytes, 1024 * 1024));
            for (int o = 0; o + 32 <= data.Length; o += 32)
            {
                byte t = data[o];
                if (t == 0x00)
                    return recs; // end of directory
                if ((t & 0x80) == 0)
                    continue; // deleted
                byte[] r = new byte[32];
                Array.Copy(data, o, r, 0, 32);
                recs.Add(r);
                if (recs.Count > 20000)
                    return recs;
            }
            if (contiguous)
                break;
            uint nx = FatNext(v, c);
            if (nx >= EndOfChain || nx < 2)
            {
                if (nx >= EndOfChain)
                    break;
                // Builder writes clusters contiguously but leaves FAT zeroed:
                // fall through to the next cluster instead of stopping.
                nx = c + 1;
            }
            c = nx;
        }
        return recs;
    }

    private static FilePos? FindFile(Fs v, uint dirCluster, bool dirContig, string name)
    {
        var recs = ReadDir(v, dirCluster, dirContig);
        for (int i = 0; i + 1 < recs.Count; i++)
        {
            if (recs[i][0] != 0x85)
                continue;
            int secondary = recs[i][1];
            if (i + 1 >= recs.Count || recs[i + 1][0] != 0xC0)
                continue;
            var stream = recs[i + 1];
            int nameLen = stream[3];
            bool contig = (stream[1] & 0x02) != 0;
            uint first = RdU32(stream, 20);
            ulong len = RdU64(stream, 24);
            var nb = new StringBuilder();
            for (int k = 0; k < secondary - 1 && i + 2 + k < recs.Count; k++)
            {
                if (recs[i + 2 + k][0] != 0xC1)
                    break;
                for (int j = 0; j < 15 && nb.Length < nameLen; j++)
                    nb.Append((char)(recs[i + 2 + k][2 + j * 2] |
                                      (recs[i + 2 + k][3 + j * 2] << 8)));
            }
            if (string.Equals(nb.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return new FilePos(first, contig, len);
        }
        return null;
    }

    private static DirPos? FindDir(Fs v, uint dirCluster, string name)
    {
        var recs = ReadDir(v, dirCluster, false);
        for (int i = 0; i + 1 < recs.Count; i++)
        {
            if (recs[i][0] != 0x85)
                continue;
            if ((recs[i][4] & 0x10) == 0) // not a directory
                continue;
            int secondary = recs[i][1];
            if (i + 1 >= recs.Count || recs[i + 1][0] != 0xC0)
                continue;
            var stream = recs[i + 1];
            int nameLen = stream[3];
            bool contig = (stream[1] & 0x02) != 0;
            uint first = RdU32(stream, 20);
            var nb = new StringBuilder();
            for (int k = 0; k < secondary - 1 && i + 2 + k < recs.Count; k++)
            {
                if (recs[i + 2 + k][0] != 0xC1)
                    break;
                for (int j = 0; j < 15 && nb.Length < nameLen; j++)
                    nb.Append((char)(recs[i + 2 + k][2 + j * 2] |
                                      (recs[i + 2 + k][3 + j * 2] << 8)));
            }
            if (string.Equals(nb.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return new DirPos(first, contig);
        }
        return null;
    }

    private static byte[] ReadFile(Fs v, FilePos f, int cap)
    {
        long want = (long)Math.Min(f.Length, (ulong)cap);
        if (want <= 0 || f.Cluster < 2)
            return Array.Empty<byte>();
        var out_ = new byte[want];
        long off = 0;
        uint c = f.Cluster;
        long clusterBytes = (long)v.ClusterSectors * v.SectorSize;
        int hops = 0;
        while (off < want)
        {
            if (++hops > 8192)
                return Array.Empty<byte>();
            int take = (int)Math.Min(clusterBytes, want - off);
            byte[] chunk = ReadBytes(v, ClusterAddr(v, c), take);
            if (chunk.Length != take)
                return Array.Empty<byte>();
            Array.Copy(chunk, 0, out_, off, take);
            off += take;
            if (off >= want)
                break;
            if (f.Contiguous)
            {
                c++;
                continue;
            }
            uint nx = FatNext(v, c);
            if (nx >= EndOfChain)
                return Array.Empty<byte>();
            // Zeroed-FAT images (sequential builders): fall through.
            c = nx < 2 ? c + 1 : nx;
        }
        return out_;
    }
}

/// <summary>
/// Header fallback via the pkg-viewer python bridge (pkg_header.py).
/// Runs `python pkg_header.py &lt;path&gt;`, parses its JSON, returns a PkgInfo
/// with real title/ids/icon. Returns null when python or parsing fails —
/// callers then fall back to the filename stub so the item stays sendable.
/// </summary>
public static class PythonHeader
{
    private static string? _bridge;
    private static string? _python;

    public static PkgInfo? TryRead(string path, int timeoutMs = 60000)
    {
        try
        {
            string? bridge = FindBridge();
            string? python = FindPython();
            if (bridge == null || python == null)
                return null;
            string? iconTmp = null;
            try { iconTmp = Path.Combine(Path.GetTempPath(), "pkghdr_" + Guid.NewGuid().ToString("N") + ".png"); }
            catch { iconTmp = null; }
            var psi = new ProcessStartInfo
            {
                FileName = python,
                // Icon goes to a temp file so stdout stays tiny (no pipe pressure).
                // argv as a list: paths with spaces/quotes must never re-parse.
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add(bridge);
            psi.ArgumentList.Add(path);
            if (iconTmp != null)
            {
                psi.ArgumentList.Add("--icon-out");
                psi.ArgumentList.Add(iconTmp);
            }
            using var p = Process.Start(psi);
            if (p == null)
                return null;
            // Drain stdout/stderr concurrently: the bridge prints ~1MB of
            // JSON (base64 icon), which would deadlock WaitForExit on a full
            // pipe if we waited before reading.
            var stdoutTask = System.Threading.Tasks.Task.Run(() => p.StandardOutput.ReadToEnd());
            var stderrTask = System.Threading.Tasks.Task.Run(() => p.StandardError.ReadToEnd());
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(); } catch { }
                return null;
            }
            if (p.ExitCode != 0)
                return null;
            if (!System.Threading.Tasks.Task.WaitAll(new[] { stdoutTask, stderrTask }, 15000))
                return null;
            string json = stdoutTask.Result;
            if (string.IsNullOrWhiteSpace(json))
                return null;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                return null;
            static string Get(JsonElement r, string name)
                => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString() ?? "" : "";
            string title = Get(root, "title");
            if (string.IsNullOrWhiteSpace(title))
                return null;
            string fmt = Get(root, "format");
            if (string.IsNullOrWhiteSpace(fmt))
                fmt = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            long size = 0;
            try { size = new FileInfo(path).Length; } catch { }
            byte[]? icon = null;
            try
            {
                if (iconTmp != null && File.Exists(iconTmp))
                {
                    var raw = File.ReadAllBytes(iconTmp);
                    if (raw.Length > 0 && raw.Length <= 768 * 1024 &&
                        raw.Length >= 8 && raw[0] == 0x89 && raw[1] == 0x50)
                        icon = raw;
                }
                else
                {
                    string b64 = Get(root, "icon_b64");
                    if (!string.IsNullOrEmpty(b64))
                    {
                        var raw = Convert.FromBase64String(b64);
                        if (raw.Length > 0 && raw.Length <= 768 * 1024)
                            icon = raw;
                    }
                }
            }
            catch { }
            finally
            {
                try { if (iconTmp != null && File.Exists(iconTmp)) File.Delete(iconTmp); }
                catch { }
            }
            var pars = new List<PkgParam>();
            string tid = Get(root, "title_id");
            string cid = Get(root, "content_id");
            string ver = Get(root, "version");
            if (!string.IsNullOrEmpty(tid))
                pars.Add(new PkgParam("TITLE_ID", tid));
            if (!string.IsNullOrEmpty(cid))
                pars.Add(new PkgParam("CONTENT_ID", cid));
            if (!string.IsNullOrEmpty(ver))
                pars.Add(new PkgParam("VERSION", ver));
            return new PkgInfo
            {
                Title = title,
                ContentId = cid,
                TitleId = tid,
                Platform = Get(root, "platform"),
                Description = Get(root, "description"),
                PackageSize = size,
                Format = fmt,
                IconData = icon,
                Params = pars,
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? FindBridge()
    {
        if (_bridge != null)
            return _bridge;
        var cands = new List<string>();
        try { cands.Add(Path.Combine(AppContext.BaseDirectory, "pkg_header.py")); } catch { }
        try { cands.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PkgSender", "pkg_header.py")); } catch { }
        if (OperatingSystem.IsWindows())
            cands.Add(@"D:\OpenCode\pkg-sender\library\pkg_header.py");
        foreach (var c in cands)
        {
            try { if (File.Exists(c)) { _bridge = c; return c; } } catch { }
        }
        return null;
    }

    private static string? FindPython()
    {
        if (_python != null)
            return _python;
        // Prefer an interpreter that can actually run the bridge (mkpfs):
        // a bare python without it silently yields bare-ID stubs.
        foreach (var c in new[] { "python", "python3", "py" })
        {
            if (ProbePython(c, "-c \"import mkpfs; print('bridgeready')\"", "bridgeready", 15000))
            {
                _python = c;
                return c;
            }
        }
        foreach (var c in new[] { "python", "python3", "py" })
        {
            if (ProbePython(c, "--version", null, 8000))
            {
                _python = c;
                return c;
            }
        }
        return null;
    }

    private static bool ProbePython(string exe, string args, string? expect, int ms)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p == null)
                return false;
            var outTask = System.Threading.Tasks.Task.Run(() => p.StandardOutput.ReadToEnd());
            if (!p.WaitForExit(ms))
            {
                try { p.Kill(); } catch { }
                return false;
            }
            if (p.ExitCode != 0)
                return false;
            if (expect == null)
                return true;
            if (!System.Threading.Tasks.Task.WaitAll(new[] { outTask }, 5000))
                return false;
            return outTask.Result.Contains(expect);
        }
        catch { return false; }
    }
}
