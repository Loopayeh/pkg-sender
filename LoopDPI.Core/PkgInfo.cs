using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia.Media.Imaging;

namespace LoopDPI.Core;

public sealed record PkgParam(string Name, string Value);

public sealed class PkgInfo
{
    public string Title { get; init; } = "";
    public string ContentId { get; init; } = "";
    public string TitleId { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string Version { get; init; } = "";
    public bool IsDlc { get; init; }
    public string Platform { get; init; } = "";
    public string Description { get; init; } = "";
    public long PackageSize { get; init; }
    public string Format { get; init; } = "pkg";
    public bool IsFolder { get; init; }
    public byte[]? IconData { get; init; }
    public List<PkgParam> Params { get; init; } = new();

    public Bitmap? Cover
    {
        get
        {
            if (IconData == null || IconData.Length == 0)
                return null;
            try
            {
                using var ms = new MemoryStream(IconData);
                return Bitmap.DecodeToHeight(ms, 512);
            }
            catch
            {
                return null;
            }
        }
    }
}

public static class PkgReader
{
    public static PkgInfo? Read(Stream input)
    {
        // PS4 first (param.sfo), PS5 fallback (param.json).
        try
        {
            var info = ReadPs4(input);
            if (info != null && (!string.IsNullOrEmpty(info.ContentId) || !string.IsNullOrEmpty(info.Title)))
                return info;
        }
        catch
        {
        }
        try
        {
            input.Position = 0;
            return ReadPs5(input);
        }
        catch
        {
            return null;
        }
    }

    private static PkgInfo? ReadPs4(Stream input)
    {
        // PS4 packages share the CNT container layout; param.sfo carries
        // TITLE/CONTENT_ID/TITLE_ID/CATEGORY (entry 0x1000, "param.sfo").
        var cnt = CntImage.Open(input);
        if (cnt == null)
            return null;
        var sfoEntry = cnt.Find(0x1000, "param.sfo");
        if (sfoEntry == null)
            return null;
        byte[] sfoBytes = cnt.ReadEntry(sfoEntry.Value, 1024 * 1024);
        if (sfoBytes.Length == 0)
            return null;
        Dictionary<string, SfoValue> sfo;
        try
        {
            sfo = SfoReader.Parse(sfoBytes);
        }
        catch
        {
            return null;
        }
        string Str(string name) => sfo.TryGetValue(name, out var v) ? v.Text : "";
        string title = Str("TITLE");
        string contentId = Str("CONTENT_ID");
        if (string.IsNullOrEmpty(title) && string.IsNullOrEmpty(contentId))
            return null;
        string titleId = Str("TITLE_ID");
        string category = Str("CATEGORY");
        string version = Str("APP_VER");
        if (string.IsNullOrEmpty(version))
            version = Str("VERSION");

        var pars = new List<PkgParam>();
        foreach (var kv in sfo)
        {
            string v = kv.Value.IsInt
                ? kv.Value.Int.ToString("X8")
                : kv.Value.Text;
            if (!string.IsNullOrWhiteSpace(v))
                pars.Add(new PkgParam(kv.Key, v));
        }

        byte[]? icon = FindIcon(cnt);

        return new PkgInfo
        {
            Title = title,
            ContentId = contentId,
            TitleId = titleId,
            ContentType = category,
            Version = version,
            IsDlc = category.Equals("ac", StringComparison.OrdinalIgnoreCase),
            Platform = "PS4",
            Description = $"[PS4] {title}",
            PackageSize = input.Length,
            IconData = icon,
            Params = pars,
        };
    }

