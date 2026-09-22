using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.OS;
using Android.Views;
using Android.Widget;
using LoopDPI.Core;

namespace PkgSender.Droid;

[Activity(Label = "PKG Sender • by Loopayeh", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    const int PickReq = 1001;
    const int ServerPort = 9898;

    static readonly Color Bg = Color.ParseColor("#171717");
    static readonly Color Card = Color.ParseColor("#202020");
    static readonly Color Card2 = Color.ParseColor("#2A2A2A");
    static readonly Color Accent = Color.ParseColor("#4F8EF7");
    static readonly Color Text = Color.ParseColor("#F1F3F8");
    static readonly Color Muted = Color.ParseColor("#8B93A5");
    static readonly Color Good = Color.ParseColor("#8FD694");
    static readonly Color Bad = Color.ParseColor("#E17B7B");
    static readonly Color DarkOnAccent = Color.ParseColor("#171717");

    sealed class LibItem
    {
        public string Path = "";
        public string Title = "";
        public string TitleId = "";
        public long Size;
        public string Platform = "";
        public byte[]? Icon;
        public bool Queued;
        public string State = "";
        public LinearLayout? Row;
        public TextView? StateView;
    }

    readonly List<LibItem> _lib = new();
    EditText? _psIp;
    LinearLayout? _libBox;
    TextView? _libHead;
    TextView? _status;
    ProgressBar? _prog;
    Button? _sendBtn;
    Button? _testBtn;
    RangeFileServer? _server;
    bool _busy;

    int Dp(int dp) => (int)(dp * Resources!.DisplayMetrics!.Density);

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Window?.SetStatusBarColor(new Color(0x10, 0x10, 0x14));

        var root = new ScrollView(this);
        var lay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        lay.SetBackgroundColor(Bg);
        int pad = Dp(16);
        lay.SetPadding(pad, pad, pad, pad);
        root.AddView(lay);

        // header: logo + title + about
        var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        var logo = new ImageView(this);
        try
        {
            using var s = GetType().Assembly.GetManifestResourceStream("PkgSender.Droid.logo.png");
            if (s != null) logo.SetImageBitmap(BitmapFactory.DecodeStream(s));
        }
        catch { }
        logo.LayoutParameters = new LinearLayout.LayoutParams(Dp(48), Dp(48));
        head.AddView(logo);
        var titleBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var t1 = new TextView(this) { Text = "PKG Sender" };
        t1.SetTextColor(Text); t1.TextSize = 20; t1.SetTypeface(null, TypefaceStyle.Bold);
        var t2 = new TextView(this) { Text = "PS4 / PS5 over LAN • by Loopayeh" };
        t2.SetTextColor(Muted); t2.TextSize = 12;
        titleBox.AddView(t1); titleBox.AddView(t2);
        var tlp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        tlp.LeftMargin = Dp(12);
        titleBox.LayoutParameters = tlp;
        head.AddView(titleBox);
        var about = GhostBtn("About", ShowAbout);
        head.AddView(about);
        lay.AddView(head);

        // console row: IP + test
        var ipRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        ipRow.SetGravity(GravityFlags.CenterVertical);
        var ipp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        ipp.TopMargin = Dp(12);
        ipRow.LayoutParameters = ipp;
        _psIp = new EditText(this) { Hint = "Console IP, e.g. 192.168.1.105" };
        _psIp.SetTextColor(Text); _psIp.SetHintTextColor(Muted);
        _psIp.SetBackgroundColor(Card2);
        _psIp.SetPadding(Dp(12), Dp(10), Dp(12), Dp(10));
        string? saved = GetPreferences(FileCreationMode.Private).GetString("psip", null);
        if (!string.IsNullOrEmpty(saved)) _psIp.Text = saved;
        _psIp.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        ipRow.AddView(_psIp);
        _testBtn = GhostBtn("Test", () => _ = TestAsync());
        var tbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        tbp.LeftMargin = Dp(8);
        _testBtn.LayoutParameters = tbp;
        ipRow.AddView(_testBtn);
        lay.AddView(ipRow);

        // library header + add button
        var libRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        libRow.SetGravity(GravityFlags.CenterVertical);
        var llp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        llp.TopMargin = Dp(16);
        libRow.LayoutParameters = llp;
        _libHead = new TextView(this) { Text = "Library (0)" };
        _libHead.SetTextColor(Text); _libHead.TextSize = 16; _libHead.SetTypeface(null, TypefaceStyle.Bold);
        _libHead.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        libRow.AddView(_libHead);
        libRow.AddView(AccentBtn("+ Add PKG", PickFlow));
        lay.AddView(libRow);

        _libBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var lbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lbp.TopMargin = Dp(8);
        _libBox.LayoutParameters = lbp;
        lay.AddView(_libBox);

        // send queue
        _sendBtn = AccentBtn("Send queue", () => _ = SendQueueAsync());
        var sbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        sbp.TopMargin = Dp(16);
        _sendBtn.LayoutParameters = sbp;
        lay.AddView(_sendBtn);

        _prog = new ProgressBar(this, null, Android.Resource.Attribute.ProgressBarStyleHorizontal);
        _prog.Max = 100;
        var pbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        pbp.TopMargin = Dp(8);
        _prog.LayoutParameters = pbp;
        _prog.Visibility = ViewStates.Gone;
        lay.AddView(_prog);

        _status = new TextView(this) { Text = "idle — add a PKG, tap it to queue, then Send" };
        _status.SetTextColor(Muted); _status.TextSize = 13;
        var stp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        stp.TopMargin = Dp(8);
        _status.LayoutParameters = stp;
        lay.AddView(_status);

        SetContentView(root);
        RefreshLib();
    }

    Button AccentBtn(string text, Action onClick)
    {
        var b = new Button(this) { Text = text };
        b.SetTextColor(DarkOnAccent);
        b.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Accent);
        b.Click += (_, _) => onClick();
        return b;
    }

    Button GhostBtn(string text, Action onClick)
    {
        var b = new Button(this) { Text = text };
        b.SetTextColor(Text);
        b.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(Color.ParseColor("#404040"));
        b.Click += (_, _) => onClick();
        return b;
    }

    void ShowAbout()
    {
        new AlertDialog.Builder(this)
            .SetTitle("PKG Sender")
            .SetMessage("1.0.0 (droid)\nby Loopayeh\n\ngithub.com/Loopayeh/pkg-sender\n\nInstalls PS4/PS5 games over LAN.\nRun pkg-receiver.elf on PS5 or RPI/GoldHEN on PS4.")
            .SetPositiveButton("OK", (_, _) => { })
            .Show();
    }

    // ---------- library ----------

    void PickFlow()
    {
        try
        {
            var i = new Intent(Intent.ActionOpenDocument);
            i.AddCategory(Intent.CategoryOpenable);
            i.SetType("*/*");
            i.PutExtra(Intent.ExtraAllowMultiple, true);
            StartActivityForResult(Intent.CreateChooser(i, "Pick PKG"), PickReq);
        }
        catch (Exception ex) { Say("pick failed: " + Short(ex.Message)); }
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickReq || resultCode != Result.Ok || data == null) return;
        var uris = new List<Android.Net.Uri>();
        if (data.ClipData != null)
            for (int k = 0; k < data.ClipData.ItemCount; k++)
            {
                var u = data.ClipData.GetItemAt(k)?.Uri;
                if (u != null) uris.Add(u);
            }
        else if (data.Data != null)
            uris.Add(data.Data);
        if (uris.Count == 0) return;
        _ = Task.Run(async () =>
        {
            int n = 0;
            foreach (var u in uris)
                if (await AddUriAsync(u)) n++;
            RunOnUiThread(() => { RefreshLib(); Say(n > 0 ? $"{n} added — tap to queue" : "nothing added"); });
        });
    }

    async Task<bool> AddUriAsync(Android.Net.Uri uri)
    {
        try
        {
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
            string dest = System.IO.Path.Combine(CacheDir!.AbsolutePath, name);
            using (var src = ContentResolver!.OpenInputStream(uri)!)
            using (var dst = File.Create(dest))
                await src.CopyToAsync(dst);
            PkgInfo? pkg = null;
            try
            {
                using var fs = File.Open(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
                pkg = PkgReader.Read(fs);
            }
            catch { }
            lock (_lib)
            {
                if (_lib.Any(x => x.Path == dest)) return false;
                _lib.Add(new LibItem
                {
                    Path = dest,
                    Title = pkg?.Title is { Length: > 0 } t ? t : System.IO.Path.GetFileNameWithoutExtension(name),
                    TitleId = pkg?.TitleId ?? "",
                    Size = pkg?.PackageSize ?? new FileInfo(dest).Length,
                    Platform = pkg?.Platform ?? "",
                    Icon = pkg?.IconData,
                    Queued = true,
                });
            }
            return true;
        }
        catch { return false; }
    }

    static string SizeStr(long n) =>
        n >= 1L << 30 ? $"{n / (1024.0 * 1024 * 1024):0.0} GB" : $"{n / (1024.0 * 1024):0.0} MB";

    void RefreshLib()
    {
        var box = _libBox;
        if (box == null) return;
        box.RemoveAllViews();
        int q = 0;
        lock (_lib)
        {
            if (_libHead != null) _libHead.Text = $"Library ({_lib.Count})";
            foreach (var it in _lib)
            {
                if (it.Queued) q++;
                box.AddView(BuildRow(it));
            }
        }
        if (_lib.Count == 0)
        {
            var e = new TextView(this) { Text = "empty — + Add PKG to stage games from your phone" };
            e.SetTextColor(Muted); e.TextSize = 13;
            e.SetPadding(Dp(4), Dp(8), Dp(4), Dp(8));
            box.AddView(e);
        }
        if (_sendBtn != null) _sendBtn.Text = q > 0 ? $"Send queue ({q})" : "Send queue";
    }

    LinearLayout BuildRow(LibItem it)
    {
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetBackgroundColor(it.Queued ? Card2 : Card);
        row.SetPadding(Dp(8), Dp(8), Dp(8), Dp(8));
        var rp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        rp.TopMargin = Dp(6);
        row.LayoutParameters = rp;

        var img = new ImageView(this);
        var ilp = new LinearLayout.LayoutParams(Dp(56), Dp(56));
        ilp.RightMargin = Dp(10);
        img.LayoutParameters = ilp;
        img.SetScaleType(ImageView.ScaleType.CenterCrop);
        Bitmap? bmp = null;
        try
        {
            if (it.Icon is { Length: > 0 })
                bmp = BitmapFactory.DecodeByteArray(it.Icon, 0, it.Icon.Length);
        }
        catch { }
        if (bmp != null)
            img.SetImageBitmap(bmp);
        else
        {
            img.SetBackgroundColor(Card2);
            img.SetImageResource(Android.Resource.Drawable.IcMenuGallery);
        }
        row.AddView(img);

        var txt = new LinearLayout(this) { Orientation = Orientation.Vertical };
        txt.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        var a = new TextView(this) { Text = it.Title };
        a.SetTextColor(Text); a.TextSize = 15; a.SetTypeface(null, TypefaceStyle.Bold);
        a.SetSingleLine(true); a.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        var b = new TextView(this) { Text = $"{it.TitleId} • {SizeStr(it.Size)}" };
        b.SetTextColor(Muted); b.TextSize = 12;
        txt.AddView(a); txt.AddView(b);
        it.StateView = new TextView(this) { Text = it.State };
        it.StateView.SetTextColor(it.State.StartsWith("done") ? Good : it.State.StartsWith("fail") ? Bad : Accent);
        it.StateView.TextSize = 12;
        txt.AddView(it.StateView);
        row.AddView(txt);

        var cb = new CheckBox(this) { Checked = it.Queued };
        cb.CheckedChange += (_, e) =>
        {
            it.Queued = e.IsChecked;
            row.SetBackgroundColor(it.Queued ? Card2 : Card);
            RefreshSendLabel();
        };
        row.AddView(cb);
        row.Clickable = true;
        row.Click += (_, _) => { cb.Checked = !cb.Checked; };
        it.Row = row;
        return row;
    }

    void RefreshSendLabel()
    {
        int q;
        lock (_lib) q = _lib.Count(x => x.Queued);
        RunOnUiThread(() => { if (_sendBtn != null) _sendBtn.Text = q > 0 ? $"Send queue ({q})" : "Send queue"; });
    }

    // ---------- send queue ----------

    async Task SendQueueAsync()
    {
        if (_busy) return;
        string psIp = (_psIp?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(psIp)) { Say("type the console IP first"); return; }
        List<LibItem> queue;
        lock (_lib) queue = _lib.Where(x => x.Queued).ToList();
        if (queue.Count == 0) { Say("queue is empty — tick some games"); return; }
        GetPreferences(FileCreationMode.Private).Edit().PutString("psip", psIp).Apply();

        _busy = true;
        _sendBtn!.Enabled = false;
        _testBtn!.Enabled = false;
        RunOnUiThread(() => { _prog!.Max = queue.Count; _prog.Progress = 0; _prog.Visibility = ViewStates.Visible; });
        try
        {
            string pcIp = await Task.Run(() => PhoneIpFor(psIp));
            int done = 0;
            foreach (var it in queue)
            {
                SetState(it, "sending…");
                Say($"sending {it.Title}…");
                bool ok = await SendOneAsync(psIp, pcIp, it);
                SetState(it, ok ? "done" : "failed");
                if (ok) done++;
                int d = done, n = queue.Count;
                RunOnUiThread(() => { _prog!.Progress = d; });
                Say($"{d}/{n} sent");
            }
            Say(done == queue.Count ? $"all {done} sent — watch the console." : $"{done}/{queue.Count} sent, {queue.Count - done} failed");
        }
        catch (Exception ex) { Say("error: " + Short(ex.Message)); }
        finally
        {
            _busy = false;
            RunOnUiThread(() => { _sendBtn.Enabled = true; _testBtn!.Enabled = true; });
        }
    }

    void SetState(LibItem it, string s)
    {
        it.State = s;
        RunOnUiThread(() =>
        {
            if (it.StateView != null)
            {
                it.StateView.Text = s;
                it.StateView.SetTextColor(s.StartsWith("done") ? Good : s.StartsWith("fail") ? Bad : Accent);
            }
        });
    }

    async Task<bool> SendOneAsync(string psIp, string pcIp, LibItem it)
    {
        try
        {
            _server?.Dispose();
            var files = new Dictionary<string, string> { ["pkg"] = it.Path };
            _server = new RangeFileServer(files, ServerPort);
            _server.Start();
            string url = _server.UrlFor(pcIp, "pkg");

            PkgInfo? pkg = null;
            try
            {
                using var fs = File.Open(it.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                pkg = PkgReader.Read(fs);
            }
            catch { }
            bool isPs4 = (pkg?.Platform ?? "").StartsWith("PS4");

            var (ok, reply) = await Ps4Installer.PushRpiAsync(psIp, url, it.Title);
            string method = "rpi";
            if (!ok && isPs4 && pkg != null)
            {
                _server.RegisterManifest("pkg", Ps4Installer.BuildManifest(url, pkg.PackageSize, pkg.Digest));
                var g = await Ps4Installer.PushGoldHenAsync(psIp, pcIp, _server.ManifestUrlFor(pcIp, "pkg"), pkg, ServerPort);
                ok = g.Ok; reply = g.Reply; method = "goldhen";
            }
            if (!ok) SetState(it, "failed");
            return ok;
        }
        catch { SetState(it, "failed"); return false; }
    }

    // ---------- test ----------

    async Task TestAsync()
    {
        string psIp = (_psIp?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(psIp)) { Say("type the console IP first"); return; }
        GetPreferences(FileCreationMode.Private).Edit().PutString("psip", psIp).Apply();
        try
        {
            Say("probing console (12800/9090)…");
            string mode = await Ps4Installer.DetectAsync(psIp, fresh: true);
            string pcIp = await Task.Run(() => PhoneIpFor(psIp));
            if (mode == "offline")
            {
                Say("probing ports…");
                string diag = await Ps4Installer.DiagnoseAsync(psIp);
                Say($"OFFLINE {psIp} phone={pcIp}\n{diag}\n(all closed: wrong IP / console off / AP isolation)");
                return;
            }
            string serve;
            RangeFileServer? probe = null;
            try
            {
                LibItem? first;
                lock (_lib) first = _lib.FirstOrDefault(x => File.Exists(x.Path));
                if (first != null)
                {
                    probe = new RangeFileServer(
                        new Dictionary<string, string> { ["pkg"] = first.Path }, ServerPort);
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
                        serve = $"port {ServerPort} free (add a PKG to test serving)";
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

    /// <summary>
    /// Phone's Wi-Fi IP via Android WifiManager — far more reliable on
    /// Android than interface enumeration (which can return mobile/VPN).
    /// Falls back to NetDiscovery when Wi-Fi is off.
    /// </summary>
    string PhoneIpFor(string psIp)
    {
        string wifiIp = "0.0.0.0";
        try
        {
            var wifi = (Android.Net.Wifi.WifiManager?)GetSystemService(WifiService);
            int ip = wifi?.ConnectionInfo?.IpAddress ?? 0;
            if (ip != 0)
                wifiIp = $"{ip & 0xff}.{(ip >> 8) & 0xff}.{(ip >> 16) & 0xff}.{(ip >> 24) & 0xff}";
        }
        catch { }
        if (wifiIp != "0.0.0.0")
        {
            try
            {
                var w = System.Net.IPAddress.Parse(wifiIp).GetAddressBytes();
                var p = System.Net.IPAddress.Parse(psIp).GetAddressBytes();
                if (w[0] == p[0] && w[1] == p[1] && w[2] == p[2])
                    return wifiIp;
            }
            catch { }
            var nets = NetDiscovery.GetLanNetworks();
            string? same = null;
            try
            {
                var ps = System.Net.IPAddress.Parse(psIp);
                same = nets.FirstOrDefault(n => n.Contains(ps))?.Address.ToString();
            }
            catch { }
            return same ?? wifiIp;
        }
        var nets2 = NetDiscovery.GetLanNetworks();
        return NetDiscovery.BestPcIpFor(nets2, psIp) ?? "0.0.0.0";
    }

    void Say(string s) => RunOnUiThread(() => _status!.Text = s);
    static string Short(string s) => s.Length > 140 ? s[..140] : s;

    protected override void OnDestroy()
    {
        try { _server?.Dispose(); } catch { }
        base.OnDestroy();
    }
}
