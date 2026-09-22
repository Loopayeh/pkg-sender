using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Widget;
using LoopDPI.Core;

namespace PkgSender.Droid;

[Activity(Label = "PKG Sender • by Loopayeh", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    const int PickReq = 1001;
    const int ServerPort = 9898;

    EditText? _psIp;
    TextView? _fileLabel;
    TextView? _status;
    Button? _sendBtn;
    string? _localPath;
    RangeFileServer? _server;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        var lay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = (int)(16 * Resources!.DisplayMetrics!.Density);
        lay.SetPadding(pad, pad, pad, pad);

        _psIp = new EditText(this) { Hint = "Console IP, e.g. 192.168.1.105" };
        string? saved = GetPreferences(FileCreationMode.Private).GetString("psip", null);
        if (!string.IsNullOrEmpty(saved)) _psIp.Text = saved;
        lay.AddView(_psIp);

        var pick = new Button(this) { Text = "Pick .pkg file" };
        pick.Click += (_, _) =>
        {
            var i = new Intent(Intent.ActionOpenDocument);
            i.AddCategory(Intent.CategoryOpenable);
            i.SetType("*/*");
            StartActivityForResult(Intent.CreateChooser(i, "Pick PKG"), PickReq);
        };
        lay.AddView(pick);

        _fileLabel = new TextView(this) { Text = "no file picked" };
        lay.AddView(_fileLabel);

        _sendBtn = new Button(this) { Text = "Send to console" };
        _sendBtn.Click += (_, _) => _ = SendAsync();
        lay.AddView(_sendBtn);

        var testBtn = new Button(this) { Text = "Test connection" };
        testBtn.Click += (_, _) => _ = TestAsync();
        lay.AddView(testBtn);

        _status = new TextView(this) { Text = "idle" };
        lay.AddView(_status);

        SetContentView(lay);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickReq || resultCode != Result.Ok || data?.Data == null) return;
        try
        {
            var uri = data.Data!;
            string name = "game.pkg";
            try
            {
                using var c = ContentResolver!.Query(uri, null, null, null, null);
                if (c != null && c.MoveToFirst())
                {
                    int idx = c.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                    if (idx >= 0) name = c.GetString(idx) ?? name;
                }
            }
            catch { }
            string dest = Path.Combine(CacheDir!.AbsolutePath, name);
            using (var src = ContentResolver!.OpenInputStream(uri)!)
            using (var dst = File.Create(dest))
                src.CopyTo(dst);
            _localPath = dest;
            RunOnUiThread(() => _fileLabel!.Text = $"{name} ({new FileInfo(dest).Length / 1048576} MB)");
        }
        catch (Exception ex)
        {
            RunOnUiThread(() => _status!.Text = "pick failed: " + ex.Message);
        }
    }

    async Task SendAsync()
    {
        string psIp = (_psIp?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(psIp)) { Say("type the console IP first"); return; }
        if (string.IsNullOrEmpty(_localPath) || !File.Exists(_localPath)) { Say("pick a .pkg first"); return; }
        GetPreferences(FileCreationMode.Private).Edit().PutString("psip", psIp).Apply();

        _sendBtn!.Enabled = false;
        try
        {
            Say("finding PC address…");
            var nets = await Task.Run(() => NetDiscovery.GetLanNetworks());
            string? pcIp = NetDiscovery.BestPcIpFor(nets, psIp) ?? "0.0.0.0";
            Say($"PC={pcIp} starting server…");

            _server?.Dispose();
            var files = new Dictionary<string, string> { ["pkg"] = _localPath };
            _server = new RangeFileServer(files, ServerPort);
            _server.Start();
            string url = _server.UrlFor(pcIp, "pkg");

            Say($"pushing to {psIp} (RPI)…");
            string title = Path.GetFileName(_localPath);
            PkgInfo? pkg = null;
            try
            {
                using var fs = File.Open(_localPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                pkg = PkgReader.Read(fs);
                if (pkg != null && !string.IsNullOrWhiteSpace(pkg.Title)) title = pkg.Title;
            }
            catch { }
            bool isPs4 = (pkg?.Platform ?? "").StartsWith("PS4");

            var (ok, reply) = await Ps4Installer.PushRpiAsync(psIp, url, title);
            string method = "rpi";
            if (!ok && isPs4 && pkg != null)
            {
                Say("RPI silent — trying GoldHEN…");
                _server.RegisterManifest("pkg", Ps4Installer.BuildManifest(url, pkg.PackageSize, pkg.Digest));
                var g = await Ps4Installer.PushGoldHenAsync(psIp, pcIp, _server.ManifestUrlFor(pcIp, "pkg"), pkg, ServerPort);
                ok = g.Ok; reply = g.Reply; method = "goldhen";
            }
            Say(ok ? $"sent via {method} — watch the console." : $"failed ({method}): {Short(reply)}");
        }
        catch (Exception ex) { Say("error: " + Short(ex.Message)); }
        finally { _sendBtn.Enabled = true; }
    }

    void Say(string s) => RunOnUiThread(() => _status!.Text = s);
    static string Short(string s) => s.Length > 140 ? s[..140] : s;

    async Task TestAsync()
    {
        string psIp = (_psIp?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(psIp)) { Say("type the console IP first"); return; }
        GetPreferences(FileCreationMode.Private).Edit().PutString("psip", psIp).Apply();
        try
        {
            Say("probing console (12800/9090)…");
            string mode = await Ps4Installer.DetectAsync(psIp);
            var nets = await Task.Run(() => NetDiscovery.GetLanNetworks());
            string pcIp = NetDiscovery.BestPcIpFor(nets, psIp) ?? "0.0.0.0";
            if (mode == "offline")
            {
                Say($"console OFFLINE on {psIp} — enable RPI or GoldHEN Server. phone={pcIp} (same Wi-Fi?)");
                return;
            }
            // prove the phone can actually serve: start the real file
            // server and fetch back over loopback, then free the port.
            string serve;
            RangeFileServer? probe = null;
            try
            {
                if (!string.IsNullOrEmpty(_localPath) && File.Exists(_localPath))
                {
                    probe = new RangeFileServer(
                        new Dictionary<string, string> { ["pkg"] = _localPath }, ServerPort);
                    probe.Start();
                    string url = probe.UrlFor("127.0.0.1", "pkg");
                    using var http = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(10) };
                    using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                    long? len = resp.Content.Headers.ContentLength;
                    serve = resp.IsSuccessStatusCode ? $"server OK{(len > 0 ? $" ({len / 1048576} MB)" : "")}" : "server HTTP " + (int)resp.StatusCode;
                }
                else
                {
                    var tl = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, ServerPort);
                    try
                    {
                        tl.Start();
                        serve = $"port {ServerPort} free (pick a file to test serving)";
                    }
                    finally { try { tl.Stop(); } catch { } }
                }
            }
            catch (Exception ex) { serve = "server FAILED: " + Short(ex.Message); }
            finally { try { probe?.Dispose(); } catch { } }
            Say($"console={mode} phone={pcIp} {serve}");
        }
        catch (Exception ex) { Say("test error: " + Short(ex.Message)); }
    }

    protected override void OnDestroy()
    {
        try { _server?.Dispose(); } catch { }
        base.OnDestroy();
    }
}