    // Minimal PS5 CNT/FIH reader: header + entry table + param.json + icon.
    private static PkgInfo? ReadPs5(Stream input)
    {
        var cnt = CntImage.Open(input);
        if (cnt == null || string.IsNullOrWhiteSpace(cnt.ContentId))
            return null;
        string contentId = cnt.ContentId;

        var pj = cnt.Find(0x2000, "param.json");
        if (pj == null)
            return null;
        var pjb = pj.Value;

        string title = "", version = "";
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(cnt.ReadEntry(pjb, 2 * 1024 * 1024));
                var root = doc.RootElement;
                version = root.TryGetProperty("contentVersion", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() ?? "" : "";
                if (root.TryGetProperty("localizedParameters", out var lp) && lp.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    string lang = "en-US";
                    if (lp.TryGetProperty("defaultLanguage", out var dl) && dl.ValueKind == System.Text.Json.JsonValueKind.String)
                        lang = dl.GetString() ?? lang;
                    if (lp.TryGetProperty(lang, out var le) && le.ValueKind == System.Text.Json.JsonValueKind.Object &&
                        le.TryGetProperty("titleName", out var tn) && tn.ValueKind == System.Text.Json.JsonValueKind.String)
                        title = tn.GetString() ?? "";
                    if (string.IsNullOrEmpty(title))
                    {
                        foreach (var prop in lp.EnumerateObject())
                        {
                            if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.Object &&
                                prop.Value.TryGetProperty("titleName", out var t2) && t2.ValueKind == System.Text.Json.JsonValueKind.String)
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
        }

        byte[] iconBytes = FindIcon(cnt) ?? Array.Empty<byte>();

        string titleId = "";
        {
            var dash = contentId.Split('-');
            string mid = dash.Length >= 2 ? dash[1] : contentId;
            int us = mid.IndexOf('_');
            if (us > 0)
                mid = mid[..us];
            if (mid.Length is >= 4 and <= 16)
                titleId = mid;
        }
        if (string.IsNullOrEmpty(title))
            title = string.IsNullOrEmpty(titleId) ? contentId : titleId;

        var pars = new List<PkgParam> { new("CONTENT_ID", contentId) };
        if (!string.IsNullOrEmpty(titleId))
            pars.Add(new("TITLE_ID", titleId));
        if (!string.IsNullOrEmpty(version))
            pars.Add(new("VERSION", version));
        pars.Add(new("PLATFORM", "PS5"));

        return new PkgInfo
        {
            Title = title,
            ContentId = contentId,
            TitleId = titleId,
            Platform = "PS5",
            Version = version,
            IsDlc = IsDlcHeuristic(title, contentId),
            Description = $"[PS5 {(cnt.IsMeta ? "Meta" : cnt.IsDebug ? "Debug" : "Retail")}] {title}",
            PackageSize = input.Length,
            IconData = iconBytes.Length > 0 ? iconBytes : null,
            Params = pars,
        };
    }

    /// <summary>Generic CNT container walker (shared PS4/PS5 layout).</summary>
    private sealed class CntImage
    {
        public long CntBase;
        public bool IsDebug;
        public bool IsMeta;
        public string ContentId = "";
        public List<(uint Id, string Name, uint Flags, uint DataOff, uint DataSize)> Entries = new();
        private readonly Stream _input;

        private CntImage(Stream input)
        {
            _input = input;
        }

        public static CntImage? Open(Stream input)
        {
            try
            {
                if (!input.CanSeek || input.Length < 0x5A0)
                    return null;
                Span<byte> magic = stackalloc byte[4];
                input.Position = 0;
                if (input.Read(magic) != 4)
                    return null;
                long cntBase;
                bool isDebug, isMeta;
                if (magic[0] == 0x7F && magic[1] == (byte)'F' && magic[2] == (byte)'I' && magic[3] == (byte)'H')
                {
                    Span<byte> fih = stackalloc byte[0x60];
                    input.Position = 0;
                    if (input.Read(fih) != fih.Length)
                        return null;
                    isDebug = fih[0x05] == 0x00;
                    isMeta = false;
                    ulong emb = ReadU64LE(fih.Slice(0x58, 8));
                    if (emb == 0 || emb > (ulong)input.Length - 0x5A0)
                        return null;
                    cntBase = (long)emb;
                }
                else if (magic[0] == 0x7F && magic[1] == (byte)'C' && magic[2] == (byte)'N' && magic[3] == (byte)'T')
                {
                    cntBase = 0;
                    isDebug = true;
                    isMeta = true;
                }
                else
                {
                    return null;
                }
                byte[] hdr = new byte[0x5A0];
                input.Position = cntBase;
                input.ReadExactly(hdr);
                if (hdr[0] != 0x7F || hdr[1] != (byte)'C' || hdr[2] != (byte)'N' || hdr[3] != (byte)'T')
                    return null;
                uint count = ReadU32BE(hdr, 0x10);
                uint tableOff = ReadU32BE(hdr, 0x18);
                if (count == 0 || count > 0x10000)
                    return null;
                byte[] table = new byte[(long)count * 0x20];
                input.Position = cntBase + tableOff;
                input.ReadExactly(table);

                var raw = new List<(uint Id, uint NameOff, uint Flags, uint DataOff, uint DataSize)>();
                for (int i = 0; i < count; i++)
                {
                    int o = i * 0x20;
                    raw.Add((ReadU32BE(table, o), ReadU32BE(table, o + 4), ReadU32BE(table, o + 8),
                        ReadU32BE(table, o + 0x10), ReadU32BE(table, o + 0x14)));
                }
                var names = new Dictionary<uint, string>();
                var nt = raw.FirstOrDefault(e => e.Id == 0x0200);
                if (nt.DataSize > 0 && nt.DataSize <= 4 * 1024 * 1024 && (nt.Flags & 0x80000000) == 0)
                {
                    byte[] nb = new byte[nt.DataSize];
                    input.Position = cntBase + nt.DataOff;
                    if (input.Read(nb, 0, nb.Length) == nb.Length)
                    {
                        int s = 0;
                        for (int i = 0; i <= nb.Length; i++)
                        {
                            if (i == nb.Length || nb[i] == 0)
                            {
                                if (i > s)
                                    names[(uint)s] = Encoding.ASCII.GetString(nb, s, i - s);
                                s = i + 1;
                            }
                        }
                    }
                }
                var img = new CntImage(input)
                {
                    CntBase = cntBase,
                    IsDebug = isDebug,
                    IsMeta = isMeta,
                    ContentId = ReadAscii(hdr, 0x40, 0x30),
                };
                foreach (var e in raw)
                    img.Entries.Add((e.Id, names.TryGetValue(e.NameOff, out var n) ? n : "", e.Flags, e.DataOff, e.DataSize));
                return img;
            }
            catch
            {
                return null;
            }
        }

        public (uint Id, string Name, uint Flags, uint DataOff, uint DataSize)? Find(uint id, string name)
        {
            foreach (var e in Entries)
            {
                if (e.Id == id && (e.Flags & 0x80000000) == 0)
                    return e;
            }
            foreach (var e in Entries)
            {
                if ((e.Flags & 0x80000000) == 0 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase))
                    return e;
            }
            return null;
        }

        public byte[] ReadEntry((uint Id, string Name, uint Flags, uint DataOff, uint DataSize) e, int cap)
        {
            try
            {
                if (e.DataSize == 0 || e.DataSize > cap || (e.Flags & 0x80000000) != 0)
                    return Array.Empty<byte>();
                byte[] b = new byte[e.DataSize];
                _input.Position = CntBase + e.DataOff;
                return _input.Read(b, 0, b.Length) == b.Length ? b : Array.Empty<byte>();
            }
            catch
            {
                return Array.Empty<byte>();
            }
        }
    }

    /// <summary>
    /// Cover search: exact icon id, then icon variant ids. No fuzzy
    /// *icon*.png fallback: patch/DLC packages often bundle a generic or
    /// unrelated icon, which used to land on the wrong card in family view.
    /// No exact icon -> no cover (UI shows a placeholder tile instead).
    /// </summary>
    private static byte[]? FindIcon(CntImage cnt)
    {
        byte[]? Try((uint Id, string Name, uint Flags, uint DataOff, uint DataSize) e)
        {
            byte[] b = cnt.ReadEntry(e, 8 * 1024 * 1024);
            if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50)
                return b;
            return null;
        }
        foreach (var e in cnt.Entries)
        {
            if (e.Id == 0x1200 && (e.Flags & 0x80000000) == 0)
            {
                var hit = Try(e);
                if (hit != null)
                    return hit;
            }
        }
        foreach (var e in cnt.Entries)
        {
            if (e.Id is >= 0x1201 and <= 0x1220 && (e.Flags & 0x80000000) == 0)
            {
                var hit = Try(e);
                if (hit != null)
                    return hit;
            }
        }
        return null;
    }

