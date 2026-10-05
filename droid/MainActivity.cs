using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading;
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
using Google.Android.Material.Tabs;
using Google.Android.Material.TextField;
using Android.Webkit;
using LoopDPI.Core;

namespace PkgSender.Droid;

    [Activity(Label = "LoopFlow • by Loopayeh", MainLauncher = true, Exported = true, Icon = "@drawable/logo")]
public sealed class MainActivity : Activity
{
    const int PickReq = 1001;
    const int FileChooserReq = 1002;
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

    sealed class TabHandler : Java.Lang.Object, TabLayout.IOnTabSelectedListener
    {
        readonly Action<int> _a;
        public TabHandler(Action<int> a) { _a = a; }
        public void OnTabSelected(TabLayout.Tab? tab) { if (tab != null) _a(tab.Position); }
        public void OnTabUnselected(TabLayout.Tab? tab) { }
        public void OnTabReselected(TabLayout.Tab? tab) { }
    }

    // File inputs (<input type=file>, e.g. PLDMGR "Upload ELF Payload") do
    // nothing in a plain WebView — this wires them to the system picker.
    sealed class PldChromeClient : WebChromeClient
    {
        readonly MainActivity _host;
        public PldChromeClient(MainActivity host) { _host = host; }
        public override bool OnShowFileChooser(WebView? webView, IValueCallback? filePathCallback, FileChooserParams? fileChooserParams)
            => _host.StartPldFileChooser(filePathCallback);
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
    MaterialCardView? _connCard;
    LinearLayout? _connBody;
    TextView? _connHead;
    TextView? _connDot;
    TextView? _connChev;
    TextView? _connView;
    LinearProgressIndicator? _prog;
    MaterialButton? _sendBtn;
    MaterialButton? _testBtn;
    MaterialButton? _detectBtn;
    TextView? _elfStatus;
    LinearLayout? _senderPage;
    LinearLayout? _payloadPage;
    MaterialButton? _tabPkgs;
    MaterialButton? _tabPld;
    MaterialButton? _tabRepo;
    LinearLayout? _repoPage;
    LinearLayout? _repoBox;
    TextView? _repoStatus;
    MaterialButton? _repoRefreshBtn;
    MaterialButton? _repoAllBtn;
    readonly List<RepoItem> _repo = new();
    readonly HashSet<string> _repoOpen = new(StringComparer.OrdinalIgnoreCase);
    bool _repoFetched;
    bool _repoBusy;
    const string PldmgrRepoUrl = "https://cdn.jsdelivr.net/gh/Loopayeh/ps5-payloads@main/payloads.json";
    const string PldmgrRepoFallbackUrl = "https://itsplk.github.io/ps5-payloads-mirror/payloads.json";

    sealed class RepoItem
    {
        public string Name = "";
        public string Filename = "";
        public string Url = "";
        public string SourceDirect = "";
        public string Version = "";
        public string Category = "";
        public string Description = "";
        public string Checksum = "";
        public string Updated = "";
        public MaterialButton? GetBtn;
        public MaterialButton? SendBtn;
        public TextView? StateView;
    }
    TextInputEditText? _pldPort;
    TextInputEditText? _pldIp;
    MaterialButton? _scanBtn;
    MaterialButton? _uploadElfBtn;
    MaterialButton? _cardSendBtn;
    bool _elfBusy;
    IValueCallback? _pldFileCb;
    WebView? _pldWeb;
    TextView? _pldStatus;
    bool _pldLoaded;
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
        try
        {
            // kill the white strip under the app (light system bars in dark mode)
            var surface = Dyn("colorSurface", Color.White);
            Window?.SetStatusBarColor(surface);
            Window?.SetNavigationBarColor(surface);
        }
        catch { }

        var lay = new LinearLayout(this) { Orientation = Orientation.Vertical };
        try { lay.SetBackgroundColor(Dyn("colorSurface", Color.White)); } catch { }
        int pad = Dp(16);
        lay.SetPadding(pad, 0, pad, pad);

        var bar = new MaterialToolbar(this);
        bar.Title = "LoopFlow";
        try
        {
            bar.SetBackgroundColor(Color.Transparent);
            bar.SetTitleTextColor(Dyn("colorOnSurface", Color.Black));
        }
        catch { }
        bar.Menu.Add(0, 1, 0, "Log");
        bar.Menu.Add(0, 2, 0, "About");
        bar.Menu.Add(0, 3, 0, "Guide");
        bar.SetOnMenuItemClickListener(new MenuHandler(id =>
        {
            if (id == 1) ShowLog();
            else if (id == 2) ShowAbout();
            else ShowGuide();
        }));
        // root: toolbar + content (single page: Games)
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        try { root.SetBackgroundColor(Dyn("colorSurface", Color.White)); } catch { }
        root.AddView(bar);
        // tabs: Packages | Payloads (PLDMGR web UI inside the app)
        var tabRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var trp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        trp.TopMargin = Dp(8);
        tabRow.LayoutParameters = trp;
        _tabPkgs = TonalBtn("📦 Packages", () => ShowPage(0));
        _tabPkgs.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        tabRow.AddView(_tabPkgs);
        _tabPld = TonalBtn("🚀 Payloads", () => ShowPage(1));
        var tpp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        tpp.LeftMargin = Dp(8);
        _tabPld.LayoutParameters = tpp;
        tabRow.AddView(_tabPld);
        _tabRepo = TonalBtn("📥 Repo", () => ShowPage(2));
        var trp2 = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        trp2.LeftMargin = Dp(8);
        _tabRepo.LayoutParameters = trp2;
        tabRow.AddView(_tabRepo);
        root.AddView(tabRow);
        PaintTabs(0);
        // lay (built below) is the main body
        lay.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        root.AddView(lay);
        _senderPage = lay;

