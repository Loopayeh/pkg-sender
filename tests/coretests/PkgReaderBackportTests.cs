using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using LoopDPI.Core;
using Xunit;

namespace LoopDPI.Core.Tests;

/// <summary>
/// Backport PKG parsing (issue #16): LIH-wrapped PS5 backports and
/// PS4 scene repacks whose param entries carry the encrypted flag
/// (plain bytes in practice) must parse like any other package.
/// Fixtures are synthetic CNT/FIH/LIH containers built in-memory.
/// </summary>
public class PkgReaderBackportTests
{
    // ---------------- fixture builders ----------------

    private static void W16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
    }

    private static void W32LE(byte[] b, int o, uint v)
    {
        b[o] = (byte)v;
        b[o + 1] = (byte)(v >> 8);
        b[o + 2] = (byte)(v >> 16);
        b[o + 3] = (byte)(v >> 24);
    }

    private static void W32BE(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24);
        b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8);
        b[o + 3] = (byte)v;
    }

    private static void W64LE(byte[] b, int o, ulong v)
    {
        for (int i = 0; i < 8; i++)
            b[o + i] = (byte)(v >> (8 * i));
    }

    /// <summary>Minimal param.sfo (utf-8 string entries only).</summary>
    private static byte[] BuildSfo(params (string Key, string Value)[] items)
    {
        int count = items.Length;
        int entryEnd = 0x14 + count * 0x10;
        var keyBytes = new List<byte>();
        var keyOff = new List<int>();
        foreach (var (k, _) in items)
        {
            keyOff.Add(keyBytes.Count);
            keyBytes.AddRange(Encoding.ASCII.GetBytes(k));
            keyBytes.Add(0);
        }
        while (keyBytes.Count % 4 != 0) keyBytes.Add(0);
        int keyTab = entryEnd;
        int dataTab = keyTab + keyBytes.Count;
        var dataBytes = new List<byte>();
        var dataOff = new List<int>();
        foreach (var (_, v) in items)
        {
            dataOff.Add(dataBytes.Count);
            dataBytes.AddRange(Encoding.UTF8.GetBytes(v));
            dataBytes.Add(0);
            while (dataBytes.Count % 4 != 0) dataBytes.Add(0);
        }
        byte[] sfo = new byte[dataTab + dataBytes.Count];
        sfo[1] = (byte)'P';
        sfo[2] = (byte)'S';
        sfo[3] = (byte)'F';
        W32LE(sfo, 4, 0x101);
        W32LE(sfo, 8, (uint)keyTab);
        W32LE(sfo, 12, (uint)dataTab);
        W32LE(sfo, 16, (uint)count);
        for (int i = 0; i < count; i++)
        {
            int o = 0x14 + i * 0x10;
            W16(sfo, o, (ushort)keyOff[i]);
            W16(sfo, o + 2, 0x0204);
            uint len = (uint)Encoding.UTF8.GetByteCount(items[i].Value) + 1;
            W32LE(sfo, o + 4, len);
            W32LE(sfo, o + 8, len);
            W32LE(sfo, o + 12, (uint)dataOff[i]);
        }
        keyBytes.CopyTo(sfo, keyTab);
        dataBytes.CopyTo(sfo, dataTab);
        return sfo;
    }

    /// <summary>Fake icon: passes the PNG magic sniff (decode fails softly).</summary>
    private static byte[] FakePng() => new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3, 4 };

    private sealed record CntEntry(uint Id, string Name, uint Flags, byte[] Data);

    /// <summary>
    /// Build a CNT container: header (0x5A0) + entry table + name table +
    /// entry data. Offsets are CNT-relative, exactly what PkgReader expects.
    /// </summary>
    private static byte[] BuildCnt(string contentId, params CntEntry[] entries)
    {
        const int headerSize = 0x5A0;
        int count = entries.Length + 1; // + name table entry
        int tableOff = headerSize;
        int tableSize = count * 0x20;
        // name table blob: "name\0name\0..."
        var nt = new List<byte>();
        var nameOff = new List<int>();
        foreach (var e in entries)
        {
            nameOff.Add(nt.Count);
            nt.AddRange(Encoding.ASCII.GetBytes(e.Name));
            nt.Add(0);
        }
        int ntOff = tableOff + tableSize;
        int dataStart = ntOff + nt.Count;
        var data = new List<byte>();
        var dataOff = new List<uint>();
        foreach (var e in entries)
        {
            dataOff.Add((uint)data.Count);
            data.AddRange(e.Data);
            while (data.Count % 16 != 0) data.Add(0);
        }
        int total = dataStart + data.Count;
        if (total < 0xFE0 + 32) total = 0xFE0 + 32; // digest area
        byte[] pkg = new byte[total];

        pkg[0] = 0x7F;
        pkg[1] = (byte)'C';
        pkg[2] = (byte)'N';
        pkg[3] = (byte)'T';
        W32BE(pkg, 0x10, (uint)count);
        W32BE(pkg, 0x18, (uint)tableOff);
        byte[] cid = Encoding.ASCII.GetBytes(contentId);
        Array.Copy(cid, 0, pkg, 0x40, Math.Min(cid.Length, 0x30));

        int o = tableOff;
        // name table entry first (id 0x0200)
        W32BE(pkg, o, 0x0200);
        W32BE(pkg, o + 4, 0);
        W32BE(pkg, o + 8, 0);
        W32BE(pkg, o + 0x10, (uint)ntOff);
        W32BE(pkg, o + 0x14, (uint)nt.Count);
        o += 0x20;
        for (int i = 0; i < entries.Length; i++)
        {
            W32BE(pkg, o, entries[i].Id);
            W32BE(pkg, o + 4, (uint)nameOff[i]);
            W32BE(pkg, o + 8, entries[i].Flags);
            W32BE(pkg, o + 0x10, (uint)(dataStart + dataOff[i]));
            W32BE(pkg, o + 0x14, (uint)entries[i].Data.Length);
            o += 0x20;
        }
        nt.CopyTo(pkg, ntOff);
        data.CopyTo(pkg, dataStart);
        for (int i = 0; i < 32; i++) pkg[0xFE0 + i] = (byte)(i + 1); // digest
        return pkg;
    }

    /// <summary>Wrap a CNT in a FIH or LIH outer header.</summary>
    private static byte[] WrapInHeader(string magic, long embOffsetField, byte[] cnt)
    {
        long emb = 0x1000;
        byte[] pkg = new byte[emb + cnt.Length];
        byte[] mg = Encoding.ASCII.GetBytes(magic);
        pkg[0] = 0x7F;
        pkg[1] = mg[0];
        pkg[2] = mg[1];
        pkg[3] = mg[2];
        pkg[5] = 0x00; // debug/fake issuer
        W64LE(pkg, (int)embOffsetField, (ulong)emb);
        Array.Copy(cnt, 0, pkg, emb, cnt.Length);
        return pkg;
    }

    private static byte[] Ps4GameCnt(uint sfoFlags = 0, uint iconFlags = 0, string category = "gd") =>
        BuildCnt("UP0006-CUSA00419_00-FIFA2017FOOTBALL",
            new CntEntry(0x1000, "param.sfo", sfoFlags, BuildSfo(
                ("TITLE", "FIFA 17"),
                ("CONTENT_ID", "UP0006-CUSA00419_00-FIFA2017FOOTBALL"),
                ("TITLE_ID", "CUSA00419"),
                ("CATEGORY", category),
                ("APP_VER", "01.00"))),
            new CntEntry(0x1200, "icon0.png", iconFlags, FakePng()));

    private static byte[] Ps5Cnt(uint jsonFlags = 0, uint iconFlags = 0, string titleId = "PPSA01352") =>
        BuildCnt("UP0006-PPSA01352_00-FIFA23PS5GAME00",
            new CntEntry(0x2000, "param.json", jsonFlags, Encoding.UTF8.GetBytes(
                "{\"titleId\":\"" + titleId + "\",\"contentId\":\"UP0006-PPSA01352_00-FIFA23PS5GAME00\"," +
                "\"contentVersion\":\"01.000.000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\"," +
                "\"en-US\":{\"titleName\":\"FIFA 23\"}}}")),
            new CntEntry(0x1200, "icon0.png", iconFlags, FakePng()));

    // ---------------- PS4 (CNT) ----------------

    [Fact]
    public void Ps4RetailCnt_Parses()
    {
        using var ms = new MemoryStream(Ps4GameCnt());
        var info = PkgReader.Read(ms);
        Assert.NotNull(info);
        Assert.Equal("FIFA 17", info!.Title);
        Assert.Equal("CUSA00419", info.TitleId);
        Assert.Equal("PS4", info.Platform);
        Assert.Equal("01.00", info.Version);
    }

    [Fact]
    public void Ps4BackportFlaggedSfo_ParsesViaFallback()
    {
        // Scene backport repack: param.sfo entry carries the encrypted
        // flag while the bytes stay plain (as pkg-viewer observes).
        using var ms = new MemoryStream(Ps4GameCnt(sfoFlags: 0x80000000, category: "gp"));
        var info = PkgReader.Read(ms);
        Assert.NotNull(info);
        Assert.Equal("FIFA 17", info!.Title);
        Assert.Equal("gp", info.ContentType); // update/backport
    }

    [Fact]
    public void Ps4FlaggedIcon_StillFound()
    {
        using var ms = new MemoryStream(Ps4GameCnt(iconFlags: 0x80000000));
        var info = PkgReader.Read(ms);
        Assert.NotNull(info);
        Assert.NotNull(info!.IconData);
        Assert.True(info.IconData.Length >= 8);
    }

    // ---------------- PS5 (FIH retail) ----------------

    [Fact]
    public void Ps5FihRetail_Parses()
    {
        using var ms = new MemoryStream(WrapInHeader("FIH", 0x58, Ps5Cnt()));
        var info = PkgReader.Read(ms);
        Assert.NotNull(info);
        Assert.Equal("FIFA 23", info!.Title);
        Assert.Equal("PS5", info.Platform);
        Assert.Equal("PPSA01352", info.TitleId);
        Assert.DoesNotContain("Backport", info.Description);
    }

    // ---------------- PS5 (LIH backport) — the issue #16 case ----------------

    [Fact]
    public void Ps5LihBackport_Parses()
    {
        using var ms = new MemoryStream(WrapInHeader("LIH", 0x30, Ps5Cnt()));
        var info = PkgReader.Read(ms);
        Assert.NotNull(info);
        Assert.Equal("FIFA 23", info!.Title);
        Assert.Equal("PPSA01352", info.TitleId);
        Assert.Equal("PS5", info.Platform);
        Assert.Contains("Backport", info.Description);
        Assert.Contains(info.Params, p => p.Name == "PACKAGE" && p.Value == "Backport (LIH)");
    }

    [Fact]
    public void Ps5LihBackport_FlaggedParamJson_Parses()
    {
        // Backport wrapper AND flagged param.json entry: both fallbacks.
        using var ms = new MemoryStream(WrapInHeader("LIH", 0x30, Ps5Cnt(jsonFlags: 0x80000000)));
        var info = PkgReader.Read(ms);
        Assert.NotNull(info);
        Assert.Equal("FIFA 23", info!.Title);
        Assert.Contains("Backport", info.Description);
    }

    [Fact]
    public void Ps5LihBackport_DlcHeuristic_StillWorks()
    {
        byte[] cnt = BuildCnt("UP0006-PPSA01352_00-FIFA23PS5DLCKIT1",
            new CntEntry(0x2000, "param.json", 0, Encoding.UTF8.GetBytes(
                "{\"titleId\":\"PPSA01352\",\"contentId\":\"UP0006-PPSA01352_00-FIFA23PS5DLCKIT1\"," +
                "\"localizedParameters\":{\"en-US\":{\"titleName\":\"FIFA 23 DLC Kit 1\"}}}")));
        using var ms = new MemoryStream(WrapInHeader("LIH", 0x30, cnt));
        var info = PkgReader.Read(ms);
        Assert.NotNull(info);
        Assert.True(info!.IsDlc);
    }

    // ---------------- negative cases ----------------

    [Fact]
    public void LihWrapper_EmbeddedOffsetZero_Rejected()
    {
        byte[] pkg = WrapInHeader("LIH", 0x30, Ps5Cnt());
        W64LE(pkg, 0x30, 0); // null embedded offset
        using var ms = new MemoryStream(pkg);
        Assert.Null(PkgReader.Read(ms));
    }

    [Fact]
    public void LihWrapper_NonCntPayload_Rejected()
    {
        byte[] pkg = new byte[0x2000];
        pkg[0] = 0x7F;
        pkg[1] = (byte)'L';
        pkg[2] = (byte)'I';
        pkg[3] = (byte)'H';
        W64LE(pkg, 0x30, 0x1000); // points at zeros, not a CNT
        using var ms = new MemoryStream(pkg);
        Assert.Null(PkgReader.Read(ms));
    }

    [Fact]
    public void GarbageStream_StillRejected()
    {
        using var ms = new MemoryStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Null(PkgReader.Read(ms));
    }
}