    private sealed record SfoValue(bool IsInt, string Text, uint Int);

    /// <summary>Minimal param.sfo parser (magic \0PSF; utf8/int entries).</summary>
    private static class SfoReader
    {
        public static Dictionary<string, SfoValue> Parse(byte[] b)
        {
            var dict = new Dictionary<string, SfoValue>(StringComparer.Ordinal);
            if (b.Length < 0x14 || b[0] != 0 || b[1] != (byte)'P' || b[2] != (byte)'S' || b[3] != (byte)'F')
                throw new InvalidDataException("not SFO");
            uint keyTab = Ru32(b, 8), dataTab = Ru32(b, 12);
            int n = (int)Ru32(b, 16);
            if (n < 0 || n > 4096)
                throw new InvalidDataException("bad SFO count");
            for (int k = 0; k < n; k++)
            {
                int o = 0x14 + k * 0x10;
                if (o + 16 > b.Length)
                    throw new InvalidDataException("bad SFO entry");
                ushort keyOff = (ushort)(b[o] | (b[o + 1] << 8));
                ushort fmt = (ushort)(b[o + 2] | (b[o + 3] << 8));
                int len = b[o + 4] | (b[o + 5] << 8) | (b[o + 6] << 16) | (b[o + 7] << 24);
                uint dataOff = (uint)(b[o + 12] | (b[o + 13] << 8) | (b[o + 14] << 16) | (b[o + 15] << 24));
                string name = CStr(b, (int)(keyTab + keyOff));
                if (fmt == 0x404)
                {
                    uint v = Ru32(b, (int)(dataTab + dataOff));
                    dict[name] = new SfoValue(true, "", v);
                }
                else if (fmt == 0x204 || fmt == 0x4)
                {
                    int slen = fmt == 0x204 ? Math.Max(0, len - 1) : Math.Max(0, len);
                    int start = (int)(dataTab + dataOff);
                    if (start < 0 || start + slen > b.Length)
                        throw new InvalidDataException("bad SFO string");
                    string s = Encoding.UTF8.GetString(b, start, slen).Trim('\x0');
                    dict[name] = new SfoValue(false, s, 0);
                }
                else
                {
                    throw new InvalidDataException($"unknown SFO type {fmt:X4}");
                }
            }
            return dict;
        }