        // payload page: PLDMGR web UI (console IP + editable port, default 8084)
        _payloadPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _payloadPage.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _payloadPage.SetPadding(pad, Dp(4), pad, 0);
        _payloadPage.Visibility = ViewStates.Gone;
        // row 1 (compact): console IP + web port + Open
        var pldIpRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        pldIpRow.SetGravity(GravityFlags.CenterVertical);
        var pldIpWrap = new TextInputLayout(this, null, MatAttr("textInputOutlinedStyle"));
        pldIpWrap.Hint = "Console IP";
        pldIpWrap.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        _pldIp = new TextInputEditText(pldIpWrap.Context);
        _pldIp.InputType = Android.Text.InputTypes.ClassText
            | Android.Text.InputTypes.TextVariationVisiblePassword;
        pldIpWrap.AddView(_pldIp);
        pldIpRow.AddView(pldIpWrap);
        var portWrap = new TextInputLayout(this, null, MatAttr("textInputOutlinedStyle"));
        portWrap.Hint = "Port";
        var pwp = new LinearLayout.LayoutParams(Dp(92), ViewGroup.LayoutParams.WrapContent);
        pwp.LeftMargin = Dp(8);
        pwp.Gravity = GravityFlags.CenterVertical;
        portWrap.LayoutParameters = pwp;
        _pldPort = new TextInputEditText(portWrap.Context);
        _pldPort.InputType = Android.Text.InputTypes.ClassNumber;
        string? savedPort = GetPreferences(FileCreationMode.Private).GetString("pldport", null);
        _pldPort.Text = string.IsNullOrEmpty(savedPort) ? "8084" : savedPort;
        portWrap.AddView(_pldPort);
        pldIpRow.AddView(portWrap);
        var openBtn = TonalBtn("Open", () => OpenPayloadUrl());
        var obp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        obp.LeftMargin = Dp(8);
        obp.Gravity = GravityFlags.CenterVertical;
        openBtn.LayoutParameters = obp;
        pldIpRow.AddView(openBtn);
        _payloadPage.AddView(pldIpRow);
        _pldIp.TextChanged += (_, _) => SyncIpFromPayloadTab();
        // row 2 (compact): scan + reload + send our ELF through PLDMGR itself
        var pldRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        pldRow.SetGravity(GravityFlags.CenterVertical);
        var plr = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        plr.TopMargin = Dp(8);
        pldRow.LayoutParameters = plr;
        _scanBtn = TonalBtn("⌕ Scan", () => _ = ScanPayloadPortAsync());
        _scanBtn.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        pldRow.AddView(_scanBtn);
        var reloadBtn = TonalBtn("⟳", () => OpenPayloadUrl());
        var rbp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 0.6f);
        rbp.LeftMargin = Dp(8);
        rbp.Gravity = GravityFlags.CenterVertical;
        reloadBtn.LayoutParameters = rbp;
        pldRow.AddView(reloadBtn);
        _uploadElfBtn = TonalBtn("⬆ Our ELF", () => _ = UploadReceiverViaPldmgrAsync());
        var ueb = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1.4f);
        ueb.LeftMargin = Dp(8);
        ueb.Gravity = GravityFlags.CenterVertical;
        _uploadElfBtn.LayoutParameters = ueb;
        pldRow.AddView(_uploadElfBtn);
        _payloadPage.AddView(pldRow);
        _pldStatus = new TextView(this) { Text = "" };
        _pldStatus.TextSize = 12;
        _pldStatus.SetTextColor(_subColor);
        _pldStatus.SetSingleLine(true);
        _pldStatus.Ellipsize = Android.Text.TextUtils.TruncateAt.Start;
        var psp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        psp.TopMargin = Dp(4);
        psp.BottomMargin = Dp(4);
        _pldStatus.LayoutParameters = psp;
        _payloadPage.AddView(_pldStatus);
        _pldWeb = new WebView(this);
        _pldWeb.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _pldWeb.Settings.JavaScriptEnabled = true;
        _pldWeb.Settings.DomStorageEnabled = true;
        _pldWeb.Settings.LoadWithOverviewMode = true;
        _pldWeb.Settings.UseWideViewPort = true;
        _pldWeb.Settings.MediaPlaybackRequiresUserGesture = false;
        _pldWeb.SetWebViewClient(new WebViewClient());
        _pldWeb.SetWebChromeClient(new PldChromeClient(this));
        _payloadPage.AddView(_pldWeb);
        root.AddView(_payloadPage);

        // repo page: cloud payload repository (phone downloads, sends via PLDMGR)
        _repoPage = new LinearLayout(this) { Orientation = Orientation.Vertical };
        _repoPage.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _repoPage.SetPadding(pad, Dp(4), pad, 0);
        _repoPage.Visibility = ViewStates.Gone;
        var repoRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        repoRow.SetGravity(GravityFlags.CenterVertical);
        _repoRefreshBtn = TonalBtn("⟳ Refresh", () => _ = RefreshRepoAsync());
        _repoRefreshBtn.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        repoRow.AddView(_repoRefreshBtn);
        _repoAllBtn = TonalBtn("⬇ Get all", () => _ = GetAllRepoAsync());
        var rab = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        rab.LeftMargin = Dp(8);
        rab.Gravity = GravityFlags.CenterVertical;
        _repoAllBtn.LayoutParameters = rab;
        repoRow.AddView(_repoAllBtn);
        _repoPage.AddView(repoRow);
        _repoStatus = new TextView(this) { Text = "" };
        _repoStatus.TextSize = 12;
        _repoStatus.SetTextColor(_subColor);
        _repoStatus.SetSingleLine(true);
        _repoStatus.Ellipsize = Android.Text.TextUtils.TruncateAt.Start;
        var rsp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        rsp.TopMargin = Dp(4);
        rsp.BottomMargin = Dp(4);
        _repoStatus.LayoutParameters = rsp;
        _repoPage.AddView(_repoStatus);
        var repoScroll = new ScrollView(this);
        repoScroll.LayoutParameters = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, 0, 1f);
        _repoBox = new LinearLayout(this) { Orientation = Orientation.Vertical };
        repoScroll.AddView(_repoBox);
        _repoPage.AddView(repoScroll);
        root.AddView(_repoPage);

        // merged console card: connection + receiver ELF, collapsible
        var conn = new MaterialCardView(this);
        var hlp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        hlp.TopMargin = Dp(8);
        conn.LayoutParameters = hlp;
        conn.Radius = Dp(20);
        conn.CardElevation = Dp(0);
        try { conn.SetCardBackgroundColor(Dyn("colorSurfaceContainer", Color.ParseColor("#F3EDF7"))); } catch { }
        var connIn = new LinearLayout(this) { Orientation = Orientation.Vertical };
        connIn.SetPadding(Dp(16), Dp(12), Dp(16), Dp(12));
        conn.AddView(connIn);
        var connHead = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        connHead.SetGravity(GravityFlags.CenterVertical);
        connHead.Clickable = true;
        _connDot = new TextView(this) { Text = "●" };
        _connDot.TextSize = 16; _connDot.SetTypeface(null, TypefaceStyle.Bold);
        _connDot.SetTextColor(Dyn("colorOnSurfaceVariant", Color.Gray));
        connHead.AddView(_connDot);
        _connHead = new TextView(this) { Text = "Console" };
        _connHead.TextSize = 16; _connHead.SetTypeface(null, TypefaceStyle.Bold);
        try { _connHead.SetTextColor(Dyn("colorOnSurface", Color.Black)); } catch { }
        var chp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        chp.LeftMargin = Dp(8);
        _connHead.LayoutParameters = chp;
        _connHead.SetSingleLine(true); _connHead.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        connHead.AddView(_connHead);
        _connChev = new TextView(this) { Text = "▸" };
        _connChev.TextSize = 16;
        _connChev.SetTextColor(Dyn("colorOnSurfaceVariant", Color.Gray));
        connHead.AddView(_connChev);
        connIn.AddView(connHead);
        _connBody = new LinearLayout(this) { Orientation = Orientation.Vertical };
        bool connOpen = true;
        try { connOpen = GetPreferences(FileCreationMode.Private).GetBoolean("conn_open", true); } catch { }
        _connBody.Visibility = connOpen ? ViewStates.Visible : ViewStates.Gone;
        _connChev.Text = connOpen ? "▾" : "▸";
        connHead.Click += (_, _) => ToggleConnCard();

        // (app identity lives in the toolbar now; connection card is compact)

        // console row: outlined IP + tonal test
        var ipRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var ipp = new LinearLayout.LayoutParams(
            ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        ipp.TopMargin = Dp(10);
        ipRow.LayoutParameters = ipp;
        var ipWrap = new TextInputLayout(this, null, MatAttr("textInputOutlinedStyle"));
        ipWrap.Hint = "Console IP, e.g. 192.168.1.105";
        ipWrap.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        _psIp = new TextInputEditText(ipWrap.Context);
        _psIp.InputType = Android.Text.InputTypes.ClassText
            | Android.Text.InputTypes.TextVariationVisiblePassword;
        string? saved = GetPreferences(FileCreationMode.Private).GetString("psip", null);
        if (!string.IsNullOrEmpty(saved)) _psIp.Text = saved;
        else _psIp.Text = "192.168.1.";
        ipWrap.AddView(_psIp);
        ipRow.AddView(ipWrap);
        _testBtn = TonalBtn("Test", () => _ = TestAsync());
        var tbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        tbp.LeftMargin = Dp(8);
        tbp.Gravity = GravityFlags.CenterVertical;
        _testBtn.LayoutParameters = tbp;
        ipRow.AddView(_testBtn);
        _connBody.AddView(ipRow);
        _psIp.TextChanged += (_, _) => UpdateConnHead();
        // slim full-width Detect under the IP row: IP keeps full width, no blank gap
        _detectBtn = TonalBtn("⌕ Detect console automatically", () => _ = DetectAsync());
        _detectBtn.TextSize = 13;
        var dbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        dbp.TopMargin = Dp(8);
        _detectBtn.LayoutParameters = dbp;
        _connBody.AddView(_detectBtn);
        _connView = new TextView(this) { Text = "● not tested" };
        _connView.TextSize = 13; _connView.SetTypeface(null, TypefaceStyle.Bold);
        _connView.SetTextColor(Dyn("colorOnSurfaceVariant", Color.Gray));
        var cnp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        cnp.TopMargin = Dp(12);
        _connView.LayoutParameters = cnp;
        _connBody.AddView(_connView);
        // receiver ELF row inside the same card
        var elfRow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        elfRow.SetGravity(GravityFlags.CenterVertical);
        var erp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        erp.TopMargin = Dp(10);
        elfRow.LayoutParameters = erp;
        var elfLab = new TextView(this) { Text = "pkg-receiver.elf" };
        elfLab.TextSize = 13; elfLab.SetTypeface(null, TypefaceStyle.Bold);
        elfLab.SetTextColor(Dyn("colorOnSurfaceVariant", Color.Gray));
        elfLab.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        elfLab.SetSingleLine(true); elfLab.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        elfRow.AddView(elfLab);
        _cardSendBtn = TonalBtn("Send", () => _ = SendReceiverFromCardAsync());
        _cardSendBtn.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        elfRow.AddView(_cardSendBtn);
        var shareBtn = TonalBtn("Share", () => _ = ShareElfAsync());
        var shbp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WrapContent, ViewGroup.LayoutParams.WrapContent);
        shbp.LeftMargin = Dp(8);
        shareBtn.LayoutParameters = shbp;
        elfRow.AddView(shareBtn);
        _connBody.AddView(elfRow);
        _elfStatus = new TextView(this) { Text = "" };
        _elfStatus.SetTextColor(_subColor); _elfStatus.TextSize = 12;
        _elfStatus.Visibility = ViewStates.Gone;
        _connBody.AddView(_elfStatus);
        connIn.AddView(_connBody);
        lay.AddView(conn);
        _connCard = conn;
        UpdateConnHead();

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
        var addBtn = FilledBtn("+ Add PKG / Image", PickFlow);
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

        SetContentView(root);
        RefreshLib();
        // first launch: About once, then the setup guide on top
        try
        {
            var prefs = GetPreferences(FileCreationMode.Private);
            if (!prefs.Contains("guide_shown_v1"))
            {
                prefs.Edit().PutBoolean("guide_shown_v1", true).Apply();
                lay.Post(() => { try { ShowAbout(thenGuide: true); } catch { } });
            }
        }
        catch { }
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
                if (_connDot != null)
                    _connDot.SetTextColor(ok == true ? Color.ParseColor("#2E7D32")
                        : ok == false ? Color.ParseColor("#C62828")
                        : Dyn("colorOnSurfaceVariant", Color.Gray));
                if (_connCard != null)
                {
                    _connCard.StrokeWidth = ok == null ? 0 : Dp(2);
                    if (ok != null)
                        _connCard.StrokeColor = ok == true ? Color.ParseColor("#2E7D32") : Color.ParseColor("#C62828");
                }
            }
            catch { }
        });
    }

    void ToggleConnCard()
    {
        try
        {
            bool open = _connBody?.Visibility != ViewStates.Visible;
            if (_connBody != null) _connBody.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
            if (_connChev != null) _connChev.Text = open ? "▾" : "▸";
            GetPreferences(FileCreationMode.Private).Edit().PutBoolean("conn_open", open).Apply();
        }
        catch { }
    }

    void UpdateConnHead()
    {
        try
        {
            string ip = (_psIp?.Text ?? "").Trim();
            if (_connHead != null)
                _connHead.Text = string.IsNullOrEmpty(ip) || ip.EndsWith(".") ? "Console • not set" : "Console • " + ip;
        }
        catch { }
    }

    void PaintTabs(int page)
    {
        try
        {
            var on = Dyn("colorPrimaryContainer", Color.LightGray);
            var onTx = Dyn("colorOnPrimaryContainer", Color.Black);
            var off = Dyn("colorSurfaceContainer", Color.Gray);
            var offTx = Dyn("colorOnSurfaceVariant", Color.Black);
            MaterialButton?[] tabs = { _tabPkgs, _tabPld, _tabRepo };
            for (int i = 0; i < tabs.Length; i++)
            {
                var b = tabs[i];
                if (b == null) continue;
                bool active = i == page;
                b.BackgroundTintList = Android.Content.Res.ColorStateList.ValueOf(active ? on : off);
                b.SetTextColor(active ? onTx : offTx);
            }
        }
        catch { }
    }

    void ShowPage(int page) // 0 packages, 1 payloads, 2 repo
    {
        try
        {
            if (_senderPage != null) _senderPage.Visibility = page == 0 ? ViewStates.Visible : ViewStates.Gone;
            if (_payloadPage != null) _payloadPage.Visibility = page == 1 ? ViewStates.Visible : ViewStates.Gone;
            if (_repoPage != null) _repoPage.Visibility = page == 2 ? ViewStates.Visible : ViewStates.Gone;
            PaintTabs(page);
            if (page == 1)
            {
                try
                {
                    string main = (_psIp?.Text ?? "").Trim();
                    if (!string.IsNullOrEmpty(main) && _pldIp != null
                        && string.IsNullOrEmpty((_pldIp.Text ?? "").Trim()))
                        _pldIp.Text = main;
                }
                catch { }
                if (!_pldLoaded) OpenPayloadUrl();
            }
            if (page == 2 && !_repoFetched) _ = RefreshRepoAsync();
        }
        catch { }
    }

    void OpenPayloadUrl()
    {
        try
        {
            string ip = (_psIp?.Text ?? "").Trim();
            string port = (_pldPort?.Text ?? "").Trim();
            if (port == "") port = "8084";
            try { GetPreferences(FileCreationMode.Private).Edit().PutString("pldport", port).Apply(); } catch { }
            if (string.IsNullOrEmpty(ip) || ip.EndsWith("."))
            {
                if (_pldStatus != null) _pldStatus.Text = "set the console IP first (top of Packages tab)";
                return;
            }
            string url = $"http://{ip}:{port}/";
            if (_pldStatus != null) _pldStatus.Text = url;
            _pldWeb?.LoadUrl(url);
            _pldLoaded = true;
        }
        catch (Exception ex)
        {
            if (_pldStatus != null) _pldStatus.Text = "open failed: " + ex.Message;
        }
    }

    void PldSay(string s) => RunOnUiThread(() =>
    {
        try { if (_pldStatus != null) _pldStatus.Text = s; } catch { }
    });

    void SyncIpFromPayloadTab()
    {
        try
        {
            string ip = (_pldIp?.Text ?? "").Trim();
            if (_psIp != null && (_psIp.Text ?? "") != ip) _psIp.Text = ip;
            GetPreferences(FileCreationMode.Private).Edit().PutString("psip", ip).Apply();
        }
        catch { }
    }

    void SetPayloadIp(string ip)
    {
        try
        {
            RunOnUiThread(() =>
            {
                if (_pldIp != null) _pldIp.Text = ip;
                if (_psIp != null) _psIp.Text = ip;
            });
            GetPreferences(FileCreationMode.Private).Edit().PutString("psip", ip).Apply();
        }
        catch { }
    }

    /// <summary>
    /// Scans the phone's /24 for a host with the payload web port open
    /// (PLDMGR 8084 by default) — finds the console even when our
    /// receiver ELF isn't running (so no beacon to listen for).
    /// </summary>
    async Task ScanPayloadPortAsync()
    {
        var btn = _scanBtn;
        if (btn != null) RunOnUiThread(() => btn.Enabled = false);
        try
        {
            int port = PldmgrPort();
            string? prefix = SubnetPrefix();
            if (string.IsNullOrEmpty(prefix))
            {
                PldSay("no network — type the IP manually");
                return;
            }
            PldSay($"scanning {prefix}0/24 for :{port}…");
            string? hit = await Task.Run(() => ScanSubnetForPort(port, prefix));
            if (string.IsNullOrEmpty(hit))
            {
                PldSay($"nothing on :{port} — PLDMGR running? same network?");
                return;
            }
            SetPayloadIp(hit);
            _pldLoaded = false;
            OpenPayloadUrl();
            PldSay($"found console at {hit}:{port}");
        }
        finally { if (btn != null) RunOnUiThread(() => btn.Enabled = true); }
    }

    string SubnetPrefix()
    {
        try
        {
            var wifi = (Android.Net.Wifi.WifiManager?)GetSystemService(WifiService);
            int raw = wifi?.ConnectionInfo?.IpAddress ?? 0;
            if (raw != 0)
            {
                string wifiIp = $"{raw & 0xff}.{(raw >> 8) & 0xff}.{(raw >> 16) & 0xff}.{(raw >> 24) & 0xff}";
                var b = System.Net.IPAddress.Parse(wifiIp).GetAddressBytes();
                return $"{b[0]}.{b[1]}.{b[2]}.";
            }
        }
        catch { }
        try
        {
            string typed = (_pldIp?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(typed) || typed.EndsWith(".")) typed = (_psIp?.Text ?? "").Trim();
            var b = System.Net.IPAddress.Parse(typed).GetAddressBytes();
            if (b.Length == 4) return $"{b[0]}.{b[1]}.{b[2]}.";
        }
        catch { }
        return "";
    }

    static string? ScanSubnetForPort(int port, string prefix)
    {
        try
        {
            string? found = null;
            var sem = new SemaphoreSlim(28);
            var tasks = new System.Collections.Generic.List<Task>();
            for (int i = 1; i < 255; i++)
            {
                string ip = prefix + i;
                tasks.Add(Task.Run(async () =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        if (found != null) return;
                        using var cli = new TcpClient();
                        using var cts = new CancellationTokenSource(600);
                        try
                        {
                            await cli.ConnectAsync(ip, port, cts.Token);
                            if (cli.Connected) found = ip;
                        }
                        catch { }
                    }
                    finally { sem.Release(); }
                }));
            }
            Task.WaitAll(tasks.ToArray(), TimeSpan.FromSeconds(25));
            return found;
        }
        catch { return null; }
    }

    int PldmgrPort()
    {
        string t = (_pldPort?.Text ?? "").Trim();
        int port = 8084;
        if (int.TryParse(t, out int p) && p > 0 && p <= 65535) port = p;
        try { GetPreferences(FileCreationMode.Private).Edit().PutString("pldport", port.ToString()).Apply(); } catch { }
        return port;
    }

    /// <summary>
    /// Sends a payload file through PLDMGR itself: POST raw bytes to
    /// /manage:upload?filename=… then GET /loadpayload:… to launch it.
    /// No loader port needed — works even if PLDMGR was loaded first.
    /// Progress goes to say(); returns (ok, final message).
    /// </summary>
    async Task<(bool Ok, string Msg)> PushFileToPldmgrAsync(string ip, int port, string filename, byte[] bytes, Action<string> say)
    {
        if (_elfBusy) return (false, "busy — wait a moment");
        _elfBusy = true;
        try
        {
            say($"uploading {filename} to PLDMGR {ip}:{port}…");
            using var http = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(90) };
            using var up = await http.PostAsync(
                $"http://{ip}:{port}/manage:upload?filename={Uri.EscapeDataString(filename)}",
                new System.Net.Http.ByteArrayContent(bytes));
            string upBody = (await up.Content.ReadAsStringAsync()).Trim();
            if (!up.IsSuccessStatusCode || !upBody.Contains("OK"))
                return (false, $"upload HTTP {(int)up.StatusCode} {Short(upBody)}");
            say("uploaded — launching…");
            using var http2 = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(20) };
            using var ln = await http2.GetAsync($"http://{ip}:{port}/loadpayload:{Uri.EscapeDataString(filename)}");
            string lnBody = (await ln.Content.ReadAsStringAsync()).Trim();
            if (ln.IsSuccessStatusCode && lnBody.Contains("OK"))
                return (true, $"{filename} live");
            return (false, $"uploaded, launch HTTP {(int)ln.StatusCode} — tap it in INSTALLED list");
        }
        catch (Exception ex) { return (false, "send failed: " + Short(ex.Message)); }
        finally { _elfBusy = false; }
    }

    async Task<byte[]> BundledReceiverBytesAsync() => await Task.Run(() =>
    {
        using var emb = GetType().Assembly.GetManifestResourceStream("PkgSender.Droid.pkg-receiver.elf")
            ?? throw new IOException("bundled ELF missing");
        using var ms = new MemoryStream();
        emb.CopyTo(ms);
        return ms.ToArray();
    });

    /// <summary>Console target shared by payload/repo sends: IP with fallbacks + PLDMGR web port.</summary>
    bool RepoConsoleTarget(out string ip, out int port)
    {
        ip = (_pldIp?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(ip) || ip.EndsWith("."))
        {
            ip = (_psIp?.Text ?? "").Trim();
            if (!string.IsNullOrEmpty(ip) && !ip.EndsWith(".")) SetPayloadIp(ip);
        }
        port = PldmgrPort();
        return !string.IsNullOrEmpty(ip) && !ip.EndsWith(".");
    }

    async Task UploadReceiverViaPldmgrAsync()
    {
        if (!RepoConsoleTarget(out string ip, out int port)) { PldSay("set the console IP first (or ⌕ Scan)"); return; }
        var btn = _uploadElfBtn;
        if (btn != null) RunOnUiThread(() => btn.Enabled = false);
        try
        {
            byte[] elf;
            try { elf = await BundledReceiverBytesAsync(); }
            catch (Exception ex) { PldSay("bundled ELF missing: " + Short(ex.Message)); return; }
            var (ok, msg) = await PushFileToPldmgrAsync(ip, port, "pkg-receiver.elf", elf, PldSay);
            PldSay(msg);
            Toast(ok ? "receiver live" : "send failed");
            if (ok) _pldWeb?.Reload();
        }
        finally { if (btn != null) RunOnUiThread(() => btn.Enabled = true); }
    }

    async Task SendReceiverFromCardAsync()
    {
        string ip = (_psIp?.Text ?? "").Trim();
        if (string.IsNullOrEmpty(ip) || ip.EndsWith(".")) { ElfSay(false, "type the console IP first"); return; }
        int port = PldmgrPort();
        var btn = _cardSendBtn;
        if (btn != null) RunOnUiThread(() => btn.Enabled = false);
        try
        {
            byte[] elf;
            try { elf = await BundledReceiverBytesAsync(); }
            catch (Exception ex) { ElfSay(false, "bundled ELF missing: " + Short(ex.Message)); return; }
            var (ok, msg) = await PushFileToPldmgrAsync(ip, port, "pkg-receiver.elf", elf, m => ElfSay(null, m));
            ElfSay(ok, msg);
            Toast(ok ? "receiver live" : "send failed");
            if (ok) Say("receiver live — Test, then Send queue");
        }
        finally { if (btn != null) RunOnUiThread(() => btn.Enabled = true); }
    }

    // ---------- cloud payload repository (phone-side, works with offline console) ----------

    void RepoSay(string s) => RunOnUiThread(() =>
    {
        try { if (_repoStatus != null) _repoStatus.Text = s; } catch { }
    });

    static string RepoSafeName(string f)
    {
        foreach (char c in new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' })
            f = f.Replace(c, '_');
        f = f.Trim();
        if (f.Length > 120) f = f[..120];
        return string.IsNullOrEmpty(f) ? "payload.elf" : f;
    }

    string RepoFilePath(string filename)
    {
        string dir = System.IO.Path.Combine(CacheDir!.AbsolutePath, "pldmgr_repo");
        try { Directory.CreateDirectory(dir); } catch { }
        return System.IO.Path.Combine(dir, RepoSafeName(filename));
    }

    string SavedRepoVersion(string filename)
    {
        try { return GetPreferences(FileCreationMode.Private).GetString("repo_ver_" + RepoSafeName(filename), "") ?? ""; }
        catch { return ""; }
    }

    static string RepoStr(JsonElement e, string key)
    {
        try
        {
            if (e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                return v.GetString() ?? "";
        }
        catch { }
        return "";
    }

    async Task RefreshRepoAsync()
    {
        var rb = _repoRefreshBtn;
        if (rb != null) RunOnUiThread(() => rb.Enabled = false);
        try
        {
            RepoSay("fetching repository…");
            string json = await Task.Run(async () =>
            {
                using var http = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromSeconds(30) };
                try { return await http.GetStringAsync(PldmgrRepoUrl); }
                catch { return await http.GetStringAsync(PldmgrRepoFallbackUrl); }
            });
            var items = new List<RepoItem>();
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    foreach (var e in doc.RootElement.EnumerateArray())
                    {
                        string fn = RepoStr(e, "filename");
                        string url = RepoStr(e, "url");
                        if (string.IsNullOrEmpty(fn) || string.IsNullOrEmpty(url)) continue;
                        items.Add(new RepoItem
                        {
                            Name = RepoStr(e, "name"),
                            Filename = fn,
                            Url = url,
                            SourceDirect = RepoStr(e, "source_direct"),
                            Version = RepoStr(e, "version"),
                            Category = RepoStr(e, "category"),
                            Description = RepoStr(e, "description"),
                            Checksum = RepoStr(e, "checksum"),
                            Updated = RepoStr(e, "last_update"),
                        });
                    }
            }
            lock (_repo) { _repo.Clear(); _repo.AddRange(items); }
            _repoFetched = true;
            BuildRepoList();
            RepoSay(items.Count == 0 ? "repository empty — retry" : $"{items.Count} payloads");
        }
        catch (Exception ex) { RepoSay("repo fetch failed: " + Short(ex.Message)); }
        finally { if (rb != null) RunOnUiThread(() => rb.Enabled = true); }
    }

    void BuildRepoList()
    {
        try
        {
            var box = _repoBox;
            if (box == null) return;
            RunOnUiThread(() =>
            {
                try
                {
                    box.RemoveAllViews();
                    List<RepoItem> items;
                    lock (_repo) items = new List<RepoItem>(_repo);
                    if (items.Count == 0)
                    {
                        var t = new TextView(this) { Text = "no payloads — tap ⟳ Refresh (needs phone internet)" };
                        t.SetTextColor(_subColor); t.TextSize = 13;
                        box.AddView(t);
                        return;
                    }
                    // group by category like PLDMGR (its `category` field), A→Z, collapsible
                    var groups = items
                        .GroupBy(x => string.IsNullOrEmpty(x.Category) ? "Uncategorized" : x.Category)
                        .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);
                    foreach (var g in groups)
                    {
                        string key = g.Key;
                        var cards = g.OrderBy(x => string.IsNullOrEmpty(x.Name) ? x.Filename : x.Name, StringComparer.OrdinalIgnoreCase).ToList();
                        var body = new LinearLayout(this) { Orientation = Orientation.Vertical };
                        foreach (var it in cards) body.AddView(RepoCard(it));
                        bool open;
                        lock (_repoOpen) open = _repoOpen.Contains(key);
                        body.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
                        var head = TonalBtn("", () => { });
                        head.LayoutParameters = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
                        head.Text = (open ? "▾ " : "▸ ") + $"{key} ({cards.Count})";
                        head.Click += (_, _) => ToggleRepoGroup(key, body, head);
                        box.AddView(head);
                        box.AddView(body);
                    }
                }
                catch { }
            });
        }
        catch { }
    }

    void ToggleRepoGroup(string key, LinearLayout body, MaterialButton head)
    {
        try
        {
            bool open = body.Visibility != ViewStates.Visible;
            body.Visibility = open ? ViewStates.Visible : ViewStates.Gone;
            lock (_repoOpen) { if (open) _repoOpen.Add(key); else _repoOpen.Remove(key); }
            string t = head.Text ?? "";
            if (t.StartsWith("▸ ") || t.StartsWith("▾ ")) t = t[2..];
            head.Text = (open ? "▾ " : "▸ ") + t;
        }
        catch { }
    }

    MaterialCardView RepoCard(RepoItem it)
    {
        var card = new MaterialCardView(this);
        var cp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        cp.TopMargin = Dp(8);
        card.LayoutParameters = cp;
        card.Radius = Dp(16);
        card.CardElevation = Dp(0);
        try { card.SetCardBackgroundColor(Dyn("colorSurfaceContainer", Color.ParseColor("#F3EDF7"))); } catch { }
        var ci = new LinearLayout(this) { Orientation = Orientation.Vertical };
        ci.SetPadding(Dp(14), Dp(10), Dp(14), Dp(10));
        card.AddView(ci);
        string title = string.IsNullOrEmpty(it.Name) ? it.Filename : it.Name;
        if (!string.IsNullOrEmpty(it.Version)) title += "  " + it.Version;
        var t = new TextView(this) { Text = title };
        t.TextSize = 15; t.SetTypeface(null, TypefaceStyle.Bold);
        t.SetSingleLine(true); t.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        try { t.SetTextColor(Dyn("colorOnSurface", Color.Black)); } catch { }
        ci.AddView(t);
        string sub = it.Category;
        if (!string.IsNullOrEmpty(it.Updated)) sub += (sub == "" ? "" : " • ") + it.Updated;
        sub += (sub == "" ? "" : " • ") + it.Filename;
        var s = new TextView(this) { Text = sub };
        s.TextSize = 12; s.SetTextColor(_subColor);
        s.SetSingleLine(true); s.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        ci.AddView(s);
        if (!string.IsNullOrEmpty(it.Description))
        {
            var d = new TextView(this) { Text = it.Description };
            d.TextSize = 12; d.SetTextColor(_subColor);
            d.SetMaxLines(2); d.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
            ci.AddView(d);
        }
        it.StateView = new TextView(this) { Text = "" };
        it.StateView.TextSize = 12;
        it.StateView.SetTextColor(_subColor);
        it.StateView.SetSingleLine(true);
        it.StateView.Ellipsize = Android.Text.TextUtils.TruncateAt.End;
        ci.AddView(it.StateView);
        var row = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var rp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        rp.TopMargin = Dp(6);
        row.LayoutParameters = rp;
        it.GetBtn = TonalBtn("⬇ Get", () => _ = GetRepoItemAsync(it));
        it.GetBtn.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        it.SendBtn = TonalBtn("⬆ Send", () => _ = SendRepoItemAsync(it));
        var sp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        sp.LeftMargin = Dp(8);
        it.SendBtn.LayoutParameters = sp;
        row.AddView(it.GetBtn); row.AddView(it.SendBtn);
        ci.AddView(row);
        UpdateRepoItemState(it);
        return card;
    }

    void UpdateRepoItemState(RepoItem it)
    {
        RunOnUiThread(() =>
        {
            try
            {
                bool exists = File.Exists(RepoFilePath(it.Filename));
                string saved = SavedRepoVersion(it.Filename);
                bool current = exists && !string.IsNullOrEmpty(it.Version) && saved == it.Version;
                bool outdated = exists && !current;
                string state;
                if (!exists) state = "not on phone";
                else if (current) state = $"on phone • {it.Version} ✓";
                else if (string.IsNullOrEmpty(saved)) state = "on phone • version unknown";
                else state = $"update: {saved} → {it.Version}";
                if (it.StateView != null) it.StateView.Text = state;
                if (it.GetBtn != null)
                {
                    it.GetBtn.Visibility = current ? ViewStates.Gone : ViewStates.Visible;
                    it.GetBtn.Text = outdated ? "⟳ Update" : "⬇ Get";
                    it.GetBtn.Enabled = !_repoBusy;
                }
                if (it.SendBtn != null)
                {
                    it.SendBtn.Visibility = exists ? ViewStates.Visible : ViewStates.Gone;
                    it.SendBtn.Enabled = exists && !_repoBusy;
                }
            }
            catch { }
        });
    }

    void RefreshAllRepoStates()
    {
        List<RepoItem> items;
        lock (_repo) items = new List<RepoItem>(_repo);
        foreach (var it in items) UpdateRepoItemState(it);
    }

    void Toast(string s) => RunOnUiThread(() =>
    {
        try { Android.Widget.Toast.MakeText(this, s, ToastLength.Short)?.Show(); } catch { }
    });

    /// <summary>Streamed download with live % + KB/s. Caller must already be off the UI thread for progress; use RepoSay (it marshals).</summary>
    async Task<byte[]> DownloadWithProgressAsync(string url, string label, Action<string> progress)
    {
        using var http = new System.Net.Http.HttpClient() { Timeout = TimeSpan.FromMinutes(10) };
        using var resp = await http.GetAsync(url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
        resp.EnsureSuccessStatusCode();
        long? total = resp.Content.Headers.ContentLength;
        using var net = await resp.Content.ReadAsStreamAsync();
        using var ms = new MemoryStream();
        var buf = new byte[65536];
        long got = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long lastReport = -9999;
        int r;
        while ((r = await net.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            ms.Write(buf, 0, r);
            got += r;
            long now = sw.ElapsedMilliseconds;
            if (now - lastReport > 400)
            {
                lastReport = now;
                string pct = total > 0 ? $" {got * 100 / total.Value}%" : "";
                double kbs = now > 0 ? (got / 1024.0) / (now / 1000.0) : 0;
                progress?.Invoke($"⬇ {label}{pct} • {got / 1024} KB • {kbs:0} KB/s");
            }
        }
        return ms.ToArray();
    }

    /// <summary>Mirror first, original release (source_direct) as fallback.</summary>
    async Task<byte[]> DownloadRepoFileAsync(RepoItem it, Action<string> progress)
    {
        Exception? last = null;
        var urls = string.IsNullOrEmpty(it.SourceDirect) ? new[] { it.Url } : new[] { it.Url, it.SourceDirect };
        foreach (var u in urls)
        {
            try { return await DownloadWithProgressAsync(u, it.Filename, progress); }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new IOException("download failed");
    }

    async Task GetRepoItemAsync(RepoItem it)
    {
        if (_repoBusy) return;
        _repoBusy = true;
        RefreshAllRepoStates();
        try
        {
            void Prog(string m)
            {
                RepoSay(m);
                RunOnUiThread(() => { try { if (it.StateView != null) it.StateView.Text = m; } catch { } });
            }
            byte[] bytes = await DownloadRepoFileAsync(it, Prog);
            if (!string.IsNullOrEmpty(it.Checksum))
            {
                string hex;
                try
                {
                    using var sha = System.Security.Cryptography.SHA256.Create();
                    hex = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
                }
                catch { hex = ""; }
                if (!string.IsNullOrEmpty(hex) && hex != it.Checksum.ToLowerInvariant())
                    throw new IOException("sha256 mismatch — retry");
            }
            string path = RepoFilePath(it.Filename);
            await Task.Run(() => File.WriteAllBytes(path, bytes));
            try { GetPreferences(FileCreationMode.Private).Edit().PutString("repo_ver_" + RepoSafeName(it.Filename), it.Version).Apply(); } catch { }
            RepoSay($"saved {it.Filename} ({bytes.Length / 1024} KB) ✓");
            Toast($"saved {it.Filename}");
        }
        catch (Exception ex) { RepoSay($"get failed: {Short(ex.Message)}"); Toast("download failed"); }
        finally
        {
            _repoBusy = false;
            UpdateRepoItemState(it);
            RefreshAllRepoStates();
        }
    }

    async Task GetAllRepoAsync()
    {
        if (_repoBusy) return;
        List<RepoItem> items;
        lock (_repo) items = new List<RepoItem>(_repo);
        if (items.Count == 0) { RepoSay("refresh the list first"); return; }
        var btn = _repoAllBtn;
        _repoBusy = true;
        if (btn != null) RunOnUiThread(() => btn.Enabled = false);
        RefreshAllRepoStates();
        try
        {
            int done = 0, skipped = 0, failed = 0;
            foreach (var it in items)
            {
                bool exists = File.Exists(RepoFilePath(it.Filename));
                string saved = SavedRepoVersion(it.Filename);
                if (exists && !string.IsNullOrEmpty(it.Version) && saved == it.Version) { skipped++; continue; }
                int n = done + failed + skipped + 1;
                try
                {
                    byte[] bytes = await DownloadRepoFileAsync(it, m => RepoSay($"[{n}/{items.Count}] {m}"));
                    if (!string.IsNullOrEmpty(it.Checksum))
                    {
                        string hex = "";
                        try
                        {
                            using var sha = System.Security.Cryptography.SHA256.Create();
                            hex = Convert.ToHexString(sha.ComputeHash(bytes)).ToLowerInvariant();
                        }
                        catch { }
                        if (!string.IsNullOrEmpty(hex) && hex != it.Checksum.ToLowerInvariant())
                            throw new IOException("sha256 mismatch");
                    }
                    await Task.Run(() => File.WriteAllBytes(RepoFilePath(it.Filename), bytes));
                    try { GetPreferences(FileCreationMode.Private).Edit().PutString("repo_ver_" + RepoSafeName(it.Filename), it.Version).Apply(); } catch { }
                    done++;
                }
                catch { failed++; }
                UpdateRepoItemState(it);
            }
            RepoSay($"done: {done} downloaded, {skipped} up-to-date, {failed} failed");
            Toast($"done: {done} new, {failed} failed");
        }
        finally
        {
            _repoBusy = false;
            if (btn != null) RunOnUiThread(() => btn.Enabled = true);
            RefreshAllRepoStates();
        }
    }

    async Task SendRepoItemAsync(RepoItem it)
    {
        if (_repoBusy) return;
        string path = RepoFilePath(it.Filename);
        if (!File.Exists(path)) { RepoSay("Get it first"); return; }
        if (!RepoConsoleTarget(out string ip, out int port)) { RepoSay("set the console IP first"); return; }
        _repoBusy = true;
        RefreshAllRepoStates();
        try
        {
            byte[] bytes = await Task.Run(() => File.ReadAllBytes(path));
            var (ok, msg) = await PushFileToPldmgrAsync(ip, port, RepoSafeName(it.Filename),
                bytes, m => RunOnUiThread(() => { try { if (it.StateView != null) it.StateView.Text = m; } catch { } }));
            RunOnUiThread(() => { try { if (it.StateView != null) it.StateView.Text = msg; } catch { } });
            RepoSay(ok ? $"{it.Filename} live ✓" : msg);
            Toast(ok ? $"{it.Filename} sent" : "send failed");
        }
        finally
        {
            _repoBusy = false;
            RefreshAllRepoStates();
        }
    }

    bool StartPldFileChooser(IValueCallback? cb)
    {
        try
        {
            try { _pldFileCb?.OnReceiveValue(null); } catch { }
            _pldFileCb = cb;
            var i = new Intent(Intent.ActionGetContent);
            i.AddCategory(Intent.CategoryOpenable);
            i.SetType("*/*");
            i.PutExtra(Intent.ExtraAllowMultiple, false);
            StartActivityForResult(Intent.CreateChooser(i, "Pick file"), FileChooserReq);
            return true;
        }
        catch { _pldFileCb = null; return false; }
    }

    public override void OnBackPressed()
    {
        try
        {
            if (_payloadPage?.Visibility == ViewStates.Visible && _pldWeb != null && _pldWeb.CanGoBack())
            {
                _pldWeb.GoBack();
                return;
            }
        }
        catch { }
        base.OnBackPressed();
    }

    void ShowLog()
    {
        var dlg = new BottomSheetDialog(this);
        var v = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(20);
        v.SetPadding(pad, pad, pad, pad);
        var t = new TextView(this) { Text = "Transfer log (copy to me if a send fails)" };
        t.TextSize = 16; t.SetTypeface(null, TypefaceStyle.Bold);
        var sv = new AndroidX.Core.Widget.NestedScrollView(this);
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

    void ShowAbout(bool thenGuide = false)
    {
        const string repo = "https://github.com/Loopayeh/pkg-sender";
        const string site = "https://loopayeh.github.io/";
        const string usdt = "0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50";
        const string trust = "https://link.trustwallet.com/send?coin=20000714&address=0x839a30D52Ef7D2b53e818b9931efd7FE6F472e50&token_id=0x55d398326f99059fF775485246999027B3197955";
        const string coffee = "https://coffeebede.com/loopayeh";

        var dlg = new BottomSheetDialog(this);
        var sv = new AndroidX.Core.Widget.NestedScrollView(this);
        var v = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(24);
        v.SetPadding(pad, pad, pad, pad);
        sv.AddView(v);

        // header: logo + name + version
        var head = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        head.SetGravity(GravityFlags.CenterVertical);
        var him = new ImageView(this);
        him.LayoutParameters = new LinearLayout.LayoutParams(Dp(48), Dp(48));
        him.SetScaleType(ImageView.ScaleType.CenterCrop);
        try
        {
            using var s = GetType().Assembly.GetManifestResourceStream("PkgSender.Droid.logo.png");
            if (s != null)
                using (var bmp = BitmapFactory.DecodeStream(s))
                    if (bmp != null)
                        him.SetImageBitmap(Bitmap.CreateScaledBitmap(bmp, Dp(48), Dp(48), true));
        }
        catch { }
        head.AddView(him);
        var ht = new LinearLayout(this) { Orientation = Orientation.Vertical };
        var htp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        htp.LeftMargin = Dp(14);
        ht.LayoutParameters = htp;
        var t = new TextView(this) { Text = "LoopFlow" };
        t.TextSize = 20; t.SetTypeface(null, TypefaceStyle.Bold);
        var ver = new TextView(this) { Text = "1.0.1 (Android) • by Loopayeh" };
        ver.SetTextColor(_subColor); ver.TextSize = 13;
        ht.AddView(t); ht.AddView(ver);
        head.AddView(ht);
        v.AddView(head);

        var b = new TextView(this)
        {
            Text = "Installs PS4/PS5 games over LAN.\nRun pkg-receiver.elf on PS5, or Remote Package Installer / GoldHEN on PS4."
        };
        b.SetTextColor(_subColor); b.TextSize = 14;
        var bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        bp.TopMargin = Dp(12); bp.BottomMargin = Dp(4);
        b.LayoutParameters = bp;
        v.AddView(b);

        v.AddView(SectionLabel("Links"));
        v.AddView(LinkRow("GitHub — source & releases", repo));
        v.AddView(LinkRow("Website — more projects", site));

        v.AddView(SectionLabel("Support the project"));
        var dn = new TextView(this) { Text = "If you enjoy this app, a small donation keeps it going." };
        dn.SetTextColor(_subColor); dn.TextSize = 13;
        v.AddView(dn);
        var addr = new TextView(this) { Text = "USDT (BEP-20)\n" + usdt };
        addr.SetTextIsSelectable(true);
        addr.Typeface = Android.Graphics.Typeface.Monospace;
        addr.TextSize = 12;
        var ap = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        ap.TopMargin = Dp(8);
        addr.LayoutParameters = ap;
        v.AddView(addr);
        var drow = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        var dp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        dp.TopMargin = Dp(10); dp.BottomMargin = Dp(16);
        drow.LayoutParameters = dp;
        var copyBtn = TonalBtn("Copy address", () =>
        {
            try
            {
                var cm = (ClipboardManager?)GetSystemService(ClipboardService);
                cm!.PrimaryClip = ClipData.NewPlainText("USDT", usdt);
                Say("donate address copied");
            }
            catch { }
        });
        copyBtn.LayoutParameters = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        var twBtn = TonalBtn("TrustWallet", () =>
        {
            try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(trust))); } catch { }
        });
        var twp = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WrapContent, 1f);
        twp.LeftMargin = Dp(8);
        twBtn.LayoutParameters = twp;
        drow.AddView(copyBtn); drow.AddView(twBtn);
        v.AddView(drow);
        v.AddView(LinkRow("☕ حمایت تومانی — coffeebede.com/loopayeh", coffee));

        var close = FilledBtn("Close", () => dlg.Dismiss());
        v.AddView(close);
        dlg.SetContentView(sv);
        if (thenGuide)
        {
            bool chained = false;
            dlg.DismissEvent += (_, _) =>
            {
                if (chained) return;
                chained = true;
                try
                {
                    // let the About sheet fully tear down first —
                    // showing Guide instantly on dismiss wedges its scroll
                    new Handler(Looper.MainLooper!).PostDelayed(() =>
                    {
                        try { ShowGuide(); } catch { }
                    }, 400);
                }
                catch { try { ShowGuide(); } catch { } }
            };
        }
        dlg.Show();
    }

    void ShowGuide()
    {
        var dlg = new BottomSheetDialog(this);
        var sv = new AndroidX.Core.Widget.NestedScrollView(this);
        var v = new LinearLayout(this) { Orientation = Orientation.Vertical };
        int pad = Dp(24);
        v.SetPadding(pad, pad, pad, pad);
        sv.AddView(v);

        var t = new TextView(this) { Text = "Setup guide • راهنمای اتصال" };
        t.TextSize = 20; t.SetTypeface(null, TypefaceStyle.Bold);
        v.AddView(t);

        var b = new TextView(this)
        {
            Text = "BEST SETUP (recommended)\n"
                + "1) Connect the console to the modem with a LAN cable — not Wi-Fi. Much faster and more stable.\n\n"
                + "2) Connect the phone to the same modem's Wi-Fi (5GHz preferred). Phone and console must be on the same network (e.g. both 192.168.1.x).\n\n"
                + "3) On PS5, run the exploit first, then load pkg-receiver.elf (it's bundled here — tap Send ELF on the pkg-receiver.elf card; PLDMGR must be running). On PS4, open Remote Package Installer and keep it in focus (minimize only after \"waiting to install\" is done). With GoldHEN: Settings → GoldHEN → Server Settings → enable the servers, then Test will find the console.\n\n"
                + "4) Type the console IP above and tap Test. Green = connected.\n\n"
                + "5) Tap + Add PKG / Image, tick the games, then Send queue. PKGs install on the console; disc images are copied to /data/homebrew. Keep the phone awake and don't leave the app mid-transfer.\n\n"
                + "6) Payloads tab: opens the Payload Manager (PLDMGR) web dashboard running on your console — same console IP, port 8084 by default (change it for other tools, e.g. 9200). No IP? Tap ⌕ Scan. If our receiver isn't running yet, tap ⬆ Our ELF: it uploads pkg-receiver.elf to PLDMGR and launches it, no loader port needed. The dashboard's own Upload button works too — pick any ELF from the phone.\n\n"
                + "7) Repo tab: cloud payload list (same source PLDMGR uses) downloaded with the phone's internet — perfect for an offline console. ⬇ Get saves a payload on the phone (sha-checked), ⬆ Send pushes it to the console through PLDMGR. ⬇ Get all grabs every missing/update.\n\n"
                + "Tip: if Test can't reach the console, check the IP, and make sure the modem lets Wi-Fi devices talk to each other (a modem setting called AP/Client Isolation must be OFF).\n\n"
                + "————————————————\n\n"
                + "بهترین حالت (پیشنهادی)\n"
                + "۱) کنسول را با کابل لن (LAN) به مودم وصل کن — نه وای‌فای. سرعت و پایداری خیلی بالاتر میره.\n\n"
                + "۲) گوشی را به وای‌فای همان مودم وصل کن (ترجیحاً باند 5GHz). گوشی و کنسول باید توی یک شبکه باشن (مثلاً هر دو 192.168.1.x).\n\n"
                + "۳) روی PS5 اول اکسپلویت را اجرا کن و pkg-receiver.elf را بفرست بالا (از کارت pkg-receiver.elf دکمه Send ELF را بزن — باید PLDMGR بالا باشه). روی PS4 برنامه Remote Package Installer را باز کن و بذار جلو بمونه (بعد از شروع نصب می‌تونی مینیمایزش کنی). با گلدHEN: برو توی Settings ← GoldHEN ← Server Settings و سرورها (Payload/BinLoader Server) را روشن کن، بعد Test کنسول را پیدا می‌کنه.\n\n"
                + "۴) آی‌پی کنسول را بالا وارد کن و Test را بزن. سبز شد یعنی وصله.\n\n"
                + "۵) با + Add PKG / Image بازی اضافه کن (PKG یا ایمیج دیسک — ایمیج‌ها توی /data/homebrew کپی می‌شن)، تیک بزن و Send queue را بزن. وسط انتقال گوشی را خاموش نکن و از برنامه بیرون نرو.\n\n"
                + "۶) تب Payloads: داشبورد وب Payload Manager (PLDMGR) روی کنسولت را باز می‌کنه — با همان آی‌پی کنسول، پورت پیش‌فرض 8084 (برای ابزار دیگه عوضش کن، مثلاً 9200). آی‌پی نداری؟ ⌕ Scan را بزن. اگه رسیور ما هنوز بالا نیست، ⬆ Our ELF را بزن: خودش pkg-receiver.elf را به PLDMGR آپلود و اجرا می‌کنه، بدون نیاز به پورت لودر. دکمه Upload خود داشبورد هم کار می‌کنه — هر ELFای از گوشی انتخاب کن.\n\n"
                + "۷) تب Repo: لیست پیلودهای ابری (همون منبعی که PLDMGR استفاده می‌کنه) با اینترنت گوشی دانلود می‌شه — عالی برای کنسول آفلاین. ⬇ Get پیلود رو روی گوشی ذخیره می‌کنه (با چک sha)، ⬆ Send از طریق PLDMGR می‌فرستش رو کنسول. ⬇ Get all همه ناقص‌ها/آپدیت‌ها رو یکجا می‌گیره.\n\n"
                + "نکته: اگه Test وصل نشد، آی‌پی را چک کن و مطمئن شو مودم اجازه می‌ده دستگاه‌های وای‌فای همدیگه رو ببینن (تو تنظیمات وای‌فای مودم گزینه‌ای به اسم AP/Client Isolation هست — باید خاموش باشه)."
        };
        b.SetTextColor(_subColor); b.TextSize = 14;
        var bp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        bp.TopMargin = Dp(12); bp.BottomMargin = Dp(12);
        b.LayoutParameters = bp;
        v.AddView(b);

        var close = FilledBtn("Close", () => dlg.Dismiss());
        v.AddView(close);
        dlg.SetContentView(sv);
        dlg.Show();
    }

    TextView SectionLabel(string s)
    {
        var l = new TextView(this) { Text = s };
        l.TextSize = 13; l.SetTypeface(null, TypefaceStyle.Bold);
        try { l.SetTextColor(Dyn("colorPrimary", Color.Gray)); } catch { }
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.TopMargin = Dp(16); lp.BottomMargin = Dp(4);
        l.LayoutParameters = lp;
        return l;
    }

    TextView LinkRow(string label, string url)
    {
        var l = new TextView(this) { Text = label + "\n" + url };
        l.TextSize = 14;
        try { l.SetTextColor(Dyn("colorPrimary", Color.Blue)); } catch { }
        l.SetTextIsSelectable(true);
        l.Clickable = true;
        l.Click += (_, _) =>
        {
            try { StartActivity(new Intent(Intent.ActionView, Android.Net.Uri.Parse(url))); } catch { }
        };
        var lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.WrapContent);
        lp.TopMargin = Dp(4); lp.BottomMargin = Dp(4);
        l.LayoutParameters = lp;
        return l;
    }

    // ---------- bundled ELF: copy to phone storage ----------

    void ElfSay(bool? ok, string s) => RunOnUiThread(() =>
    {
        try
        {
            if (_elfStatus == null) return;
            _elfStatus.Text = s;
            _elfStatus.Visibility = string.IsNullOrEmpty(s) ? ViewStates.Gone : ViewStates.Visible;
            _elfStatus.SetTextColor(ok == true ? Color.ParseColor("#1B7A2E")
                : ok == false ? Color.ParseColor("#C62828") : _subColor);
        }
        catch { }
    });

    string ReadBundledElf(out string name)
    {
        name = "pkg-receiver.elf";
        var emb = GetType().Assembly.GetManifestResourceStream("PkgSender.Droid.pkg-receiver.elf")
            ?? throw new IOException("bundled ELF missing");
        using (emb)
        using (var ms = new MemoryStream())
        {
            emb.CopyTo(ms);
            string tmp = System.IO.Path.Combine(CacheDir!.AbsolutePath, name);
            File.WriteAllBytes(tmp, ms.ToArray());
            return tmp;
        }
    }

    async Task ShareElfAsync()
    {
        try
        {
            string tmp = await Task.Run(() => ReadBundledElf(out _));
            string fileName = "pkg-receiver.elf";
            Android.Net.Uri shareUri;
            if ((int)Build.VERSION.SdkInt >= 29)
            {
                var cv = new ContentValues();
                cv.Put(Android.Provider.MediaStore.MediaColumns.DisplayName, fileName);
                cv.Put(Android.Provider.MediaStore.MediaColumns.MimeType, "application/octet-stream");
                cv.Put(Android.Provider.MediaStore.MediaColumns.RelativePath, "Download/");
                shareUri = ContentResolver!.Insert(
                    Android.Provider.MediaStore.Downloads.ExternalContentUri!, cv)
                    ?? throw new IOException("mediastore insert failed");
                using (var outS = ContentResolver!.OpenOutputStream(shareUri)!)
                using (var inS = File.OpenRead(tmp))
                    await inS.CopyToAsync(outS);
            }
            else
            {
#pragma warning disable CS0618
                string dst = System.IO.Path.Combine(
                    Android.OS.Environment.GetExternalStoragePublicDirectory(
                        Android.OS.Environment.DirectoryDownloads)!.AbsolutePath, fileName);
                using (var outS = File.Create(dst))
                using (var inS = File.OpenRead(tmp))
                    await inS.CopyToAsync(outS);
#pragma warning restore CS0618
                shareUri = Android.Net.Uri.FromFile(new Java.IO.File(dst));
            }
            var i = new Intent(Intent.ActionSend);
            i.SetType("application/octet-stream");
            i.PutExtra(Intent.ExtraStream, shareUri);
            i.AddFlags(ActivityFlags.GrantReadUriPermission);
            StartActivity(Intent.CreateChooser(i, "Share pkg-receiver.elf"));
            ElfSay(true, "share sheet opened — pick USB / messenger / drive");
        }
        catch (Exception ex) { ElfSay(false, "share failed: " + Short(ex.Message)); }
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
        if (requestCode == FileChooserReq)
        {
            try
            {
                var cb = _pldFileCb;
                _pldFileCb = null;
                if (cb == null) return;
                if (resultCode == Result.Ok && data != null)
                    cb.OnReceiveValue(WebChromeClient.FileChooserParams.ParseResult((int)resultCode, data));
                else
                    cb.OnReceiveValue(null);
            }
            catch { }
            return;
        }
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
            var e = new TextView(this) { Text = "empty — + Add PKG / Image to stage games (PKG or disc image) from your phone" };
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
        if (_detectBtn != null) _detectBtn.Enabled = false;
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
            RunOnUiThread(() => { _sendBtn.Enabled = true; _testBtn!.Enabled = true; if (_detectBtn != null) _detectBtn.Enabled = true; RefreshLib(); });
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

    /// <summary>
    /// Auto-discover the console: listen for receiver UDP beacons
    /// ("PKGSENDER...") on 12801, fill the IP field and Test it.
    /// No need to read the IP off the console screen anymore.
    /// </summary>
    async Task DetectAsync()
    {
        var det = _detectBtn;
        if (det != null) RunOnUiThread(() => det.Enabled = false);
        try
        {
            Say("listening for console beacons…");
            SetConn(null, "detecting…");
            string? ip = await Task.Run(() => ListenForBeacon(TimeSpan.FromSeconds(6)));
            if (string.IsNullOrEmpty(ip))
            {
                SetConn(false, "no beacon — type IP manually");
                Say("no beacon heard: console off / other network / AP isolation. Type the IP and Test.");
                return;
            }
            RunOnUiThread(() => { if (_psIp != null) _psIp.Text = ip; });
            GetPreferences(FileCreationMode.Private).Edit().PutString("psip", ip).Apply();
            Say($"found console at {ip} — testing…");
            await TestAsync();
        }
        finally { if (det != null) RunOnUiThread(() => det.Enabled = true); }
    }

    static string? ListenForBeacon(TimeSpan wait)
    {
        try
        {
            using var udp = new System.Net.Sockets.UdpClient(12801);
            udp.Client.ReceiveTimeout = 500;
            var deadline = DateTime.UtcNow + wait;
            var seen = new System.Collections.Generic.HashSet<string>();
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    var ep = new System.Net.IPEndPoint(System.Net.IPAddress.Any, 0);
                    byte[] buf = udp.Receive(ref ep);
                    string msg = System.Text.Encoding.ASCII.GetString(buf);
                    if (!msg.StartsWith("PKGSENDER", StringComparison.Ordinal)) continue;
                    if (System.Net.IPAddress.IsLoopback(ep.Address)) continue;
                    // prefer the IP embedded in the beacon tail ("PKGSENDER v1 192.168.x.x")
                    string ip = ep.Address.ToString();
                    foreach (var part in msg.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                        if (System.Net.IPAddress.TryParse(part, out var a) && !System.Net.IPAddress.IsLoopback(a))
                        { ip = a.ToString(); break; }
                    if (seen.Add(ip)) return ip;
                }
                catch (System.Net.Sockets.SocketException) { }
            }
        }
        catch { }
        return null;
    }

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
        try { _pldWeb?.Destroy(); } catch { }
        base.OnDestroy();
    }
}
