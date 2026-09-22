using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.OS;
using Android.Views;
using Android.Widget;
using Google.Android.Material.AppBar;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.Card;
using Google.Android.Material.CheckBox;
using Google.Android.Material.Color;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Google.Android.Material.TextField;
using LoopDPI.Core;

namespace PkgSender.Droid;

[Activity(Label = "PKG Sender • by Loopayeh", MainLauncher = true, Exported = true)]
public sealed class MainActivity : Activity
{
    const int PickReq = 1001;
    const int ServerPort = 9898;

    static readonly Color Good = Color.ParseColor("#8FD694");
    static readonly Color Bad = Color.ParseColor("#E17B7B");

    sealed class LibItem
    {
        public string Path = "";
        public string? UriStr; // direct mode: original SAF uri, no copy
        public bool Direct;
        public string Format = "pkg"; // pkg | exfat | ffpfsc | ffpkg
        public string FileName = ""; // remote basename for image copy
        public string Title = "";
        public string TitleId = "";
        public long Size;
        public string Platform = "";
        public byte[]? Icon;
        public PkgInfo? Pkg;
        public bool Queued;
        public string State = "";
        public MaterialCardView? Row;
        public TextView? StateView;
    }

    sealed class MenuHandler : Java.Lang.Object, MaterialToolbar.IOnMenuItemClickListener
    {
        readonly Action _a;
        public MenuHandler(Action a) { _a = a; }
        public bool OnMenuItemClick(IMenuItem item) { _a(); return true; }
    }

    readonly List<LibItem> _lib = new();
    TextInputEditText? _psIp;
    LinearLayout? _libBox;
    TextView? _libHead;
    TextView? _status;
    LinearProgressIndicator? _prog;
    MaterialButton? _sendBtn;
    MaterialButton? _testBtn;
    RangeFileServer? _server;
    bool _busy;
    Color _subColor = Color.Gray;

    int Dp(int dp) => (int)(dp * Resources!.DisplayMetrics!.Density);

    int MatAttr(string name)
    {
        try { return Resources?.GetIdentifier(name, "attr", PackageName) ?? 0; }
        catch { return 0; }
    }

    Color Dyn(string attrName, Color fallback)
        => new Color(MaterialColors.GetColor(this, MatAttr(attrName), fallback));

    MaterialButton FilledBtn(string text, Action onClick)
    {
        var b = new MaterialButton(this) { Text = text };
        b.Click += (_, _) => onClick();
        return b;
    }

    MaterialButton TonalBtn(string text, Action onClick)
    {
        var b = new MaterialButton(this) { Text = text };
        b.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(
            Dyn("colorPrimaryContainer", Color.LightGray));
        b.SetTextColor(Dyn("colorOnPrimaryContainer", Color.Black));
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        _subColor = Dyn("colorOnSurfaceVariant", Color.Gray);

        var lay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(16);
        lay.SetPadding(pad, 0, pad, pad);

        var bar = new MaterialToolbar(this);
        bar.Title = "PKG Sender";
        bar.Subtitle = "PS4 / PS5 over LAN";
        try
        {
            using var s = GetType().Assembly.GetManifestResourceStream("PkgSender.Droid.logo.png");
            if (s != null)
                using (var bmp = BitmapFactory.DecodeStream(s))
                    if (bmp != null)
                        bar.Logo = new BitmapDrawable(Resources, bmp);
        }
        catch { }
        bar.Menu.Add(0, 1, 0, "About");
        bar.SetOnMenuItemClickListener(new MenuHandler(ShowAbout));
        lay.AddView(bar);

        // console row: outlined IP + tonal test
        var ipRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var ipp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        ipp.TopMargin = Dp(4);
        ipRow.LayoutParameters = ipp;
        var ipWrap = new TextInputLayout(this, null, MatAttr("textInputOutlinedStyle"));
        ipWrap.Hint = "Console IP, e.g. 192.168.1.105";
        ipWrap.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        _psIp = new TextInputEditText(ipWrap.Context);
        _psIp.InputType = Android.Text.InputTypes.ClassText
            | Android.Text.InputTypes.TextVariationVisiblePassword;
        string? saved = GetPreferences(FileCreationMode.Private).GetString("psip", null);
        if (!string.IsNullOrEmpty(saved)) _psIp.Text = saved;
        ipWrap.AddView(_psIp);
        ipRow.AddView(ipWrap);
        _testBtn = TonalBtn("Test", () => _ = TestAsync());
        var tbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        tbp.LeftMargin = Dp(8);
        tbp.Gravity = GravityFlags.CenterVertical;
        _testBtn.LayoutParameters = tbp;
        ipRow.AddView(_testBtn);
        lay.AddView(ipRow);

        // library header + add
        var libRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        libRow.SetGravity(GravityFlags.CenterVertical);
        var llp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        llp.TopMargin = Dp(16);
        libRow.LayoutParameters = llp;
        _libHead = new TextView(this) { Text = "Library (0)" };
        _libHead.TextSize = 18; _libHead.SetTypeface(null, TypefaceStyle.Bold);
        _libHead.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        libRow.AddView(_libHead);
        libRow.AddView(TonalBtn("+ Add PKG", PickFlow));
        lay.AddView(libRow);

        // scrolling library
        var scroller = new ScrollView(this);
        scroller.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _libBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scroller.AddView(_libBox);
        lay.AddView(scroller);

        // send + progress + status
        _sendBtn = FilledBtn("Send queue", () => _ = SendQueueAsync());
        var sbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        sbp.TopMargin = Dp(12);
        _sendBtn.LayoutParameters = sbp;
        lay.AddView(_sendBtn);

        _prog = new LinearProgressIndicator(this);
        var pbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        pbp.TopMargin = Dp(8);
        _prog.LayoutParameters = pbp;
        _prog.Visibility = ViewStates.Gone;
        lay.AddView(_prog);

        _status = new TextView(this) { Text = "idle — add a PKG, tick it, then Send" };
        _status.SetTextColor(_subColor); _status.TextSize = 13;
        var stp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        stp.TopMargin = Dp(8);
        _status.LayoutParameters = stp;
        lay.AddView(_status);

        SetContentView(lay);
        RefreshLib();
    }

