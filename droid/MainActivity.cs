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

[Activity(Label = "PKG Sender • by Loopayeh", MainLauncher = true, Exported = true, Icon = "@drawable/logo")]
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
        readonly Action<int> _a;
        public MenuHandler(Action<int> a) { _a = a; }
        public bool OnMenuItemClick(IMenuItem item) { _a(item.ItemId); return true; }
    }

    readonly List<LibItem> _lib = new();
    readonly System.Collections.Concurrent.ConcurrentQueue<string> _logQ = new();
    readonly object _logFileLock = new();
    string _logPath = "";
    void AddLog(string s)
    {
        try
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + s;
            _logQ.Enqueue(line);
            while (_logQ.Count > 300 && _logQ.TryDequeue(out _)) { }
            if (!string.IsNullOrEmpty(_logPath))
                lock (_logFileLock)
                    File.AppendAllText(_logPath, line + "\n");
        }
        catch { }
    }
    TextInputEditText? _psIp;
    LinearLayout? _libBox;
    TextView? _libHead;
    TextView? _statusTitle;
    TextView? _statusDetail;
    MaterialCardView? _statusCard;
    MaterialCardView? _heroCard;
    TextView? _connView;
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
        var b = new MaterialButton(this, null, MatAttr("materialButtonStyle")) { Text = text };
        b.CornerRadius = Dp(12);
        b.Click += (_, _) => onClick();
        return b;
    }

    MaterialButton TonalBtn(string text, Action onClick)
    {
        var b = new MaterialButton(this, null, MatAttr("materialButtonOutlinedStyle")) { Text = text };
        try
        {
            // tonal-filled look: container tint + matching stroke, SlipNet-style pill
            b.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(
                Dyn("colorSecondaryContainer", Color.LightGray));
            b.SetTextColor(Dyn("colorOnSecondaryContainer", Color.Black));
            b.StrokeColor = Android.Content.Res.ColorStateList.ValueOf(
                Dyn("colorOutlineVariant", Color.LightGray));
            b.StrokeWidth = Dp(1);
        }
        catch { }
        b.CornerRadius = Dp(20);
        b.Click += (_, _) => onClick();
        return b;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            _logPath = System.IO.Path.Combine(CacheDir!.AbsolutePath, "pkgsender.log");
            File.AppendAllText(_logPath, $"--- start {DateTime.Now} ---\n");
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                try { File.AppendAllText(_logPath, $"CRASH {DateTime.Now}: {e.ExceptionObject}\n"); } catch { }
            };
            Android.Runtime.AndroidEnvironment.UnhandledExceptionRaiser += (_, e) =>
            {
                try { File.AppendAllText(_logPath, $"ANDROID-CRASH {DateTime.Now}: {e.Exception}\n"); } catch { }
            };
        }
        catch { }
        _subColor = Dyn("colorOnSurfaceVariant", Color.Gray);

        var lay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        try { lay.SetBackgroundColor(Dyn("colorSurface", Color.White)); } catch { }
        int pad = Dp(16);
        lay.SetPadding(pad, 0, pad, pad);

        var bar = new MaterialToolbar(this);
        bar.Title = "PKG Sender";
        try
        {
            bar.SetBackgroundColor(Color.Transparent);
            bar.SetTitleTextColor(Dyn("colorOnSurface", Color.Black));
        }
        catch { }
        bar.Menu.Add(0, 1, 0, "Log");
        bar.Menu.Add(0, 2, 0, "About");
        bar.SetOnMenuItemClickListener(new MenuHandler(id =>
        {
            if (id == 1) ShowLog();
            else ShowAbout();
        }));
        lay.AddView(bar);

        // decode logo once for hero art
        Bitmap? heroLogo = null;
        try
        {
            using var s = GetType().Assembly.GetManifestResourceStream("PkgSender.Droid.logo.png");
            if (s != null)
                using (var bmp = BitmapFactory.DecodeStream(s))
                    if (bmp != null)
                        heroLogo = Bitmap.CreateScaledBitmap(bmp, Dp(56), Dp(56), true);
        }
        catch { }

        // hero: app identity + console connection in one surface card
        var hero = new MaterialCardView(this);
        var hlp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        hlp.TopMargin = Dp(8);
        hero.LayoutParameters = hlp;
        hero.Radius = Dp(24);
        hero.CardElevation = Dp(0);
        try { hero.SetCardBackgroundColor(Dyn("colorSurfaceContainer", Color.ParseColor("#F3EDF7"))); } catch { }
        var heroIn = new LinearLayout(this) { Orientation = Orientation.Vertical };
        heroIn.SetPadding(Dp(20), Dp(20), Dp(20), Dp(20));
        hero.AddView(heroIn);

        var heroRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        heroRow.SetGravity(GravityFlags.CenterVertical);
        var heroImg = new ImageView(this);
        heroImg.LayoutParameters = new LinearLayout.LayoutParams(Dp(56), Dp(56));
        if (heroLogo != null) heroImg.SetImageBitmap(heroLogo);
        else heroImg.SetImageResource(Android.Resource.Drawable.IcMenuGallery);
        heroImg.SetScaleType(ImageView.ScaleType.CenterCrop);
        try
        {
            var hrd = new GradientDrawable();
            hrd.SetCornerRadius(Dp(16));
            hrd.SetColor(Android.Graphics.Color.Transparent);
            heroImg.SetBackgroundDrawable(hrd);
            heroImg.ClipToOutline = true;
        }
        catch { }
        heroRow.AddView(heroImg);
        var heroTxt = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var htp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        htp.LeftMargin = Dp(16);
        heroTxt.LayoutParameters = htp;
        var heroTitle = new TextView(this) { Text = "PKG Sender" };
        heroTitle.TextSize = 22; heroTitle.SetTypeface(null, TypefaceStyle.Bold);
        try { heroTitle.SetTextColor(Dyn("colorOnSurface", Color.Black)); } catch { }
        var heroSub = new TextView(this) { Text = "PS4 / PS5 packages over LAN" };
        heroSub.TextSize = 14;
        heroSub.SetTextColor(Dyn("colorOnSurfaceVariant", Color.Gray));
        heroTxt.AddView(heroTitle); heroTxt.AddView(heroSub);
        heroRow.AddView(heroTxt);
        heroIn.AddView(heroRow);

        // console row: outlined IP + tonal test
        var ipRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var ipp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        ipp.TopMargin = Dp(16);
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
        heroIn.AddView(ipRow);
        _connView = new TextView(this) { Text = "● not tested" };
        _connView.TextSize = 13; _connView.SetTypeface(null, TypefaceStyle.Bold);
        _connView.SetTextColor(Dyn("colorOnSurfaceVariant", Color.Gray));
        var cnp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        cnp.TopMargin = Dp(12);
        _connView.LayoutParameters = cnp;
        heroIn.AddView(_connView);
        lay.AddView(hero);
        _heroCard = hero;

        // library header + add
        var libRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        libRow.SetGravity(GravityFlags.CenterVertical);
        var llp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        llp.TopMargin = Dp(16);
        libRow.LayoutParameters = llp;
        _libHead = new TextView(this) { Text = "Library (0)" };
        _libHead.TextSize = 20; _libHead.SetTypeface(null, TypefaceStyle.Bold);
        try { _libHead.SetTextColor(Dyn("colorOnSurface", Color.Black)); } catch { }
        _libHead.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        libRow.AddView(_libHead);
        var addBtn = FilledBtn("+ Add PKG", PickFlow);
        addBtn.CornerRadius = Dp(16);
        libRow.AddView(addBtn);
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
        _sendBtn.CornerRadius = Dp(16);
        _sendBtn.SetMinimumHeight(Dp(56));
        _sendBtn.TextSize = 16;
        var sbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        sbp.TopMargin = Dp(12);
        _sendBtn.LayoutParameters = sbp;
        lay.AddView(_sendBtn);

        _statusCard = new MaterialCardView(this);
        var scp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        scp.TopMargin = Dp(12);
        _statusCard.LayoutParameters = scp;
        _statusCard.Radius = Dp(16);
        _statusCard.CardElevation = Dp(0);
        try { _statusCard.SetCardBackgroundColor(Dyn("colorSurfaceContainer", Color.ParseColor("#F3EDF7"))); } catch { }
        var scIn = new LinearLayout(this) { Orientation = Orientation.Vertical };
        scIn.SetPadding(Dp(14), Dp(12), Dp(14), Dp(12));
        _statusCard.AddView(scIn);

        _prog = new LinearProgressIndicator(this);
        _prog.Visibility = ViewStates.Gone;
        scIn.AddView(_prog);

        _statusTitle = new TextView(this) { Text = "Ready" };
        _statusTitle.TextSize = 15; _statusTitle.SetTypeface(null, TypefaceStyle.Bold);
        try { _statusTitle.SetTextColor(Dyn("colorOnSurface", Color.Black)); } catch { }
        _statusTitle.SetSingleLine(true); _statusTitle.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        scIn.AddView(_statusTitle);

        _statusDetail = new TextView(this) { Text = "add a PKG, tick it, then Send" };
        _statusDetail.SetTextColor(_subColor); _statusDetail.TextSize = 12;
        _statusDetail.SetMaxLines(3);
        _statusDetail.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        scIn.AddView(_statusDetail);
        lay.AddView(_statusCard);

        SetContentView(lay);
        RefreshLib();
    }

    void SetConn(bool? ok, string text)
    {
        RunOnUiThread(() =>
        {
            try
            {
                if (_connView != null)
                {
                    _connView.Text = (ok == null ? "○ " : "● ") + text;
                    _connView.SetTextColor(ok == true ? Color.ParseColor("#2E7D32")
                        : ok == false ? Color.ParseColor("#C62828")
                        : Dyn("colorOnSurfaceVariant", Color.Gray));
                }
                if (_heroCard != null)
                {
                    _heroCard.StrokeWidth = ok == null ? 0 : Dp(2);
                    if (ok != null)
                        _heroCard.StrokeColor = ok == true ? Color.ParseColor("#2E7D32") : Color.ParseColor("#C62828");
                }
            }
            catch { }
        });
    }

    void ShowLog()
    {
        var dlg = new BottomSheetDialog(this);
        var v = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(20);
        v.SetPadding(pad, pad, pad, pad);
        var t = new TextView(this) { Text = "Transfer log (copy to me if a send fails)" };
        t.TextSize = 16; t.SetTypeface(null, TypefaceStyle.Bold);
        var sv = new ScrollView(this);
        var slp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, Dp(320));
        slp.TopMargin = Dp(8); slp.BottomMargin = Dp(12);
        sv.LayoutParameters = slp;
        var body = new TextView(this) { Text = ReadLogTail() };
        body.SetTextIsSelectable(true);
        body.Typeface = Android.Graphics.Typeface.Monospace;
        body.TextSize = 11;
        sv.AddView(body);
        var close = FilledBtn("Close", () => dlg.Dismiss());
        v.AddView(t); v.AddView(sv); v.AddView(close);
        dlg.SetContentView(v);
        dlg.Show();
    }

    string ReadLogTail()
    {
        try
        {
            if (!string.IsNullOrEmpty(_logPath) && File.Exists(_logPath))
            {
                var lines = File.ReadAllLines(_logPath);
                int from = Math.Max(0, lines.Length - 150);
                return string.Join("\n", lines.Skip(from));
            }
        }
        catch { }
        return string.Join("\n", _logQ.ToArray());
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
            Exception? last = null;
            for (int a = 0; a < 3; a++)
            {
                try
                {
                    var (ch, fin, pfd) = OpenChannelAt(_cr, _uri, offset);
                    return new SafStream(ch, fin, pfd, Length, offset);
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { System.Threading.Thread.Sleep(150); } catch { }
                }
            }
            throw last ?? new IOException("open failed");
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
        // one reused direct buffer: per-read Allocate() churned 34k native
        // buffers per transfer and starved the runtime under 16 threads.
        readonly Java.Nio.ByteBuffer _bb = Java.Nio.ByteBuffer.Allocate(64 * 1024);
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
            int total = 0;
            while (total < count)
            {
                _bb.Clear();
                int lim = Math.Min(_bb.Capacity(), count - total);
                _bb.Limit(lim);
                int n = _ch.Read(_bb);
                if (n <= 0) return total;
                _bb.Flip();
                _bb.Get(buffer, offset + total, n);
                total += n;
                _pos += n;
            }
            return total;
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
                : low.EndsWith(".ffpkg") ? "ffpkg"
                : low.EndsWith(".pfs") ? "pfs" : "pkg";
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
                else
                {
                    try
                    {
                        // exFAT image: walk the FS straight on the document.
                        var (ch2, fin2, pfd2) = OpenChannelAt(ContentResolver!, uri, 0);
                        using (var ss2 = new SafStream(ch2, fin2, pfd2, total, 0))
                            hpkg = ExfatReader.Read(ss2, name, total);
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
        card.Radius = Dp(20);
        card.CardElevation = Dp(0);
        try
        {
            card.StrokeWidth = Dp(1);
            card.StrokeColor = Dyn("colorOutlineVariant", Color.ParseColor("#E0E0E0"));
            card.SetCardBackgroundColor(Dyn("colorSurfaceContainerLow", Color.White));
            if (it.Queued)
            {
                card.StrokeColor = Dyn("colorPrimary", Color.ParseColor("#6750A4"));
                card.SetCardBackgroundColor(Dyn("colorPrimaryContainer", Color.White));
            }
        }
        catch { }

        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetPadding(Dp(14), Dp(14), Dp(14), Dp(14));
        card.AddView(row);

        var img = new ImageView(this);
        var ilp = new LinearLayout.LayoutParams(Dp(60), Dp(60));
        ilp.RightMargin = Dp(12);
        img.LayoutParameters = ilp;
        img.SetScaleType(ImageView.ScaleType.CenterCrop);
        try
        {
            var rd = new GradientDrawable();
            rd.SetCornerRadius(Dp(12));
            rd.SetColor(Android.Graphics.Color.Transparent);
            img.SetBackgroundDrawable(rd);
            img.ClipToOutline = true;
        }
        catch { }
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
        var titleRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        titleRow.SetGravity(GravityFlags.CenterVertical);
        var a = new TextView(this) { Text = it.Title };
        a.TextSize = 16; a.SetTypeface(null, TypefaceStyle.Bold);
        a.SetSingleLine(true); a.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        a.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        titleRow.AddView(a);
        // platform badge: PS5 / PS4 / format fallback
        string plat = (it.Platform ?? "").Trim().ToUpperInvariant();
        string badgeTxt = plat.StartsWith("PS5") ? "PS5" : plat.StartsWith("PS4") ? "PS4"
            : (it.Format ?? "").Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(badgeTxt))
        {
            var badge = new TextView(this) { Text = badgeTxt };
            badge.TextSize = 11; badge.SetTypeface(null, TypefaceStyle.Bold);
            badge.SetPadding(Dp(8), Dp(3), Dp(8), Dp(3));
            try
            {
                var bd = new GradientDrawable();
                bd.SetCornerRadius(Dp(8));
                if (badgeTxt == "PS5")
                {
                    bd.SetColor(Color.White);
                    badge.SetTextColor(Color.Black);
                }
                else if (badgeTxt == "PS4")
                {
                    bd.SetColor(Color.ParseColor("#0D6EFD"));
                    badge.SetTextColor(Color.White);
                }
                else
                {
                    bd.SetColor(Dyn("colorSurfaceVariant", Color.ParseColor("#E1E2EC")));
                    badge.SetTextColor(Dyn("colorOnSurfaceVariant", Color.Gray));
                }
                badge.SetBackgroundDrawable(bd);
            }
            catch { }
            var blp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
            blp.LeftMargin = Dp(8);
            badge.LayoutParameters = blp;
            titleRow.AddView(badge);
        }
        txt.AddView(titleRow);
        var b = new TextView(this)
        {
            Text = $"{it.Format.ToUpperInvariant()} • {it.TitleId} • {SizeStr(it.Size)}{(it.Direct ? " • direct" : "")}"
        };
        b.SetTextColor(_subColor); b.TextSize = 13;
        txt.AddView(b);
        it.StateView = new TextView(this) { Text = it.State };
        PaintState(it.StateView, it.State);
        txt.AddView(it.StateView);
        row.AddView(txt);

        var cb = new MaterialCheckBox(this) { Checked = it.Queued, Enabled = !_busy };
        cb.CheckedChange += (_, e) =>
        {
            if (_busy) { cb.Checked = it.Queued; return; }
            it.Queued = e.IsChecked;
            try
            {
                if (it.Row != null)
                {
                    if (it.Queued)
                    {
                        it.Row.StrokeColor = Dyn("colorPrimary", Color.ParseColor("#6750A4"));
                        it.Row.SetCardBackgroundColor(Dyn("colorPrimaryContainer", Color.White));
                    }
                    else
                    {
                        it.Row.StrokeColor = Dyn("colorOutlineVariant", Color.ParseColor("#E0E0E0"));
                        it.Row.SetCardBackgroundColor(Dyn("colorSurfaceContainerLow", Color.White));
                    }
                }
            }
            catch { }
            RefreshSendLabel();
        };
        row.AddView(cb);
        row.Clickable = true;
        row.Click += (_, _) => { if (_busy) return; cb.Checked = !cb.Checked; };
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
        RunOnUiThread(() => RefreshLib()); // rebuild rows with locked checkboxes
        // keep Wi-Fi/CPU awake: doze or Wi-Fi power-save dropping the
        // server mid-transfer looks like a random "copy failed" on console
        Android.Net.Wifi.WifiManager.WifiLock? wl = null;
        PowerManager.WakeLock? cpu = null;
        try
        {
            try
            {
                var wifi = (Android.Net.Wifi.WifiManager?)GetSystemService(WifiService);
                wl = wifi?.CreateWifiLock(Android.Net.WifiMode.FullHighPerf, "pkgsender:send");
                wl?.SetReferenceCounted(false);
                wl?.Acquire();
            }
            catch { }
            try
            {
                var pm = (PowerManager?)GetSystemService(PowerService);
                cpu = pm?.NewWakeLock(WakeLockFlags.Partial, "pkgsender:send");
                cpu?.SetReferenceCounted(false);
                cpu?.Acquire(30 * 60 * 1000L);
            }
            catch { }
        }
        catch { }
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
                RunOnUiThread(() => { _prog!.Max = n; _prog.SetProgressCompat(d, true); });
                Say($"{d}/{n} sent");
            }
            Say(done == queue.Count ? $"all {done} sent — watch the console." : $"{done}/{queue.Count} sent, {queue.Count - done} failed");
        }
        catch (Exception ex) { Say("error: " + Short(ex.Message)); }
        finally
        {
            try { if (wl?.IsHeld == true) wl.Release(); } catch { }
            try { if (cpu?.IsHeld == true) cpu.Release(); } catch { }
            try { wl?.Dispose(); } catch { }
            try { cpu?.Dispose(); } catch { }
            _busy = false;
            // done items leave the queue (unticked); failed stay ticked for retry
            lock (_lib) foreach (var q in queue) if (q.State.StartsWith("done")) q.Queued = false;
            RunOnUiThread(() => { _sendBtn.Enabled = true; _testBtn!.Enabled = true; RefreshLib(); });
        }
    }

    /// <summary>
    /// Follow a receiver pull copy to completion: live MB/%/speed on the
    /// phone progress bar, then byte-verify the landed file. False on
    /// stall, drop, or size mismatch.
    /// </summary>
    long LastPullGot;

    async Task<bool> TrackPullAsync(string psIp, string remote, LibItem it)
    {
        long t0 = System.Environment.TickCount64;
        long lastGot = 0;
        long lastTick = t0;
        RangeFileServer? srv = _server;
        try
        {
            while (true)
            {
                await Task.Delay(1000);
                var (active, name, got, want, paused) =
                    await ConsoleClient.GetPullAsync(psIp);
                LastPullGot = got;
                if (!active) break;
                long now = System.Environment.TickCount64;
                double sec = Math.Max(1, now - lastTick) / 1000.0;
                double spd = (got - lastGot) / 1048576.0 / sec;
                lastTick = now; lastGot = got;
                long g = got, w = want;
                long served = 0;
                try { served = srv?.ServedFor("pkg") ?? 0; } catch { }
                long sv = served;
                RunOnUiThread(() =>
                {
                    if (w > 0)
                    {
                        _prog!.Max = 1000;
                        _prog.SetProgressCompat((int)Math.Min(1000, 1000L * g / w), false);
                    }
                    Say($"copying… {g / 1048576.0:0}/{w / 1048576.0:0} MB ({(w > 0 ? 100.0 * g / w : 0):0}%, {spd:0.0} MB/s, served {sv / 1048576.0:0}){(paused ? " — paused" : "")}");
                });
                SetState(it, $"copying {100.0 * got / Math.Max(1, want):0}%");
                if (now - t0 > 6 * 60 * 60 * 1000L) return false;
            }
        }
        catch { return false; }
        try
        {
            var (exists, size) = await ConsoleClient.StatAsync(psIp, remote);
            if (exists && size == it.Size) return true;
            Say($"landed size mismatch (console {size}, want {it.Size})");
            return false;
        }
        catch { return false; }
    }

    /// <summary>
    /// Follow a PKG install's download off our server: the receiver only
    /// reports busy/active, so served-bytes is the progress signal. True
    /// once the console pulled the full file (local install continues).
    /// </summary>
    async Task<bool> TrackInstallAsync(string psIp, RangeFileServer? srv, LibItem it)
    {
        long t0 = System.Environment.TickCount64;
        long lastServed = 0;
        long prevServed = 0;
        long prevTick = t0;
        long stallSince = t0;
        try
        {
            while (true)
            {
                await Task.Delay(1000);
                long served = 0;
                try { served = srv?.ServedFor("pkg") ?? 0; } catch { }
                long now = System.Environment.TickCount64;
                if (served > lastServed)
                {
                    lastServed = served;
                    stallSince = now;
                }
                double sec = Math.Max(1, now - prevTick) / 1000.0;
                double spd = (served - prevServed) / 1048576.0 / sec;
                prevServed = served; prevTick = now;
                long s = Math.Min(served, it.Size);
                double pct = it.Size > 0 ? 100.0 * s / it.Size : 0;
                RunOnUiThread(() =>
                {
                    _prog!.Max = 1000;
                    _prog.SetProgressCompat((int)Math.Min(1000, pct * 10), false);
                    Say($"installing… {s / 1048576.0:0}/{it.Size / 1048576.0:0} MB ({pct:0}%, {spd:0.0} MB/s)");
                });
                SetState(it, $"sending {pct:0}%");
                if (served >= it.Size && it.Size > 0) return true;
                if (now - stallSince > 120000) return served >= it.Size && it.Size > 0;
                if (now - t0 > 6 * 60 * 60 * 1000L) return false;
            }
        }
        catch { return false; }
    }

    void PaintState(TextView v, string s)
    {
        try
        {
            bool done = s.StartsWith("done");
            bool fail = s.StartsWith("fail");
            v.Text = (done ? "✓ " : fail ? "✕ " : "") + s;
            v.TextSize = 13;
            if (done || fail) v.SetTypeface(null, TypefaceStyle.Bold);
            if (done || fail)
            {
                var d = new GradientDrawable();
                d.SetCornerRadius(Dp(8));
                d.SetColor(done ? Color.ParseColor("#E6F4EA") : Color.ParseColor("#FCE8E6"));
                v.SetBackgroundDrawable(d);
                v.SetPadding(Dp(8), Dp(3), Dp(8), Dp(3));
            }
            else v.SetBackgroundDrawable(null);
            v.SetTextColor(done ? Color.ParseColor("#1B7A2E") : fail ? Color.ParseColor("#C62828") : _subColor);
        }
        catch { }
    }

    void SetState(LibItem it, string s)
    {
        it.State = s;
        RunOnUiThread(() =>
        {
            if (it.StateView != null) PaintState(it.StateView, s);
        });
    }

    /// <summary>Server serving one library item: direct SAF source or cached file.</summary>
    RangeFileServer BuildServerFor(LibItem it)
    {
        var server = new RangeFileServer(new Dictionary<string, string>(), ServerPort);
        server.RequestLog = line => AddLog(line);
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

            // cover for the console install notification (console fetches it).
            // Unique id per send: the console caches artwork by URL, so a
            // fixed /icon/pkg would show the previous game's cover.
            string? iconUrl = null;
            if (it.Icon is { Length: > 0 })
            {
                string iconId = "icon" + DateTime.UtcNow.Ticks;
                _server.RegisterIcon(iconId, it.Icon);
                iconUrl = _server.IconUrlFor(pcIp, iconId);
            }

            // disc images go to /data/homebrew via receiver pull, not install.
            // Poll the receiver's pull status so the phone shows live progress.
            // The receiver has no segment retry: re-pull resumes partials.
            if (it.Format != "pkg")
            {
                string remote = "/data/homebrew/" + it.FileName;
                for (int attempt = 1; attempt <= 6; attempt++)
                {
                    AddLog($"pull try {attempt}/6 {it.FileName} size={it.Size} resume=true");
                    if (attempt > 1)
                        Say($"retrying copy from {SizeStr(Math.Min(it.Size, LastPullGot))}… ({attempt}/6)");
                    else
                        Say($"copying {it.FileName} to console…");
                    var (pok, preply) = await ConsoleClient.PullAsync(psIp, url, remote, resume: true);
                    if (!pok)
                    {
                        SetState(it, "failed");
                        Say($"copy failed: {Short(preply)}");
                        return false;
                    }
                    if (await TrackPullAsync(psIp, remote, it))
                    {
                        SetState(it, "done");
                        return true;
                    }
                }
                SetState(it, "failed");
                Say("copy stalled after 6 tries — check console space/Wi-Fi");
                return false;
            }

            var (ok, reply) = await Ps4Installer.PushRpiAsync(psIp, url, it.Title, iconUrl);
            string method = "rpi";
            if (!ok && isPs4 && pkg != null)
            {
                _server.RegisterManifest("pkg", Ps4Installer.BuildManifest(url, it.Size, pkg.Digest));
                var g = await Ps4Installer.PushGoldHenAsync(psIp, pcIp, _server.ManifestUrlFor(pcIp, "pkg"), pkg, ServerPort);
                ok = g.Ok; reply = g.Reply; method = "goldhen";
            }
            if (!ok)
            {
                SetState(it, "failed");
                return false;
            }
            // install accepted: follow the console's download off our server
            bool downloaded = await TrackInstallAsync(psIp, _server, it);
            SetState(it, downloaded ? "done" : "failed");
            return downloaded;
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
            SetConn(null, "probing…");
            string mode = await Ps4Installer.DetectAsync(psIp, fresh: true);
            string pcIp = await Task.Run(() => PhoneIpFor(psIp));
            if (mode == "offline")
            {
                Say("probing ports…");
                string diag = await Ps4Installer.DiagnoseAsync(psIp);
                SetConn(false, "offline — " + psIp);
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
            SetConn(true, $"connected ({mode}) • {psIp}");
        }
        catch (Exception ex) { SetConn(false, "test failed"); Say("test error: " + Short(ex.Message)); }
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

    void Say(string s) => RunOnUiThread(() =>
    {
        try
        {
            // first line = headline, rest = collapsible-looking detail
            int nl = s.IndexOf('\n');
            string head = nl < 0 ? s : s[..nl].Trim();
            string tail = nl < 0 ? "" : s[(nl + 1)..].Trim();
            if (_statusTitle != null) _statusTitle.Text = head.Length > 90 ? head[..90] + "…" : head;
            if (_statusDetail != null)
            {
                _statusDetail.Text = tail;
                _statusDetail.Visibility = string.IsNullOrEmpty(tail) ? ViewStates.Gone : ViewStates.Visible;
            }
        }
        catch { }
    });
    static string Short(string s) => s.Length > 140 ? s[..140] : s;

    protected override void OnDestroy()
    {
        try { _server?.Dispose(); } catch { }
        base.OnDestroy();
    }
}