        private static uint Ru32(byte[] b, int o)
        {
            if (o < 0 || o + 4 > b.Length)
                throw new InvalidDataException("SFO OOB");
            return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
        }

        private static string CStr(byte[] b, int o)
        {
            if (o < 0 || o >= b.Length)
                throw new InvalidDataException("SFO OOB");
            int e = o;
            while (e < b.Length && b[e] != 0)
                e++;
            return Encoding.ASCII.GetString(b, o, e - o);
        }
    }

    /// <summary>
    /// PS5 pkgs carry no DLC category flag; match the pkgviewer heuristic
    /// (DLC/add-on/expansion/season-pass in title or content id).
    /// </summary>
    private static bool IsDlcHeuristic(string title, string contentId)
    {
        string t = (title ?? "").ToLowerInvariant();
        string c = (contentId ?? "").ToLowerInvariant();
        return t.Contains("dlc") || t.Contains("add-on") || t.Contains("addon") ||
               t.Contains("expansion") || t.Contains("season pass") ||
               c.Contains("-dlc") || c.Contains("_dlc") || c.Contains("addon");
    }

    private static uint ReadU32BE(byte[] b, int o)
        => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

    private static ulong ReadU64LE(ReadOnlySpan<byte> s)
    {
        ulong v = 0;
        for (int i = 7; i >= 0; i--)
            v = (v << 8) | s[i];
        return v;
    }

    private static string ReadAscii(byte[] b, int o, int n)
    {
        int len = n;
        for (int i = 0; i < n; i++)
        {
            if (b[o + i] == 0)
            {
                len = i;
                break;
            }
        }
        return Encoding.ASCII.GetString(b, o, len).Trim();
    }

}