    void ShowAbout()
    {
        var dlg = new BottomSheetDialog(this);
        var v = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(24);
        v.SetPadding(pad, pad, pad, pad);
        var t = new TextView(this) { Text = "PKG Sender" };
        t.TextSize = 20; t.SetTypeface(null, TypefaceStyle.Bold);
        var b = new TextView(this)
        {
            Text = "1.0.0 (droid) • by Loopayeh\n\ngithub.com/Loopayeh/pkg-sender\n\nInstalls PS4/PS5 games over LAN.\nRun pkg-receiver.elf on PS5 or RPI/GoldHEN on PS4."
        };
        b.SetTextColor(_subColor); b.TextSize = 14;
        var bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        bp.TopMargin = Dp(8); bp.BottomMargin = Dp(16);
        b.LayoutParameters = bp;
        var close = FilledBtn("Close", () => dlg.Dismiss());
        v.AddView(t); v.AddView(b); v.AddView(close);
        dlg.SetContentView(v);
        dlg.Show();
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
            i.AddFlags(ActivityFlags.GrantReadUriPermission
                | ActivityFlags.GrantPersistableUriPermission);
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
        Say($"reading {uris.Count} file(s)…");
        _ = Task.Run(async () =>
        {
            int n = 0;
            string lastErr = "";
            foreach (var u in uris)
            {
                var (added, err) = await AddUriAsync(u);
                if (added) n++;
                else if (!string.IsNullOrEmpty(err)) lastErr = err;
            }
            int total = n;
            string errMsg = lastErr;
            RunOnUiThread(() =>
            {
                RefreshLib();
                Say(total > 0 ? $"{total} added — tick to queue"
                    : string.IsNullOrEmpty(errMsg) ? "nothing added" : "add failed: " + Short(errMsg));
            });
        });
    }

    static (Java.Nio.Channels.FileChannel Ch, Java.IO.FileInputStream Fin,
        Android.OS.ParcelFileDescriptor Pfd) OpenChannelAt(
        ContentResolver cr, Android.Net.Uri uri, long offset)
    {
        var pfd = cr.OpenFileDescriptor(uri, "r")
            ?? throw new IOException("open fd failed");
        Java.IO.FileInputStream? fin = null;
        try
        {
            fin = new Java.IO.FileInputStream(pfd.FileDescriptor);
            var ch = fin.Channel ?? throw new IOException("no channel");
            ch.Position(offset); // lseek; throws on pipes
            return (ch, fin, pfd);
        }
        catch
        {
            try { fin?.Close(); } catch { }
            try { pfd.Close(); } catch { }
            throw;
        }
    }

    /// <summary>Seekable SAF document served straight to the console, no copy.</summary>
    sealed class SafRangeSource : LoopDPI.Core.IRangeSource
    {
        readonly ContentResolver _cr;
        readonly Android.Net.Uri _uri;
        public long Length { get; }
        public SafRangeSource(ContentResolver cr, Android.Net.Uri uri, long len)
        { _cr = cr; _uri = uri; Length = len; }
        public Stream OpenAt(long offset)
        {
            var (ch, fin, pfd) = OpenChannelAt(_cr, _uri, offset);
            return new SafStream(ch, fin, pfd, Length, offset);
        }
    }

