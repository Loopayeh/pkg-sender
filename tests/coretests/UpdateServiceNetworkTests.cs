using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using LoopDPI.Core;
using Xunit;

namespace LoopDPI.Core.Tests;

public class UpdateServiceNetworkTests : IDisposable
{
    private const string ReleaseJson = """
        {
          "tag_name": "v1.2.8",
          "name": "PKG Sender v1.2.8",
          "body": "Linux support.\nMore fixes.",
          "assets": [
            {"name": "PkgSender-Setup-1.2.8.exe", "browser_download_url": "https://example.invalid/setup.exe", "size": 10},
            {"name": "PkgSender-1.2.8-linux-x64.tar.gz", "browser_download_url": "https://example.invalid/linux.tar.gz", "size": 20},
            {"name": "pkg-receiver.elf", "browser_download_url": "https://example.invalid/elf", "size": 1}
          ]
        }
        """;

    private readonly TestHttpServer _server;
    private readonly string? _oldApi;

    public UpdateServiceNetworkTests()
    {
        _server = new TestHttpServer(path => path == "/releases/latest"
            ? TestHttpServer.Json(ReleaseJson)
            : (404, "{}"));
        // FetchLatestAsync reads this env var; restore it after the test.
        _oldApi = Environment.GetEnvironmentVariable("PKGSENDER_UPDATE_API");
        Environment.SetEnvironmentVariable("PKGSENDER_UPDATE_API", _server.BaseUrl + "/releases/latest");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PKGSENDER_UPDATE_API", _oldApi);
        _server.Dispose();
    }

    [Fact]
    public async Task FetchLatestAsync_ParsesReleaseFromApi()
    {
        var info = await UpdateService.FetchLatestAsync();
        Assert.NotNull(info);
        Assert.Equal("v1.2.8", info!.Tag);
        Assert.Equal("PKG Sender v1.2.8", info.Name);
        Assert.Contains("Linux support.", info.Body);
        Assert.Equal(3, info.Assets.Count);
        var linux = info.Assets.Single(a => a.Name.Contains("linux"));
        Assert.Equal("https://example.invalid/linux.tar.gz", linux.Url);
        Assert.Equal(20, linux.Size);
    }

    [Fact]
    public async Task FetchLatestAsync_NullOnUnknownEndpoint()
    {
        Environment.SetEnvironmentVariable("PKGSENDER_UPDATE_API", _server.BaseUrl + "/nope");
        Assert.Null(await UpdateService.FetchLatestAsync());
    }

    [Fact]
    public async Task FetchLatestAsync_NullOnDeadServer()
    {
        // Port 1 on loopback is never listening in the test sandbox.
        Environment.SetEnvironmentVariable("PKGSENDER_UPDATE_API", "http://127.0.0.1:1/releases/latest");
        Assert.Null(await UpdateService.FetchLatestAsync());
    }

    [Fact]
    public async Task DownloadAsync_WritesBytesAndReportsProgress()
    {
        byte[] want = new byte[512 * 1024];
        new Random(7).NextBytes(want);
        using var server = new TestHttpServer(_ => (200, Convert.ToBase64String(want)));
        // The canned server speaks JSON-shaped bytes; DownloadAsync just streams
        // whatever arrives, so decode on both sides for byte-exact comparison.
        string dest = Path.Combine(Path.GetTempPath(), "pkgsender_test_" + Guid.NewGuid().ToString("N"));
        try
        {
            long lastGot = -1, lastTotal = -1;
            await UpdateService.DownloadAsync(server.BaseUrl + "/file", dest, (got, total) =>
            {
                lastGot = got; lastTotal = total;
            });
            byte[] got64 = File.ReadAllBytes(dest);
            byte[] got = Convert.FromBase64String(Encoding.UTF8.GetString(got64));
            Assert.Equal(want, got);
            Assert.Equal(got64.Length, lastGot);
            Assert.Equal(got64.Length, lastTotal);
        }
        finally
        {
            File.Delete(dest);
        }
    }
}
