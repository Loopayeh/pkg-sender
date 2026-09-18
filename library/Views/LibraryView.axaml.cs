using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LoopDPI.Core;
using PkgSender.ViewModels;

namespace PkgSender.Views;

public partial class LibraryView : UserControl
{
    private readonly LibraryViewModel _m = new();
    private readonly List<GameItem> _all = new();
    private List<string> _roots = new();
    // One persistent file server for the whole app lifetime: re-creating it
    // per send would kill the console's in-flight download of the previous
    // PKG. New files get globally-unique ids, so parallel queues never clash.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _registry = new();
    private RangeFileServer? _server;
    private long _nextId;
    // Stable url-id per local file: re-pushing the same file reuses its URL,
    // so the console RESUMES instead of starting over.
    private readonly Dictionary<string, string> _pathIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<QueueItem> _runQueue = new();
    private readonly object _runLock = new();
    private bool _running;
    private volatile bool _stop;
    // Zero-config networking: real NIC subnets, beacon-first discovery.
    private List<LoopDPI.Core.LanNetwork> _nets = new();
    private readonly Avalonia.Threading.DispatcherTimer _liveTimer = new();
    private bool _liveBusy;
    private bool _detecting;

    public LibraryView()
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = _m;
        var s = AppSettings.Load();
        _m.PsIp = s.PsIp;
        _m.PcIp = s.PcIp;
        _m.RemoteDir = s.RemoteDir;
        _roots = LibraryScanner.LoadRoots();
        _m.Search = "";
        var platformBox = this.FindControl<ComboBox>("PlatformBox");
        platformBox.ItemsSource = new List<string> { "All", "PS5", "PS4" };
        platformBox.SelectedIndex = 0;
        platformBox.SelectionChanged += (_, _) =>
        {
            _m.PlatformFilter = platformBox.SelectedItem as string ?? "All";
            ApplyFilter();
        };
        var sortBox = this.FindControl<ComboBox>("SortBox");
        sortBox.ItemsSource = new List<string> { "Name", "Size ↓", "Size ↑" };
        sortBox.SelectedIndex = 0;
        sortBox.SelectionChanged += (_, _) =>
        {
            _m.SortMode = sortBox.SelectedItem as string ?? "Name";
            ApplyFilter();
        };
        _m.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LibraryViewModel.Search))
                ApplyFilter();
        };
        this.FindControl<Button>("BtnAddFolder").Click += async (_, _) => await AddFolderAsync("Add a folder to scan for games");
        // Auto-detect LAN addresses: PC IP is picked, never typed.
        _nets = LoopDPI.Core.NetDiscovery.GetLanNetworks();
        RefreshPcIps(selectForPs: _m.PsIp);
        this.FindControl<Avalonia.Controls.ComboBox>("PcIpBox").SelectionChanged += (_, _) =>
        {
            var box = this.FindControl<Avalonia.Controls.ComboBox>("PcIpBox");
            string addr = AddrOf(box.SelectedItem as string);
            if (!string.IsNullOrEmpty(addr))
            {
                _m.PcIp = addr;
                var keep = AppSettings.Load();
                keep.PsIp = _m.PsIp;
                keep.PcIp = _m.PcIp;
                keep.Save();
            }
        };
        if (_nets.Count == 0)
            _m.Status = "No network adapter — check cable/Wi-Fi.";
        this.FindControl<Button>("BtnGuide").Click += async (_, _) =>
        {
            var owner = Top as Window;
            if (owner != null)
                await new GuideWindow().ShowDialog(owner);
        };
        this.FindControl<Button>("BtnRedetect").Click += async (_, _) => await AutoDetectAsync();
        _liveTimer.Interval = TimeSpan.FromSeconds(5);
        _liveTimer.Tick += async (_, _) => await LiveProbeAsync();
        _liveTimer.Start();
        _ = LiveProbeAsync();
        _ = AutoDetectAsync();
        this.FindControl<Button>("BtnAddDrive").Click += async (_, _) => await AddDriveAsync();
        this.FindControl<Button>("BtnTest").Click += async (_, _) => await TestConnectionAsync();
        this.FindControl<Button>("BtnScan").Click += async (_, _) => await ScanAsync();
        this.FindControl<Button>("BtnSend").Click += (_, _) => EnqueuePkgs();
        this.FindControl<Button>("BtnAbout").Click += async (_, _) =>
        {
            var owner = Top as Window;
            if (owner != null)
                await new AboutWindow().ShowDialog(owner);
        };
        this.FindControl<Button>("BtnUpdate").Click += async (_, _) => await CheckUpdatesAsync(manual: true);
        if (s.UpdateCheck)
            _ = CheckUpdatesAsync(manual: false);
        this.FindControl<Button>("BtnStop").Click += (_, _) =>
        {
            lock (_runLock)
            {
                _stop = true; // worker aborts waits, rows stay visible
            }
            _m.Status = "Stopping…";
        };
        this.FindControl<Button>("BtnClearDone").Click += (_, _) =>
        {
            // Finished rows only: an active download is never touched.
            for (int i = _m.Queue.Count - 1; i >= 0; i--)
            {
                string st = _m.Queue[i].State;
                if (st == "sent" || st == "failed")
                    _m.Queue.RemoveAt(i);
            }
            UpdateQueueLabel();
        };
        this.FindControl<ListBox>("GamesList").SelectionChanged += (_, _) =>
        {
            var box = this.FindControl<ListBox>("GamesList");
            var selected = new HashSet<GameItem>(
                box.SelectedItems?.Cast<GameItem>() ?? Enumerable.Empty<GameItem>());
            foreach (var g in _all)
                g.IsSelected = selected.Contains(g);
            UpdateGamesLabel();
        };
        // Per-row Resume buttons live inside the queue DataTemplate.
        this.FindControl<ListBox>("QueueList").AddHandler(Button.ClickEvent, OnQueueButtonClick);
        _ = ScanAsync();
    }

    private static string AddrOf(string? item)
    {
        if (string.IsNullOrWhiteSpace(item)) return "";
        int sp = item.IndexOf(' ');
        return (sp < 0 ? item : item[..sp]).Trim();
    }

    private void RefreshPcIps(string? selectForPs)
    {
        _m.PcIps.Clear();
        foreach (var n in _nets)
            _m.PcIps.Add($"{n.Address}  ({n.InterfaceName} /{n.PrefixLength})");
        var box = this.FindControl<ComboBox>("PcIpBox");
        box.ItemsSource = _m.PcIps;
        string? best = LoopDPI.Core.NetDiscovery.BestPcIpFor(_nets, selectForPs);
        if (!string.IsNullOrEmpty(best))
        {
            _m.PcIp = best;
            for (int i = 0; i < _m.PcIps.Count; i++)
                if (AddrOf(_m.PcIps[i]) == best) { box.SelectedIndex = i; break; }
        }
        else if (_m.PcIps.Count > 0 && box.SelectedIndex < 0)
        {
            box.SelectedIndex = 0;
            _m.PcIp = AddrOf(_m.PcIps[0]);
        }
    }

    /// <summary>
    /// Startup auto-detect (no manual search): saved address first, then
    /// beacon+sweep. If a console answers at a DIFFERENT address, offer
    /// to switch — like the original sender did.
    /// </summary>
    private async Task AutoDetectAsync()
    {
        if (_detecting)
            return;
        _detecting = true;
        try
        {
            _nets = LoopDPI.Core.NetDiscovery.GetLanNetworks();
            RefreshPcIps(selectForPs: _m.PsIp);
            if (_nets.Count == 0)
            {
                Post(() => _m.Status = "No network adapter — check cable/Wi-Fi.");
                return;
            }
            string saved = (_m.PsIp ?? "").Trim();
            if (!string.IsNullOrEmpty(saved))
            {
                var (savedOk, _) = await LoopDPI.Core.NetDiscovery.ProbeAsync(saved);
                if (savedOk)
                {
                    Post(() => _m.Status = $"Console at {saved}.");
                    return;
                }
            }
            Post(() => _m.Status = "Looking for console…");
            var prog = new Progress<string>(s => Post(() => _m.Status = s));
            var found = await LoopDPI.Core.NetDiscovery.FindConsolesAsync(
                _nets, TimeSpan.FromSeconds(4),
                p => ((System.IProgress<string>)prog).Report(p));
            var verified = found.Where(c => c.Source is "beacon" or "sweep").ToList();
            if (verified.Any(c => c.Ip == saved))
            {
                Post(() => _m.Status = $"Console at {saved}.");
                return;
            }
            if (verified.Count == 0)
            {
                Post(() => _m.Status = string.IsNullOrEmpty(saved)
                    ? "No console on the LAN — run pkg-receiver on the console (see Guide)."
                    : $"No receiver at {saved} — is pkg-receiver running? (see Guide).");
                return;
            }
            var pick = verified[0];
            var owner = Top as Window;
            if (owner == null)
                return;
            var dlg = new SwitchDialog(string.IsNullOrEmpty(saved) ? "(none)" : saved, pick.Ip, pick.Source);
            await dlg.ShowDialog(owner);
            if (dlg.Switch)
            {
                ApplyConsole(pick);
                _m.Status = $"Switched to console at {pick.Ip}.";
            }
            else
            {
                _m.Status = $"Kept {saved}.";
            }
        }
        finally
        {
            _detecting = false;
            _ = LiveProbeAsync();
        }
    }

    private void ApplyConsole(LoopDPI.Core.ConsoleFound c)
    {
        _m.PsIp = c.Ip;
        string? pc = LoopDPI.Core.NetDiscovery.BestPcIpFor(_nets, c.Ip);
        if (!string.IsNullOrEmpty(pc))
            _m.PcIp = pc;
        RefreshPcIps(selectForPs: _m.PsIp);
        var keep = AppSettings.Load();
        keep.PsIp = _m.PsIp;
        keep.PcIp = _m.PcIp;
        keep.Save();
        _ = LiveProbeAsync();
    }

    /// <summary>Live receiver dot: green connected, red no receiver, gray no network.</summary>
    private async Task LiveProbeAsync()
    {
        if (_liveBusy || _detecting)
            return;
        var dot = this.FindControl<TextBlock>("TestResultText");
        if (_nets.Count == 0)
        {
            Post(() =>
            {
                _m.TestResult = "○ No network";
                dot.Foreground = new SolidColorBrush(Color.Parse("#8B93A5"));
            });
            return;
        }
        string ps = (_m.PsIp ?? "").Trim();
        if (string.IsNullOrEmpty(ps))
            return;
        _liveBusy = true;
        try
        {
            var (apiOk, busy) = await LoopDPI.Core.NetDiscovery.ProbeAsync(ps);
            Post(() =>
            {
                if (!apiOk)
                {
                    _m.TestResult = "● No receiver";
                    dot.Foreground = new SolidColorBrush(Color.Parse("#E06C5B"));
                }
                else
                {
                    _m.TestResult = busy == true ? "● Connected (busy)" : "● Connected";
                    dot.Foreground = new SolidColorBrush(Color.Parse("#6FCF7B"));
                }
            });
        }
        finally { _liveBusy = false; }
    }

    private void OnQueueButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)    {
        if (e.Source is Button { DataContext: QueueItem qi })
            ResumeRow(qi);
    }

    /// <summary>
    /// Resume a stopped/failed-download row WITHOUT a new push: the same URL
    /// is served again (counter kept), so the console continues from its last
    /// byte — resume it from the console Downloads.
    /// </summary>
    private void ResumeRow(QueueItem row)
    {
        if (row.Game == null)
            return;
        string id;
        lock (_runLock)
        {
            if (!_pathIds.TryGetValue(row.Game.Path, out id!))
                return;
            _stop = false;
            _activeIds[row] = id;
            _activeIdle[row] = 0;
            _activeSince[row] = DateTime.UtcNow;
            row.CanResume = false;
            if (!_running)
            {
                _running = true;
                _ = RunQueueAsync();
            }
        }
        _server?.Unrevoke(id);
        Post(() =>
        {
            row.State = "sending";
            row.Message = "resuming… (resume it on the console too)";
            UpdateQueueLabel();
        });
    }

    private TopLevel Top => TopLevel.GetTopLevel(this)!;

    private void Post(Action a) => Dispatcher.UIThread.Post(a);

    private async Task AddDriveAsync()
    {
        var owner = Top as Window;
        if (owner == null)
            return;
        var dlg = new DrivePickerWindow(_roots);
        var picked = await dlg.ShowDialog<List<string>?>(owner);
        if (picked == null)
            return; // cancelled
        var pickedSet = new HashSet<string>(picked, StringComparer.OrdinalIgnoreCase);
        // Replace drive roots with the dialog result, keep folder roots.
        var next = _roots.Where(r => !IsDriveRoot(r)).ToList();
        foreach (var p in picked)
        {
            if (!next.Contains(p, StringComparer.OrdinalIgnoreCase))
                next.Add(p);
        }
        _roots = next;
        LibraryScanner.SaveRoots(_roots);
        await ScanAsync();
    }

    private static bool IsDriveRoot(string path)
    {
        try
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
            return full.Length == 2 && full[1] == ':'; // e.g. "E:"
        }
        catch
        {
            return false;
        }
    }

    private async Task AddFolderAsync(string title)
    {
        var folders = await Top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
        });
        bool added = false;
        foreach (var f in folders)
        {
            string p = f.Path.LocalPath;
            if (Directory.Exists(p) && !_roots.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                _roots.Add(p);
                added = true;
            }
        }
        if (added)
        {
            LibraryScanner.SaveRoots(_roots);
            await ScanAsync();
        }
    }

    private async Task ScanAsync()
    {
        if (_m.IsBusy)
            return;
        _m.IsBusy = true;
        _m.Status = _roots.Count == 0 ? "Add a folder or drives first." : "Scanning…";
        try
        {
            var prog = new Progress<string>(s => Post(() => _m.Status = s));
            var found = await LibraryScanner.ScanAsync(_roots, prog);
            Post(() =>
            {
                _all.Clear();
                foreach (var g in found)
                {
                    // PKG-only mode: skip images/folders (kept in cache for later).
                    if (g.Info.Format != "pkg" || g.Info.IsFolder)
                        continue;
                    string gid = !string.IsNullOrWhiteSpace(g.Info.TitleId)
                        ? g.Info.TitleId : g.Info.ContentId;
                    string gver = string.IsNullOrWhiteSpace(g.Info.Version)
                        ? "" : " • v" + g.Info.Version.TrimStart('v', 'V');
                    _all.Add(new GameItem
                    {
                        Path = g.Path,
                        Title = string.IsNullOrWhiteSpace(g.Info.Title) ? Path.GetFileName(g.Path) : g.Info.Title,
                        Meta = gid + gver,
                        SizeText = Program.FormatSize(g.Info.PackageSize),
                        SizeBytes = g.Info.PackageSize,
                        Platform = g.Info.Format == "pkg"
                            ? (string.IsNullOrWhiteSpace(g.Info.Platform) ? "PKG" : g.Info.Platform)
                            : $"{(string.IsNullOrWhiteSpace(g.Info.Platform) ? "PS5" : g.Info.Platform)} • {g.Info.Format}",
                        Format = string.IsNullOrEmpty(g.Info.Format) ? "pkg" : g.Info.Format,
                        IsFolder = g.Info.IsFolder,
                        ContentId = g.Info.ContentId,
                        Cover = g.Info.Cover,
                        IconData = g.Info.IconData,
                        IsPs5 = (g.Info.Platform ?? "").StartsWith("PS5"),
                        IsPs4 = (g.Info.Platform ?? "").StartsWith("PS4"),
                        IsDlc = g.Info.IsDlc,
                        CardRadius = new CornerRadius(2),
                        ImageRadius = new CornerRadius((g.Info.Platform ?? "").StartsWith("PS5") ? 16 : 2),
                    });
                }
                ApplyFilter();
                _m.Status = _all.Count == 0
                    ? (_roots.Count == 0 ? "Add a folder or drives first." : "No PKG files found.")
                    : $"{_all.Count} games in library.";
            });
        }
        finally
        {
            Post(() => _m.IsBusy = false);
        }
    }

    private async Task TestConnectionAsync()
    {
        if (_m.IsBusy)
            return;
        _m.IsBusy = true;
        Post(() =>
        {
            _m.Status = $"Testing {_m.PsIp}:12800…";
            _m.TestResult = "Testing…";
            this.FindControl<TextBlock>("TestResultText").Foreground = new SolidColorBrush(Color.Parse("#8B93A5"));
        });
        try
        {
            bool online = await ConsoleClient.IsOnlineAsync(_m.PsIp);
            Post(() =>
            {
                _m.Status = online
                    ? $"Connected — pkg-receiver is online at {_m.PsIp}:12800."
                    : $"No connection — nothing answers at {_m.PsIp}:12800. Is pkg-receiver running on the console?";
                _m.TestResult = online ? "● Connected" : "● No connection";
                this.FindControl<TextBlock>("TestResultText").Foreground = online
                    ? new SolidColorBrush(Color.Parse("#6FCF7B"))
                    : new SolidColorBrush(Color.Parse("#E06C5B"));
            });
        }
        finally
        {
            Post(() => _m.IsBusy = false);
        }
    }

    private void ApplyFilter()
    {
        string q = (_m.Search ?? "").Trim().ToLowerInvariant();
        string pf = _m.PlatformFilter ?? "All";
        var list = new List<GameItem>();
        foreach (var g in _all)
        {
            if ((pf == "PS5" && !g.Platform.StartsWith("PS5")) ||
                (pf == "PS4" && !g.Platform.StartsWith("PS4")))
                continue;
            if (q.Length == 0 || g.Title.ToLowerInvariant().Contains(q) ||
                g.ContentId.ToLowerInvariant().Contains(q) ||
                Path.GetFileName(g.Path).ToLowerInvariant().Contains(q))
                list.Add(g);
        }
        IEnumerable<GameItem> ordered = _m.SortMode switch
        {
            "Size ↓" => list.OrderByDescending(g => g.SizeBytes).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            "Size ↑" => list.OrderBy(g => g.SizeBytes).ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
            _ => list.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase),
        };
        _m.Games.Clear();
        foreach (var g in ordered)
            _m.Games.Add(g);
        UpdateGamesLabel();
    }

    private void UpdateGamesLabel()
    {
        int ps5 = _all.Count(g => g.Platform.StartsWith("PS5"));
        int ps4 = _all.Count(g => g.Platform.StartsWith("PS4"));
        var sel = this.FindControl<ListBox>("GamesList").SelectedItems;
        int selCount = sel?.Count ?? 0;
        string scope = (_m.PlatformFilter ?? "All") switch
        {
            "PS5" => $"{_m.Games.Count} PS5 games",
            "PS4" => $"{_m.Games.Count} PS4 games",
            _ => $"{_all.Count} games ({ps5} PS5 • {ps4} PS4)",
        };
        _m.GamesLabel = selCount > 0 ? $"{scope} • {selCount} selected" : scope;
    }

    private ListBox GamesBox => this.FindControl<ListBox>("GamesList");

    private void UpdateQueueLabel()
    {
        int done = _m.Queue.Count(q => q.State == "sent");
        int failed = _m.Queue.Count(q => q.State == "failed");
        _m.QueueLabel = _m.Queue.Count == 0 ? "Queue is empty" : $"{done}/{_m.Queue.Count} sent" + (failed > 0 ? $", {failed} failed" : "");
    }

    private static bool IsPkg(GameItem g) => g.Format == "pkg" && !g.IsFolder;

    /// <summary>
    /// Queue the selection for install. Never blocks a running queue:
    /// new items just append and the console takes them in order.
    /// </summary>
    private void EnqueuePkgs()
    {
        var picked =
            GamesBox.SelectedItems?.Cast<GameItem>().ToList() ?? new();
        if (picked.Count == 0)
        {
            _m.Status = "Select games first (Ctrl+click / Shift+click).";
            return;
        }
        var wanted = picked.Where(IsPkg).ToList();
        if (wanted.Count == 0)
        {
            _m.Status = "No .pkg in the selection — pick PKG files to install.";
            return;
        }
        // Don't double-push a file that's already queued/downloading; use its
        // Resume button instead (same URL -> console continues, not restarts).
        HashSet<string> busy;
        lock (_runLock)
        {
            busy = new HashSet<string>(
                _runQueue.Select(q => q.Game.Path).Concat(_activeIds.Keys.Select(q => q.Game.Path)),
                StringComparer.OrdinalIgnoreCase);
        }
        var fresh = wanted.Where(g => !busy.Contains(g.Path)).ToList();
        if (fresh.Count == 0)
        {
            _m.Status = "Already in queue — use Resume on its row for stopped items.";
            return;
        }
        // Re-installing a stopped row = resume it (same URL, no duplicate row).
        var resumable = new Dictionary<string, QueueItem>(StringComparer.OrdinalIgnoreCase);
        lock (_runLock)
        {
            foreach (var q in _m.Queue)
            {
                if (q.CanResume && q.Game != null && !resumable.ContainsKey(q.Game.Path))
                    resumable[q.Game.Path] = q;
            }
        }
        var toAdd = new List<GameItem>();
        int resumed = 0;
        foreach (var g in fresh)
        {
            if (resumable.TryGetValue(g.Path, out var row))
            {
                resumed++;
                ResumeRow(row);
            }
            else
            {
                toAdd.Add(g);
            }
        }
        if (toAdd.Count == 0)
        {
            _m.Status = resumed > 0 ? "Resuming…" : "Nothing to add.";
            return;
        }
        wanted = toAdd;
        var keep = AppSettings.Load();
        keep.PsIp = _m.PsIp;
        keep.PcIp = _m.PcIp;
        keep.Save();

        try
        {
            EnsureServer();
        }
        catch (Exception ex)
        {
            _m.Status = "File server failed (port 9898 busy — another sender running?): " + Short(ex.Message);
            return;
        }

        bool alreadyRunning;
        lock (_runLock)
        {
            _stop = false;
            foreach (var g in wanted)
            {
                var qi = new QueueItem { Game = g, State = "queued", Message = "waiting…" };
                _m.Queue.Add(qi);
                _runQueue.Enqueue(qi);
            }
            alreadyRunning = _running;
            if (!alreadyRunning)
                _running = true;
        }
        UpdateQueueLabel();
        if (alreadyRunning)
            _m.Status = $"Queued {wanted.Count} more — console takes them in order.";
        else
            _ = RunQueueAsync();
    }

    private void EnsureServer()
    {
        if (_server != null)
            return;
        _server = new RangeFileServer(_registry);
        _server.Start();
    }

    // Pushed items awaiting their download, with server url-id, idle ticks
    // and push time. Guarded by _runLock. (Like DPI: push everything fast,
    // the console queues installs itself and pulls one file at a time.)
    private readonly Dictionary<QueueItem, string> _activeIds = new();
    private readonly Dictionary<QueueItem, int> _activeIdle = new();
    private readonly Dictionary<QueueItem, DateTime> _activeSince = new();

    private async Task RunQueueAsync()
    {
        Post(() => _m.IsSending = true);
        for (;;)
        {
            for (;;)
            {
                List<QueueItem> toPush;
                bool hasActive;
                lock (_runLock)
                {
                    if (_stop)
                        break;
                    toPush = _runQueue.ToList();
                    _runQueue.Clear();
                    hasActive = _activeIds.Count > 0;
                    if (toPush.Count == 0 && !hasActive)
                        break;
                }
                foreach (var qi in toPush)
                {
                    if (_stop)
                        break;
                    await PushOneAsync(qi);
                }
                MonitorTick();
                await Task.Delay(1000);
            }
            // Exited inner loop: either drained or stopped. Late arrivals
            // (enqueued just now) must not get stranded: re-check atomically.
            bool done;
            List<QueueItem> leftovers = new();
            List<string> revoked = new();
            lock (_runLock)
            {
                if (!_stop && (_runQueue.Count > 0 || _activeIds.Count > 0))
                {
                    done = false;
                }
                else
                {
                    done = true;
                    _running = false;
                    if (_stop)
                    {
                        leftovers.AddRange(_activeIds.Keys);
                        foreach (var kv in _activeIds)
                            revoked.Add(kv.Value);
                        _activeIds.Clear();
                        _activeIdle.Clear();
                        _activeSince.Clear();
                        _runQueue.Clear();
                    }
                }
            }
            if (done)
            {
                // Real stop: pull the files from under the console so its
                // in-flight download errors out instead of finishing quietly.
                // (An already fully-downloaded install on the console side
                // can't be recalled — that one will complete.)
                foreach (var id in revoked)
                    _server?.Revoke(id);
                foreach (var qi in leftovers)
                {
                    var row = qi;
                    Post(() => { row.State = "failed"; row.Message = "stopped"; row.CanResume = true; UpdateQueueLabel(); });
                }
                Post(() => _m.Status = _stop ? "Stopped." : "Queue finished.");
                Post(() => _m.IsSending = false);
                return;
            }
        }
    }

    /// <summary>Register + push a single PKG; download is tracked by MonitorTick.</summary>
    private async Task PushOneAsync(QueueItem item)
    {
        string id;
        lock (_runLock)
        {
            if (!_pathIds.TryGetValue(item.Game.Path, out id!))
            {
                id = System.Threading.Interlocked.Increment(ref _nextId).ToString();
                _pathIds[item.Game.Path] = id;
            }
            // Fresh (re)push of an idle url: un-revoke (undo Stop) and zero
            // its counter so progress starts clean. If another active row
            // shares the url, keep its running counter.
            if (!_activeIds.Values.Contains(id))
                _server!.ResetServed(id);
        }
        _registry[id] = item.Game.Path;
        _server!.Unrevoke(id);
        string url = _server!.UrlFor(_m.PcIp, id);
        // Cover PNG for the console installer UI (LoopDPI shows icon_url).
        string? iconUrl = null;
        if (item.Game.IconData is { Length: > 0 } icon)
        {
            _server!.RegisterIcon(id, icon);
            iconUrl = _server!.IconUrlFor(_m.PcIp, id);
        }
        Post(() =>
        {
            item.State = "sending";
            item.Message = "pushing…";
            _m.Status = $"Installing PKG: {item.Game.Title}";
        });
        var (ok, reply) = await ConsoleClient.PushAsync(_m.PsIp, url, item.Game.Title, iconUrl);
        lock (_runLock)
        {
            if (!ok)
            {
                Post(() =>
                {
                    item.State = "failed";
                    item.Message = reply.Length > 60 ? reply[..60] : reply;
                    UpdateQueueLabel();
                });
                return;
            }
            _activeIds[item] = id;
            _activeIdle[item] = 0;
            _activeSince[item] = DateTime.UtcNow;
        }
        Post(() =>
        {
            item.Message = "queued on console…";
            UpdateQueueLabel();
        });
    }

    /// <summary>
    /// Refresh per-file download progress from the server counters. Items the
    /// console fully pulled are marked sent; items it never starts (30 min)
    /// fail honestly instead of hanging the queue.
    /// </summary>
    private void MonitorTick()
    {
        List<(QueueItem Item, string Id, long Delta, long Size)> snap;
        lock (_runLock)
        {
            snap = _activeIds.Select(kv => (kv.Key, kv.Value, 0L, 0L)).ToList();
        }
        var done = new List<QueueItem>();
        var giveUp = new List<QueueItem>();
        foreach (var (item, id, _, _) in snap)
        {
            long size = item.Game.SizeBytes;
            long delta = _server!.ServedFor(id);
            lock (_runLock)
            {
                if (!_activeIds.ContainsKey(item))
                    continue;
                if (delta >= size)
                {
                    if (++_activeIdle[item] >= 5)
                        done.Add(item);
                }
                else
                {
                    _activeIdle[item] = 0;
                    if (delta == 0 && (DateTime.UtcNow - _activeSince[item]).TotalMinutes >= 30)
                        giveUp.Add(item);
                }
            }
            if (!done.Contains(item) && !giveUp.Contains(item))
            {
                var row = item;
                Post(() =>
                {
                    row.Percent = size <= 0 ? 100 : Math.Min(100, delta * 100.0 / size);
                    row.Message = delta == 0
                        ? "queued on console…"
                        : $"{Program.FormatSize(delta)} / {Program.FormatSize(size)}";
                });
            }
        }
        if (done.Count == 0 && giveUp.Count == 0)
            return;
        lock (_runLock)
        {
            foreach (var item in done.Concat(giveUp))
            {
                _activeIds.Remove(item);
                _activeIdle.Remove(item);
                _activeSince.Remove(item);
            }
        }
        foreach (var item in done)
        {
            var row = item;
            Post(() =>
            {
                row.State = "sent";
                row.Percent = 100;
                row.Message = "sent to console queue";
                UpdateQueueLabel();
            });
        }
        foreach (var item in giveUp)
        {
            var row = item;
            Post(() =>
            {
                row.State = "failed";
                row.Message = "console never pulled it";
                row.CanResume = true;
                UpdateQueueLabel();
            });
        }
    }

    private async Task SendFilesAsync(List<QueueItem> files)
    {
        var settings = AppSettings.Load();
        // Finite timeout so a stalled request surfaces as retry/fail
        // instead of hanging the queue at 0% forever.
        using var client = new ReceiverClient(_m.PsIp, timeoutSeconds: 60);
        using var mgr = new TransferManager(client, settings.ChunkSize);
        string remoteBase = _m.RemoteDir.TrimEnd('/');
        foreach (var q in files)
        {
            string leaf = TitleIdOf(q.Game);
            if (string.IsNullOrWhiteSpace(leaf))
                leaf = Path.GetFileName(q.Game.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string remote = remoteBase + "/" + leaf;
            if (q.Game.IsFolder)
                mgr.EnqueueFolder(q.Game.Path, remote);
            else
                mgr.EnqueueFile(q.Game.Path, remote);
            var qi = q;
            Post(() =>
            {
                qi.State = "queued";
                qi.Message = "waiting…";
            });
        }

        var localOf = new Dictionary<TransferItem, QueueItem>();
        QueueItem? FindRow(TransferItem ti)
        {
            foreach (var cand in files)
            {
                if (cand.Game.IsFolder && ti.LocalPath.StartsWith(cand.Game.Path, StringComparison.OrdinalIgnoreCase))
                    return cand;
                if (!cand.Game.IsFolder && string.Equals(ti.LocalPath, cand.Game.Path, StringComparison.OrdinalIgnoreCase))
                    return cand;
            }
            return null;
        }
        mgr.ItemChanged += ti =>
        {
            Post(() =>
            {
                if (!localOf.TryGetValue(ti, out var qi))
                {
                    qi = FindRow(ti);
                    if (qi == null)
                        return;
                    localOf[ti] = qi;
                }
                qi.State = ti.State switch
                {
                    TransferState.Done => "sent",
                    TransferState.Skipped => "sent",
                    TransferState.Failed => "failed",
                    TransferState.Cancelled => "failed",
                    _ => "sending",
                };
                qi.Message = ti.State switch
                {
                    TransferState.Done => "done",
                    TransferState.Skipped => "already there",
                    TransferState.Active => $"{ti.Percent:F0}% ({Program.FormatSize(ti.Sent)} / {Program.FormatSize(ti.Size)})",
                    _ => ti.Message,
                };
                if (ti.State == TransferState.Active)
                    qi.Percent = ti.Percent;
                else if (ti.State is TransferState.Done or TransferState.Skipped)
                    qi.Percent = 100;
                _m.TotalProgress = mgr.Items.Count == 0 ? 100 :
                    mgr.Items.Average(i => i.State is TransferState.Done or TransferState.Skipped ? 100 : i.Percent);
                UpdateQueueLabel();
            });
        };
        mgr.ProgressChanged += p =>
        {
            Post(() => _m.Status = $"Sending {p.FilesDone}/{p.FilesTotal}: {Path.GetFileName(p.CurrentFile)} " +
                $"({Program.FormatSize(p.DoneBytes)} / {Program.FormatSize(p.TotalBytes)})");
        };

        Post(() => _m.Status = $"Sending {files.Count} item(s) to {_m.RemoteDir}…");
        await mgr.RunAsync();
    }

    private static string TitleIdOf(GameItem g)
    {
        if (!string.IsNullOrWhiteSpace(g.ContentId))
        {
            var m = System.Text.RegularExpressions.Regex.Match(g.ContentId.ToUpperInvariant(), @"(PPSA|PPCS|CUSA)\d{5}");
            if (m.Success)
                return m.Value;
        }
        return "";
    }

    private static string Short(string s) => s.Length > 60 ? s[..60] : s;

    /// <summary>GitHub release check, mirroring pkg-viewer's check_updates.</summary>
    private async Task CheckUpdatesAsync(bool manual)
    {
        var info = await LoopDPI.Core.UpdateService.FetchLatestAsync();
        if (info == null)
        {
            if (manual) _m.Status = "Update check failed (or offline).";
            return;
        }
        bool newer;
        try { newer = LoopDPI.Core.UpdateService.IsNewer(info.Tag, LoopDPI.Core.UpdateService.AppVersion); }
        catch { newer = false; }
        if (newer)
        {
            Post(() =>
            {
                _m.Status = $"Update available: {info.Tag}";
                this.FindControl<Button>("BtnUpdate").Content = "⬆ Update available";
            });
            if (manual)
            {
                var owner = Top as Window;
                if (owner != null)
                    await new UpdateDialog(info).ShowDialog(owner);
            }
        }
        else if (manual)
        {
            _m.Status = $"Up to date ({LoopDPI.Core.UpdateService.AppVersion}).";
        }
    }
}