    /// <summary>Seekable read stream over a SAF file channel (parse + serve).</summary>
    sealed class SafStream : Stream
    {
        readonly Java.Nio.Channels.FileChannel _ch;
        readonly Java.IO.FileInputStream _fin;
        readonly Android.OS.ParcelFileDescriptor _pfd;
        readonly long _len;
        long _pos;
        public SafStream(Java.Nio.Channels.FileChannel ch,
            Java.IO.FileInputStream fin, Android.OS.ParcelFileDescriptor pfd,
            long len, long pos)
        { _ch = ch; _fin = fin; _pfd = pfd; _len = len; _pos = pos; }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _len;
        public override long Position
        {
            get => _pos;
            set => Seek(value, SeekOrigin.Begin);
        }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var bb = Java.Nio.ByteBuffer.Allocate(count);
            int n = _ch.Read(bb);
            if (n <= 0) return n;
            bb.Flip();
            bb.Get(buffer, offset, n);
            _pos += n;
            return n;
        }
        public override long Seek(long o, SeekOrigin org)
        {
            long t = org switch
            {
                SeekOrigin.Begin => o,
                SeekOrigin.Current => _pos + o,
                SeekOrigin.End => _len + o,
                _ => throw new ArgumentOutOfRangeException(nameof(org)),
            };
            _ch.Position(t);
            _pos = t;
            return _pos;
        }
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { _ch.Close(); } catch { }
                try { _fin.Close(); } catch { }
                try { _pfd.Close(); } catch { }
            }
            base.Dispose(disposing);
        }
    }

    static bool TrySeek(Android.Net.Uri uri, ContentResolver cr)
    {
        try
        {
            var (ch, fin, pfd) = OpenChannelAt(cr, uri, 1);
            try { ch.Close(); } catch { }
            try { fin.Close(); } catch { }
            try { pfd.Close(); } catch { }
            return true;
        }
        catch { return false; }
    }

    async Task<(bool Added, string Err)> AddUriAsync(Android.Net.Uri uri)
    {
        try
        {
            string name = "game.pkg";
            long total = -1;
            try
            {
                using var c = ContentResolver!.Query(uri, null, null, null, null);
                if (c != null && c.MoveToFirst())
                {
                    int idx = c.GetColumnIndex(Android.Provider.OpenableColumns.DisplayName);
                    if (idx >= 0) name = c.GetString(idx) ?? name;
                    int szi = c.GetColumnIndex(Android.Provider.OpenableColumns.Size);
                    if (szi >= 0)
                    {
                        try { total = c.GetLong(szi); } catch { }
                    }
                }
            }
            catch (Exception ex) { return (false, "name query: " + ex.Message); }
            try
            {
                ContentResolver!.TakePersistableUriPermission(uri,
                    ActivityFlags.GrantReadUriPermission);
            }
            catch { }
            lock (_lib)
            {
                if (_lib.Any(x => x.UriStr == uri.ToString())) return (false, "");
            }

            // DIRECT: seekable provider? serve straight from the document.
            string low = name.ToLowerInvariant();
            string fmt = low.EndsWith(".exfat") ? "exfat"
                : low.EndsWith(".ffpfsc") ? "ffpfsc"
                : low.EndsWith(".ffpkg") ? "ffpkg" : "pkg";
            bool isImage = fmt != "pkg";
            if (total > 0 && TrySeek(uri, ContentResolver!))
            {
                Say($"reading header {name}…");
                PkgInfo? hpkg = null;
                string parseErr = "";
                if (!isImage)
                {
                    try
                    {
                        // seekable parse straight on the document: only the
                        // header/table/param/icon offsets are read, no copy.
                        var (ch, fin, pfd) = OpenChannelAt(ContentResolver!, uri, 0);
                        using (var ss = new SafStream(ch, fin, pfd, total, 0))
                            hpkg = PkgReader.Read(ss);
                    }
                    catch (Exception ex) { parseErr = ex.Message; }
                }
                bool usable = hpkg != null
                    && (!string.IsNullOrEmpty(hpkg.Title) || !string.IsNullOrEmpty(hpkg.TitleId));
                if (usable || isImage)
                {
                    string title = usable && !string.IsNullOrEmpty(hpkg!.Title)
                        ? hpkg.Title
                        : PrettyName(name);
                    lock (_lib)
                    {
                        _lib.Add(new LibItem
                        {
                            Path = "direct:" + name,
                            UriStr = uri.ToString(),
                            Direct = true,
                            Format = fmt,
                            FileName = name,
                            Title = title,
                            TitleId = usable ? hpkg!.TitleId ?? "" : GameReader.TitleIdFromName(name),
                            Size = total,
                            Platform = usable ? hpkg!.Platform ?? "" : "",
                            Icon = usable ? hpkg!.IconData : null,
                            Pkg = usable ? hpkg : null,
                            Queued = true,
                        });
                    }
                    return (true, "");
                }
                // header parse missed -> copy fallback below
                Say($"direct parse missed{(parseErr.Length > 0 ? ": " + Short(parseErr) : "")}, copying {name}…");
            }

            string dest = System.IO.Path.Combine(CacheDir!.AbsolutePath, name);
            bool have = false;
            try
            {
                // same name + same size already cached? reuse, no copy.
                var fi = new FileInfo(dest);
                have = total > 0 && fi.Exists && fi.Length == total;
            }
            catch { }
            if (!have)
            try
            {
                Say($"copying {name}…");
                using (var src = ContentResolver!.OpenInputStream(uri)!)
                using (var dst = File.Create(dest))
                {
                    var buf = new byte[1 << 20];
                    long got = 0;
                    long lastTick = System.Environment.TickCount64;
                    long lastGot = 0;
                    int n;
                    while ((n = await src.ReadAsync(buf, 0, buf.Length)) > 0)
                    {
                        await dst.WriteAsync(buf, 0, n);
                        got += n;
                        long now = System.Environment.TickCount64;
                        if (now - lastTick >= 500)
                        {
                            double mb = got / 1048576.0;
                            double spd = (got - lastGot) / 1048576.0 / ((now - lastTick) / 1000.0);
                            string msg = total > 0
                                ? $"copying {name}… {mb:0}/{total / 1048576.0:0} MB ({100.0 * got / total:0}%, {spd:0.0} MB/s)"
                                : $"copying {name}… {mb:0} MB ({spd:0.0} MB/s)";
                            lastTick = now; lastGot = got;
                            Say(msg);
                        }
                    }
                }
            }
            catch (Exception ex) { return (false, "copy: " + ex.Message); }
            PkgInfo? pkg = null;
            try
            {
                Say($"reading {name}…");
                if (isImage)
                    pkg = GameReader.Read(dest);
                else
                {
                    using var fs = File.Open(dest, FileMode.Open, FileAccess.Read, FileShare.Read);
                    pkg = PkgReader.Read(fs);
                }
            }
            catch (Exception ex) { return (false, "parse: " + ex.Message); }
            lock (_lib)
            {
                if (_lib.Any(x => x.Path == dest)) return (false, "");
                _lib.Add(new LibItem
                {
                    Path = dest,
                    Format = fmt,
                    FileName = name,
                    Title = pkg?.Title is { Length: > 0 } t ? t : PrettyName(name),
                    TitleId = pkg?.TitleId is { Length: > 0 } i ? i : GameReader.TitleIdFromName(name),
                    Size = pkg != null && pkg.PackageSize > 0 ? pkg.PackageSize : new FileInfo(dest).Length,
                    Platform = pkg?.Platform ?? "",
                    Icon = pkg?.IconData,
                    Pkg = pkg,
                    Queued = true,
                });
            }
            return (true, "");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    static string PrettyName(string name)
    {
        string b = System.IO.Path.GetFileNameWithoutExtension(name) ?? name;
        return b.Replace('_', ' ').Replace('.', ' ').Trim();
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
            e.SetTextColor(_subColor); e.TextSize = 13;
            e.SetPadding(Dp(4), Dp(12), Dp(4), Dp(12));
            box.AddView(e);
        }
        if (_sendBtn != null) _sendBtn.Text = q > 0 ? $"Send queue ({q})" : "Send queue";
    }

    MaterialCardView BuildRow(LibItem it)
    {
        var card = new MaterialCardView(this);
        var cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        cp.TopMargin = Dp(6); cp.BottomMargin = Dp(6);
        card.LayoutParameters = cp;
        card.Radius = Dp(16);
        card.CardElevation = Dp(1);

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(12), Dp(12), Dp(12), Dp(12));
        card.AddView(row);

        var img = new ImageView(this);
        var ilp = new LinearLayout.LayoutParams(Dp(56), Dp(56));
        ilp.RightMargin = Dp(12);
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
            img.SetImageResource(Android.Resource.Drawable.IcMenuGallery);
        row.AddView(img);

        var txt = new LinearLayout(this) { Orientation = Orientation.Vertical };
        txt.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        var a = new TextView(this) { Text = it.Title };
        a.TextSize = 16; a.SetTypeface(null, TypefaceStyle.Bold);
        a.SetSingleLine(true); a.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        var b = new TextView(this)
        {
            Text = $"{it.Format.ToUpperInvariant()} • {it.TitleId} • {SizeStr(it.Size)}{(it.Direct ? " • direct" : "")}"
        };
        b.SetTextColor(_subColor); b.TextSize = 13;
        txt.AddView(a); txt.AddView(b);
        it.StateView = new TextView(this) { Text = it.State };
        it.StateView.SetTextColor(it.State.StartsWith("done") ? Good : it.State.StartsWith("fail") ? Bad : _subColor);
        it.StateView.TextSize = 13;
        txt.AddView(it.StateView);
        row.AddView(txt);

        var cb = new MaterialCheckBox(this) { Checked = it.Queued };
        cb.CheckedChange += (_, e) =>
        {
            it.Queued = e.IsChecked;
            RefreshSendLabel();
        };
        row.AddView(cb);
        row.Clickable = true;
        row.Click += (_, _) => { cb.Checked = !cb.Checked; };
        it.Row = card;
        return card;
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
        RunOnUiThread(() => { _prog!.Max = queue.Count; _prog.SetProgressCompat(0, false); _prog.Visibility = ViewStates.Visible; });
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
                RunOnUiThread(() => { _prog!.SetProgressCompat(d, true); });
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
                it.StateView.SetTextColor(s.StartsWith("done") ? Good : s.StartsWith("fail") ? Bad : _subColor);
            }
        });
    }

    /// <summary>Server serving one library item: direct SAF source or cached file.</summary>
    RangeFileServer BuildServerFor(LibItem it)
    {
        var server = new RangeFileServer(new Dictionary<string, string>(), ServerPort);
        if (it.Direct && it.UriStr != null)
            server.RegisterSource("pkg", new SafRangeSource(ContentResolver!,
                Android.Net.Uri.Parse(it.UriStr)!, it.Size));
        else
        {
            server.Dispose();
            server = new RangeFileServer(
                new Dictionary<string, string> { ["pkg"] = it.Path }, ServerPort);
        }
        return server;
    }

    static PkgInfo WithSize(PkgInfo p, long size) => new PkgInfo
    {
        Title = p.Title, ContentId = p.ContentId, TitleId = p.TitleId,
        ContentType = p.ContentType, Version = p.Version, IsDlc = p.IsDlc,
        Platform = p.Platform, Description = p.Description, PackageSize = size,
        Format = p.Format, IsFolder = p.IsFolder, Digest = p.Digest,
        IconData = p.IconData, Params = p.Params,
    };

    async Task<bool> SendOneAsync(string psIp, string pcIp, LibItem it)
    {
        try
        {
            _server?.Dispose();
            _server = BuildServerFor(it);
            _server.Start();
            string url = _server.UrlFor(pcIp, "pkg");

            PkgInfo? pkg = it.Pkg;
            if (pkg == null && !it.Direct)
            {
                try
                {
                    using var fs = File.Open(it.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    pkg = PkgReader.Read(fs);
                }
                catch { }
            }
            if (pkg != null && pkg.PackageSize != it.Size)
                pkg = WithSize(pkg, it.Size);
            bool isPs4 = (pkg?.Platform ?? "").StartsWith("PS4");

            // cover for the console install notification (console fetches it)
            string? iconUrl = null;
            if (it.Icon is { Length: > 0 })
            {
                _server.RegisterIcon("pkg", it.Icon);
                iconUrl = _server.IconUrlFor(pcIp, "pkg");
            }

            // disc images go to /data/homebrew via receiver pull, not install
            if (it.Format != "pkg")
            {
                string remote = "/data/homebrew/" + it.FileName;
                Say($"copying {it.FileName} to console…");
                var (pok, preply) = await ConsoleClient.PullAsync(psIp, url, remote, resume: true);
                SetState(it, pok ? "done" : "failed");
                if (!pok) Say($"copy failed: {Short(preply)}");
                return pok;
            }

            var (ok, reply) = await Ps4Installer.PushRpiAsync(psIp, url, it.Title, iconUrl);
            string method = "rpi";
            if (!ok && isPs4 && pkg != null)
            {
                _server.RegisterManifest("pkg", Ps4Installer.BuildManifest(url, it.Size, pkg.Digest));
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
                lock (_lib) first = _lib.FirstOrDefault(x => x.Direct || File.Exists(x.Path));
                if (first != null)
                {
                    probe = BuildServerFor(first);
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
