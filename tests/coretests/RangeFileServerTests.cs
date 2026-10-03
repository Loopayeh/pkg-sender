using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using LoopDPI.Core;
using Xunit;

namespace LoopDPI.Core.Tests;

public class RangeFileServerTests : IDisposable
{
    private readonly string _dir;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public RangeFileServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "pkgsender_rfstest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        _http.Dispose();
    }

    private static readonly string[] UnsafeTitles =
    {
        "Game \"quoted\"",                 // breaks JSON strings when unescaped
        "Back\\slash",                     // breaks JSON escapes
        "Ctrl\x01\x02chars",               // control bytes from pkg SFO data
        "New\r\nLine",                     // was space-substituted, now \uXXXX
        "Tab\tHere",
    };

    [Fact]
    public async Task Catalog_EscapesHostileTitles_StillValidJson()
    {
        string pkg = Path.Combine(_dir, "hostile.pkg");
        await File.WriteAllBytesAsync(pkg, new byte[] { 1, 2, 3, 4 });
        var entries = UnsafeTitles.Select((t, i) => new CatalogEntry
        {
            Id = i.ToString(),
            Title = t,
            TitleId = "PPSA0000" + i,
            Size = 4,
            File = "hostile.pkg",
        }).ToList();
        using var server = new RangeFileServer(new Dictionary<string, string> { ["0"] = pkg }, 19899)
        {
            CatalogProvider = () => entries,
        };
        server.Start();
        try
        {
            string json = await _http.GetStringAsync("http://127.0.0.1:19899/catalog");
            using var doc = JsonDocument.Parse(json); // must parse
            var rows = doc.RootElement.EnumerateArray().ToList();
            Assert.Equal(UnsafeTitles.Length, rows.Count);
            for (int i = 0; i < UnsafeTitles.Length; i++)
                Assert.Equal(UnsafeTitles[i], rows[i].GetProperty("title").GetString());
        }
        finally
        {
            server.Dispose();
        }
    }

    [Fact]
    public async Task Catalog_UnknownPathAndMethod_Rejected()
    {
        string pkg = Path.Combine(_dir, "a.pkg");
        await File.WriteAllBytesAsync(pkg, new byte[] { 1 });
        using var server = new RangeFileServer(new Dictionary<string, string> { ["0"] = pkg }, 19899);
        server.Start();
        try
        {
            Assert.Equal(404, (int)(await _http.GetAsync("http://127.0.0.1:19899/nope")).StatusCode);
            // /pkg/{unknown-id} must not fall through to the registered file
            Assert.Equal(404, (int)(await _http.GetAsync("http://127.0.0.1:19899/pkg/other")).StatusCode);
            var resp = await _http.GetAsync("http://127.0.0.1:19899/pkg/0");
            Assert.Equal(200, (int)resp.StatusCode);
        }
        finally
        {
            server.Dispose();
        }
    }
}
