using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
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
    // Session-unique url ids: the console caches icons by URL, and plain
    // counters restart at 0 every launch — so last week's /icon/3 (game A)
    // would be served from the console cache for this week's /icon/3
    // (game B). Prefixing every id with a per-launch tag makes each push
    // a fresh URL the console has never seen. Within one session the id
    // per path stays stable (needed for Resume).
    private readonly string _sessionTag = Guid.NewGuid().ToString("N")[..8];
    // Stable url-id per local file within one session (session-tagged, see
    // _sessionTag): re-pushing the same file reuses its URL, so the console
    // RESUMES instead of starting over.
    private readonly Dictionary<string, string> _pathIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueueItem> _runQueue = new();
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
            if (e.PropertyName == nameof(LibraryViewModel.Search) ||
                e.PropertyName == nameof(LibraryViewModel.HideExtras))
                ApplyFilter();
        };
        this.FindControl<Button>("BtnAddFolder").Click += async (_, _) => await AddFolderAsync("Add a folder to scan for games");
        this.FindControl<Button>("BtnClearSearch").Click += (_, _) => _m.Search = "";
        this.FindControl<Button>("BtnClearFilter").Click += (_, _) => _m.Search = "";
        this.FindControl<CheckBox>("CompactBox").Checked += (_, _) => SetCompact(true);
        this.FindControl<CheckBox>("CompactBox").Unchecked += (_, _) => SetCompact(false);
        this.FindControl<Button>("BtnExpand").Click += (_, _) =>
        {
            var cb = this.FindControl<CheckBox>("CompactBox");
            if (cb != null) cb.IsChecked = false;
            SetCompact(false); // direct, in case the uncheck event misfires
        };
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
        if (!s.AboutShown || PkgSender.Program.ForceAbout)
        {
            // First launch (or installer --first-install): show About
            // (support links), then start detection.
            Dispatcher.UIThread.Post(async () =>
            {
                var owner = Top as Window;
                if (owner != null)
                    await new AboutWindow().ShowDialog(owner);
                var st = AppSettings.Load();
                st.AboutShown = true;
                st.Save();
                _ = AutoDetectAsync();
            });
        }
        else
        {
            _ = AutoDetectAsync();
        }
        this.FindControl<Button>("BtnAddDrive").Click += async (_, _) => await AddDriveAsync();
        this.FindControl<Button>("BtnTest").Click += async (_, _) => await TestConnectionAsync();
        this.FindControl<Button>("BtnScan").Click += async (_, _) => await ScanFoldersAsync();
        this.FindControl<Button>("BtnSend").Click += (_, _) => EnqueuePkgs();
        this.FindControl<Button>("BtnCopy").Click += async (_, _) => await CopyImagesAsync();
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
        this.FindControl<CheckBox>("PublishCheck").Checked += (_, _) => SetPublish(true);
        this.FindControl<CheckBox>("PublishCheck").Unchecked += (_, _) => SetPublish(false);
        this.FindControl<Button>("BtnClearDone").Click += (_, _) =>
        {
            // Finished rows go; stuck rows (sending/queued with no live
            // transfer behind them) go too. Active downloads are untouched.
            lock (_runLock)
            {
                for (int i = _m.Queue.Count - 1; i >= 0; i--)
                {
                    var qi = _m.Queue[i];
                    if (qi.State == "sent" || qi.State == "failed")
                    {
                        _m.Queue.RemoveAt(i);
                    }
                    else if (!_activeIds.ContainsKey(qi) && !_runQueue.Contains(qi))
                    {
                        _m.Queue.RemoveAt(i); // orphaned row, no worker owns it
                    }
                }
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
            this.FindControl<Button>("BtnSend").IsEnabled = (box.SelectedItems?.Count ?? 0) > 0;
            this.FindControl<Button>("BtnCopy").IsEnabled =
                box.SelectedItems?.Cast<GameItem>().Any(g => g.Role == "Image") == true;
            UpdateGamesLabel();
        };
        // Dense grid: columns follow the panel width so cards always fill
        // the row — no dead strip between the last card and the scrollbar.
        var gamesList = this.FindControl<ListBox>("GamesList");
        gamesList.LayoutUpdated += (_, _) =>
        {
            if (gamesList.ItemsPanelRoot is UniformGrid grid)
            {
                int cols = Math.Max(1, (int)(gamesList.Bounds.Width / 180));
                if (grid.Columns != cols)
                    grid.Columns = cols;
            }
        };
        // Double-click a card: PKGs queue for install, images copy to homebrew.
        this.FindControl<ListBox>("GamesList").DoubleTapped += async (_, e) =>
        {
            if ((e.Source as Control)?.DataContext is GameItem g)
            {
                if (g.Role == "Image")
                    await CopyImagesAsync(new[] { g });
                else
                    EnqueueGames(new[] { g });
            }
        };
        // 🔗 chip inside cards: filter the library to that family.
        this.FindControl<ListBox>("GamesList").AddHandler(Button.ClickEvent, OnCardLinkClick);
        // Per-row Resume buttons live inside the queue DataTemplate.
        this.FindControl<ListBox>("QueueList").AddHandler(Button.ClickEvent, OnQueueButtonClick);
        // No auto-scan at startup: the user presses Scan when ready.
        _m.Status = _roots.Count == 0 ? "Add a folder or drives first." : "Press Scan to load the library.";
    }

    private static string AddrOf(string? item)
    {
        if (string.IsNullOrWhiteSpace(item)) return "";
        int sp = item.IndexOf(' ');
        return (sp < 0 ? item : item[..sp]).Trim();
    }

    /// <summary>
    /// Compact view (like pkg-viewer): the whole app shrinks to a small
    /// window showing only connection status + ETA + the send queue.
    /// Header, toolbar, status bar and the games library are hidden.
    /// </summary>
    private double _savedW = 1200, _savedH = 700;
    private WindowState _savedState = WindowState.Normal;
    private bool _compact;

    private void SetCompact(bool on)
    {
        // Guard: ignore redundant calls (e.g. double events) so the saved
        // size is never overwritten by the compact size itself.
        if (_compact == on)
            return;
        _compact = on;
        var compact = this.FindControl<Border>("CompactBar");
        var header = this.FindControl<Border>("HeaderBar");
        var toolbar = this.FindControl<Border>("ToolbarBar");
        var status = this.FindControl<Border>("StatusBar");
        var games = this.FindControl<Border>("GamesPanel");
        var queue = this.FindControl<Border>("QueuePanel");
        if (compact == null || games == null || queue == null)
            return;
        compact.IsVisible = on;
        if (header != null) header.IsVisible = !on;
        if (toolbar != null) toolbar.IsVisible = !on;
        if (status != null) status.IsVisible = !on;
        games.IsVisible = !on;
        Grid.SetColumn(queue, on ? 0 : 1);
        Grid.SetColumnSpan(queue, on ? 2 : 1);
        queue.Margin = on ? new Thickness(0) : new Thickness(12, 0, 0, 0);
        if (TopLevel.GetTopLevel(this) is Window w)
        {
            if (on)
            {
                _savedW = w.Width;
                _savedH = w.Height;
                _savedState = w.WindowState;
                w.WindowState = WindowState.Normal;
                w.MinWidth = 360;
                w.MinHeight = 240;
                w.Width = 470;
                w.Height = 520;
            }
            else
            {
                w.MinWidth = 1140;
                w.MinHeight = 560;
                double tw = Math.Max(_savedW, 1140);
                double th = Math.Max(_savedH, 560);
                var ws = _savedState;
                // Restore after the expand layout settles: resizing in the
                // same tick as the visibility changes sometimes gets
                // swallowed, leaving the window stuck small.
                Dispatcher.UIThread.Post(() =>
                {
                    w.MinWidth = 1140;
                    w.MinHeight = 560;
                    w.WindowState = ws;
                    if (ws == WindowState.Normal)
                    {
                        w.Width = tw;
                        w.Height = th;
                    }
                });
            }
        }
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

    private void OnCardLinkClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (e.Source is Button { Name: "LinkBtn", DataContext: GameItem g } && !string.IsNullOrEmpty(g.FamilyKey))
        {
            // Toggle: click again to go back to the full library.
            _m.Search = _m.Search == g.FamilyKey ? "" : g.FamilyKey;
            e.Handled = true;
        }
    }

    private void OnQueueButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)    {
        if (e.Source is Button { DataContext: QueueItem qi } btn)
        {
                switch (btn.Name)
                {
                    case "BtnUp": MoveRow(qi, -1); break;
                    case "BtnDown": MoveRow(qi, +1); break;
                    case "BtnRemove": RemoveRow(qi); break;
                    case "BtnPause": TogglePause(qi); break;
                    default: ResumeRow(qi); break;
                }
        }
    }

    /// <summary>
    /// Reorder a still-queued (not yet pushed) row. Active/sent rows are
    /// locked: the console already owns their order. Called on UI thread.
    /// </summary>
    private void MoveRow(QueueItem row, int dir)
    {
        lock (_runLock)
        {
            if (!_runQueue.Contains(row))
            {
                Post(() => _m.Status = "Only queued items can be reordered (this one is already on the console).");
                return;
            }
            int i = _m.Queue.IndexOf(row);
            int j = i + dir;
            while (j >= 0 && j < _m.Queue.Count && !_runQueue.Contains(_m.Queue[j]))
                j += dir;
            if (j < 0 || j >= _m.Queue.Count)
                return;
            _m.Queue.Move(i, j);
            // Mirror the order into the pending list.
            _runQueue.Remove(row);
            int pos = 0;
            foreach (var q in _m.Queue)
            {
                if (q == row)
                    break;
                if (_runQueue.Contains(q))
                    pos++;
            }
            _runQueue.Insert(Math.Min(pos, _runQueue.Count), row);
        }
        UpdateQueueLabel();
    }

    /// <summary>
    /// Remove one row: queued -> just drops out; active download -> its URL
    /// is revoked (console errors out) and the row stays as resumable;
    /// sent/failed -> row removed.
    /// </summary>
    private void RemoveRow(QueueItem row)
    {
        string? revokeId = null;
        bool wasPending;
        lock (_runLock)
        {
            wasPending = _runQueue.Remove(row);
            if (wasPending)
            {
                _speedSamples.Remove(row);
            }
            else if (_activeIds.TryGetValue(row, out var id))
            {
                revokeId = id;
                _activeIds.Remove(row);
                _activeIdle.Remove(row);
                _activeSince.Remove(row);
                _speedSamples.Remove(row);
            }
        }
        if (wasPending)
        {
            Post(() =>
            {
                _m.Queue.Remove(row);
                UpdateQueueLabel();
                _m.Status = "Removed from queue.";
            });
            return;
        }
        if (revokeId != null)
            _server?.Revoke(revokeId);
        Post(() =>
        {
            if (revokeId != null)
            {
                row.State = "failed";
                row.Message = "cancelled";
                row.CanResume = true;
                _m.Status = "Cancelled — resume it from its row to re-queue.";
            }
            else
            {
                _m.Queue.Remove(row);
            }
            UpdateQueueLabel();
        });
    }

    /// <summary>
    /// Per-row pause/start. Pausing a still-queued row just parks it (the
    /// worker skips it); pausing an active download revokes its URL like a
    /// cancel, so the row stays resumable. Starting re-queues or resumes it
    /// and wakes the worker if it went idle.
    /// Called on UI thread.
    /// </summary>
    private void TogglePause(QueueItem row)
    {
        bool startWorker = false;
        lock (_runLock)
        {
            if (!row.IsPaused)
            {
                row.IsPaused = true;
                if (_activeIds.TryGetValue(row, out var id))
                {
                    _server?.Revoke(id);
                    _activeIds.Remove(row);
                    _activeIdle.Remove(row);
                    _activeSince.Remove(row);
                    _speedSamples.Remove(row);
                    row.CanResume = true;
                }
                Post(() => row.Message = "paused");
            }
            else
            {
                row.IsPaused = false;
                if (_runQueue.Contains(row))
                {
                    Post(() => row.Message = "waiting…");
                }
                else if (row.CanResume && row.Game != null &&
                         _pathIds.ContainsKey(row.Game.Path))
                {
                    // Was mid-transfer when paused: resume it (same URL).
                    ResumeRow(row); // re-entrant lock, restarts worker if needed
                }
                else
                {
                    Post(() => row.Message = "waiting…");
                }
                if (!_running)
                {
                    _running = true;
                    startWorker = true;
                }
            }
        }
        if (startWorker)
            _ = RunQueueAsync();
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
            row.IsPaused = false;
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
        // Drives only — folder results already on screen stay untouched.
        await ScanAsync(picked, merge: true);
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
        var added = new List<string>();
        foreach (var f in folders)
        {
            string p = f.Path.LocalPath;
            if (Directory.Exists(p) && !_roots.Contains(p, StringComparer.OrdinalIgnoreCase))
            {
                _roots.Add(p);
                added.Add(p);
            }
        }
        if (added.Count > 0)
        {
            LibraryScanner.SaveRoots(_roots);
            // New folders only — drive results already on screen stay untouched.
            await ScanAsync(added, merge: true);
        }
    }

    /// <summary>
    /// The Scan button: added folders only, never drives. Replaces the list.
    /// </summary>
    private async Task ScanFoldersAsync()
    {
        var folders = _roots.Where(r => !IsDriveRoot(r)).ToList();
        if (folders.Count == 0)
        {
            _m.Status = _roots.Count == 0
                ? "Add a folder or drives first."
                : "No folders added — Scan covers + Add folder paths only (drives: Scan drives…).";
            return;
        }
        await ScanAsync(folders, merge: false);
    }

    /// <summary>
    /// Scan an explicit root set. merge=false replaces the list (Scan
    /// button); merge=true upserts by path (drives/folders added later).
    /// PKG files only — images/folders stay out of the list.
    /// </summary>
    private async Task ScanAsync(List<string> roots, bool merge)
    {
        if (_m.IsBusy)
            return;
        _m.IsBusy = true;
        _m.Status = roots.Count == 0 ? "Add a folder or drives first." : "Scanning…";
        try
        {
            var prog = new Progress<string>(s => Post(() => _m.Status = s));
            var found = await LibraryScanner.ScanAsync(roots, prog);
            Post(() =>
            {
                if (!merge)
                    _all.Clear();
                else if (found.Count > 0)
                {
                    var paths = new HashSet<string>(found.Select(g => g.Path), StringComparer.OrdinalIgnoreCase);
                    for (int i = _all.Count - 1; i >= 0; i--)
                        if (paths.Contains(_all[i].Path))
                            _all.RemoveAt(i);
                }
                foreach (var g in found)
                {
                    // Folders stay out (not single files); images ride along
                    // for the console Images catalog, PKGs for Games.
                    if (g.Info.IsFolder)
                        continue;
                    string gid = !string.IsNullOrWhiteSpace(g.Info.TitleId)
                        ? g.Info.TitleId : g.Info.ContentId;
                    string gver = string.IsNullOrWhiteSpace(g.Info.Version)
                        ? "" : " • v" + g.Info.Version.TrimStart('v', 'V');
                    string fmt = string.IsNullOrEmpty(g.Info.Format) ? "pkg" : g.Info.Format;
                    string role = fmt != "pkg" ? "Image"
                        : g.Info.IsDlc ? "DLC"
                        : g.Info.ContentType.Equals("gp", StringComparison.OrdinalIgnoreCase) ? "Patch"
                        : "Game";
                    string meta = gid + gver + (role == "Game" ? "" : " • " + role);
                    _all.Add(new GameItem
                    {
                        Path = g.Path,
                        Title = string.IsNullOrWhiteSpace(g.Info.Title) ? Path.GetFileName(g.Path) : g.Info.Title,
                        Meta = meta,
                        SizeText = Program.FormatSize(g.Info.PackageSize),
                        SizeBytes = g.Info.PackageSize,
                        Platform = g.Info.Format == "pkg"
                            ? (string.IsNullOrWhiteSpace(g.Info.Platform) ? "PKG" : g.Info.Platform)
                            : $"{(string.IsNullOrWhiteSpace(g.Info.Platform) ? "PS5" : g.Info.Platform)} • {g.Info.Format}",
                        Format = fmt,
                        IsFolder = g.Info.IsFolder,
                        ContentId = g.Info.ContentId,
                        TitleId = g.Info.TitleId ?? "",
                        Version = (g.Info.Version ?? "").TrimStart('v', 'V'),
                        Cover = g.Info.Cover,
                        HasCover = g.Info.Cover != null,
                        IconData = g.Info.IconData,
                        IsPs5 = (g.Info.Platform ?? "").StartsWith("PS5"),
                        IsPs4 = (g.Info.Platform ?? "").StartsWith("PS4"),
                        IsDlc = g.Info.IsDlc,
                        FamilyKey = FamilyKeyOf(g.Info, g.Path),
                        Role = role,
                        CardRadius = new CornerRadius(2),
                        ImageRadius = new CornerRadius((g.Info.Platform ?? "").StartsWith("PS5") ? 16 : 2),
                    });
                }
                LinkFamilies();
                WriteIconDiag();
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

    /// <summary>
    /// Family key: exact TitleId (region codes stay separate families).
    /// Updates share the base TitleId; DLC content ids contain it too.
    /// No TitleId → lone file family (never merged by title text).
    /// </summary>
    private static string FamilyKeyOf(LoopDPI.Core.PkgInfo info, string path)
    {
        string tid = (info.TitleId ?? "").Trim().ToUpperInvariant();
        if (tid.Length >= 4)
            return tid;
        return "FILE:" + Path.GetFileName(path).ToUpperInvariant();
    }

    private static int RoleRank(string role) => role switch
    {
        "Game" => 0,
        "Patch" => 1,
        "DLC" => 2,
        _ => 3, // Image and anything else sorts last, stays visible
    };

    /// <summary>
    /// Second pass over the library: family sizes + tooltip text, so
    /// updates/DLCs are visibly attached to their base game.
    /// </summary>
    private void LinkFamilies()
    {
        var groups = _all.GroupBy(g => g.FamilyKey).ToList();
        foreach (var grp in groups)
        {
            var members = grp.OrderBy(g => RoleRank(g.Role))
                .ThenByDescending(g => g.SizeBytes)
                .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
            bool linked = !grp.Key.StartsWith("FILE:", StringComparison.Ordinal) && members.Count > 1;
            string tip = linked
                ? "Linked (click 🔗 to show only this family):\n" + string.Join("\n", members.Select(m =>
                    $"• {m.Title} ({m.Role}, {m.SizeText})"))
                : "";
            foreach (var m in members)
            {
                m.FamilyCount = linked ? members.Count : 0;
                m.HasFamily = linked;
                m.FamilyTip = tip;
            }
        }
    }

    /// <summary>
    /// Icon diagnostic: one line per file (title, role, icon bytes + short
    /// hash) so a wrong cover in family view can be traced to its source.
    /// Written to %AppData%\PkgSender\icon-diag.log on every scan.
    /// </summary>
    private void WriteIconDiag()
    {
        try
        {
            var lines = new List<string> { $"scan {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({_all.Count} files)" };
            foreach (var g in _all.OrderBy(g => g.FamilyKey).ThenBy(g => g.Role))
            {
                int len = g.IconData?.Length ?? 0;
                string hash = "";
                if (len > 0)
                {
                    using var sha = System.Security.Cryptography.SHA1.Create();
                    var h = sha.ComputeHash(g.IconData!);
                    hash = " sha1:" + Convert.ToHexString(h)[..12];
                }
                lines.Add($"{g.FamilyKey} | {g.Role,-5} | icon={len}{hash} | {g.Title} | {g.Path}");
            }
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PkgSender");
            Directory.CreateDirectory(dir);
            File.WriteAllLines(Path.Combine(dir, "icon-diag.log"), lines);
        }
        catch
        {
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
                g.FamilyKey.ToLowerInvariant().Contains(q) ||
                Path.GetFileName(g.Path).ToLowerInvariant().Contains(q))
                list.Add(g);
        }
        // Hide DLC/updates mode: collapse each family to its base Game(s).
        // Clicking 🔗 sets Search = FamilyKey -> exact family view, which
        // bypasses the collapse so updates/DLCs become visible.
        if (_m.HideExtras)
        {
            bool isFamilyView = q.Length > 0 &&
                _all.Any(a => string.Equals(a.FamilyKey.ToLowerInvariant(), q, StringComparison.Ordinal));
            if (!isFamilyView)
            {
                var collapsed = new List<GameItem>();
                foreach (var grp in list.GroupBy(g => g.FamilyKey))
                {
                    if (grp.Key.StartsWith("FILE:", StringComparison.Ordinal))
                    {
                        collapsed.AddRange(grp);
                        continue;
                    }
                    var bases = grp.Where(g => g.Role == "Game").ToList();
                    var images = grp.Where(g => g.Role == "Image").ToList();
                    if (bases.Count > 0)
                    {
                        collapsed.AddRange(bases);
                        collapsed.AddRange(images); // images stay visible next to their game
                    }
                    else
                        collapsed.AddRange(grp); // DLC-only family: nothing to collapse to
                }
                list = collapsed;
            }
        }
        IEnumerable<GameItem> MemberOrder(IEnumerable<GameItem> ms) => ms
            .OrderBy(g => RoleRank(g.Role))
            .ThenByDescending(g => g.SizeBytes)
            .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase);
        // Families stay contiguous in every mode; the family is positioned
        // by its base item (first Game-rank member, else biggest member).
        GameItem Rep(IGrouping<string, GameItem> grp)
        {
            var ordered = MemberOrder(grp).ToList();
            return ordered.FirstOrDefault(g => g.Role == "Game") ?? ordered[0];
        }
        var families = list.GroupBy(g => g.FamilyKey).ToList();
        IEnumerable<IGrouping<string, GameItem>> orderedFams = _m.SortMode switch
        {
            "Size ↓" => families.OrderByDescending(f => Rep(f).SizeBytes).ThenBy(f => Rep(f).Title, StringComparer.OrdinalIgnoreCase),
            "Size ↑" => families.OrderBy(f => Rep(f).SizeBytes).ThenBy(f => Rep(f).Title, StringComparer.OrdinalIgnoreCase),
            _ => families.OrderBy(f => Rep(f).Title, StringComparer.OrdinalIgnoreCase),
        };
        _m.Games.Clear();
        foreach (var fam in orderedFams)
            foreach (var g in MemberOrder(fam))
                _m.Games.Add(g);
        bool filtering = q.Length > 0;
        _m.IsFiltering = filtering;
        if (filtering)
        {
            string raw = (_m.Search ?? "").Trim();
            _m.FilterLabel = _m.Games.Count == 0
                ? $"🔍 No matches for \"{raw}\""
                : $"🔍 Filtered by \"{raw}\" — {_m.Games.Count} of {_all.Count} shown";
        }
        else
        {
            _m.FilterLabel = "";
        }
        UpdateGamesLabel();
    }

    private void UpdateGamesLabel()
    {
        int ps5 = _all.Count(g => g.Platform.StartsWith("PS5"));
        int ps4 = _all.Count(g => g.Platform.StartsWith("PS4"));
        int fams = _all.Select(g => g.FamilyKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var sel = this.FindControl<ListBox>("GamesList").SelectedItems;
        int selCount = sel?.Count ?? 0;
        string scope = (_m.PlatformFilter ?? "All") switch
        {
            "PS5" => $"{_m.Games.Count} PS5 games",
            "PS4" => $"{_m.Games.Count} PS4 games",
            _ => $"{_all.Count} games in {fams} families ({ps5} PS5 • {ps4} PS4)",
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
        EnqueueGames(picked);
    }

    /// <summary>
    /// Shared queue path for Send PKG and double-click: same dedupe,
    /// resume and server rules, no duplicated logic.
    /// </summary>
    private void EnqueueGames(IEnumerable<GameItem> picked)
    {
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
                _runQueue.Add(qi);
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

    /// <summary>
    /// Copy selected image files (.exfat/.ffpkg/.ffpfsc) to /data/homebrew:
    /// the receiver pulls each file from our file server itself (same
    /// mechanism as its console Images tab). Covers show on cards whenever
    /// the image parse yields an icon.
    /// </summary>
    private async Task CopyImagesAsync(IEnumerable<GameItem>? only = null)
    {
        try
        {
            await CopyImagesInnerAsync(only);
        }
        catch (Exception ex)
        {
            _m.Status = "Copy crashed: " + Short(ex.Message);
        }
    }

    private async Task CopyImagesInnerAsync(IEnumerable<GameItem>? only = null)
    {
        var picked = (only ?? GamesBox.SelectedItems?.Cast<GameItem>() ?? Enumerable.Empty<GameItem>())
            .Where(g => g.Role == "Image" && !g.IsFolder)
            .ToList();
        if (picked.Count == 0)
        {
            _m.Status = "No images in the selection — pick .exfat/.ffpkg/.ffpfsc rows (IMG badge).";
            return;
        }
        if (string.IsNullOrWhiteSpace(_m.PsIp))
        {
            _m.Status = "No console address — set it at the top (Test to verify).";
            return;
        }
        if (string.IsNullOrWhiteSpace(_m.PcIp))
        {
            _m.Status = "No PC address selected — pick it at the top first.";
            return;
        }
        try
        {
            EnsureServer();
        }
        catch (Exception ex)
        {
            _m.Status = "File server failed (port 9898 busy — another sender running?): " + Short(ex.Message);
            return;
        }
        int ok = 0;
        foreach (var g in picked)
        {
            string id;
            lock (_runLock)
            {
                if (!_pathIds.TryGetValue(g.Path, out id!))
                {
                    id = "lib-" + _sessionTag + "-" + System.Threading.Interlocked.Increment(ref _nextId).ToString();
                    _pathIds[g.Path] = id;
                }
            }
            _registry[id] = g.Path;
            string url = _server!.UrlFor(_m.PcIp, id);
            string remote = "/data/homebrew/" + Path.GetFileName(g.Path);
            _m.Status = $"Copying {g.Title} to homebrew…";
            // Local preflight: can our own server serve this id at all?
            string localCheck;
            try
            {
                using var hc = new System.Net.Http.HttpClient(
                    new System.Net.Http.HttpClientHandler { UseProxy = false })
                    { Timeout = TimeSpan.FromSeconds(10) };
                using var hr = await hc.SendAsync(new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Head, url));
                localCheck = ((int)hr.StatusCode).ToString();
            }
            catch (Exception ex)
            {
                localCheck = "LOCAL-FAIL:" + Short(ex.Message);
            }
            var (started, reply) = await LoopDPI.Core.ConsoleClient.PullAsync(_m.PsIp, url, remote);
            PullLog($"{DateTime.Now:HH:mm:ss} {g.Title} local={localCheck} started={started} reply={reply}");
            if (started)
            {
                ok++;
                // Copy rows live in the queue below with their own progress
                // bar (State "copying": no pause/resume — those are install-only).
                var row = new QueueItem { Game = g, State = "copying", Message = "copying…", Percent = 0 };
                _m.Queue.Add(row);
                UpdateQueueLabel();
                // Follow the copy: poll the receiver's pull progress so a
                // 36GB image doesn't look dead while it downloads.
                long lastGot = 0;
                DateTime lastT = DateTime.UtcNow;
                for (int t = 0; t < 7200; t++)
                {
                    await Task.Delay(3000);
                    if (!_m.Queue.Contains(row))
                        break; // user removed the row
                    var (active, name, got, want) = await LoopDPI.Core.ConsoleClient.GetPullAsync(_m.PsIp);
                    if (!active)
                    {
                        row.Percent = 100;
                        row.State = "sent";
                        row.Message = want > 0 ? Program.FormatSize(want) + " copied" : "copied";
                        break;
                    }
                    DateTime now = DateTime.UtcNow;
                    double dt = (now - lastT).TotalSeconds;
                    string spd = dt > 0.5 && got >= lastGot
                        ? " • " + FormatSpeed((got - lastGot) / dt)
                        : "";
                    lastGot = got;
                    lastT = now;
                    row.Percent = want > 0 ? Math.Min(100, got * 100.0 / want) : 0;
                    row.Message = want > 0
                        ? $"{Program.FormatSize(got)} / {Program.FormatSize(want)}{spd}"
                        : $"{Program.FormatSize(got)}{spd}";
                }
                if (row.State == "copying" && _m.Queue.Contains(row))
                {
                    row.State = "failed";
                    row.Message = "stalled — retry Copy images";
                }
                UpdateQueueLabel();
            }
            else
                _m.Status = $"Copy failed for {g.Title}: local={localCheck} console={CopyHint(reply)}";
        }
        if (ok > 0)
            _m.Status = picked.Count == ok
                ? $"Copy started for {ok} image(s) — watch the console notifications."
                : $"Copy started for {ok}/{picked.Count} image(s), {picked.Count - ok} failed.";
    }

    /// <summary>
    /// Append pull diagnostics (PC-side + console reply) for copy debugging.
    /// </summary>
    private static void PullLog(string line)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PkgSender");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "pull-debug.log"), line + Environment.NewLine);
        }
        catch
        {
        }
    }

    private void EnsureServer()
    {
        if (_server != null)
            return;
        _server = new RangeFileServer(_registry);
        _server.Start();
    }

    /// <summary>
    /// Publish library toggle (console browser catalog): the receiver pulls
    /// GET /catalog + /icon/{id} from this PC and installs via its own page.
    /// </summary>
    private void SetPublish(bool on)
    {
        if (on)
        {
            try
            {
                EnsureServer();
            }
            catch (Exception ex)
            {
                Post(() =>
                {
                    this.FindControl<CheckBox>("PublishCheck").IsChecked = false;
                    _m.Status = "File server failed (port 9898 busy — another sender running?): " + ex.Message;
                });
                return;
            }
            _server!.CatalogProvider = BuildCatalog;
            StartPcAnnounce();
            Post(() => _m.Status = $"Library published: {_server!.CatalogUrlFor(_m.PcIp)} — open it from the console browser (pkg remote installer).");
        }
        else
        {
            if (_server != null)
                _server.CatalogProvider = null;
            StopPcAnnounce();
            Post(() => _m.Status = "Library unpublished.");
        }
    }

    private System.Threading.CancellationTokenSource? _publishCts;

    /// <summary>Broadcast our catalog endpoint while published (console auto-find).</summary>
    private void StartPcAnnounce()
    {
        StopPcAnnounce();
        var cts = new System.Threading.CancellationTokenSource();
        _publishCts = cts;
        _ = LoopDPI.Core.NetDiscovery.AnnouncePcAsync(_m.PcIp, _server!.Port, cts.Token);
    }

    private void StopPcAnnounce()
    {
        try { _publishCts?.Cancel(); } catch { }
        _publishCts = null;
    }

    /// <summary>
    /// Snapshot the scanned PKG list into catalog rows. Runs on the file
    /// server thread — defensive copy, never throws.
    /// </summary>
    private IReadOnlyList<LoopDPI.Core.CatalogEntry> BuildCatalog()
    {
        var rows = new List<LoopDPI.Core.CatalogEntry>();
        try
        {
            GameItem[] snap;
            lock (_runLock)
            {
                snap = _all.ToArray();
            }
            foreach (var g in snap)
            {
                if (g.IsFolder)
                    continue;
                string id;
                lock (_runLock)
                {
                    if (!_pathIds.TryGetValue(g.Path, out id!))
                    {
                        id = "lib-" + _sessionTag + "-" + System.Threading.Interlocked.Increment(ref _nextId).ToString();
                        _pathIds[g.Path] = id;
                    }
                }
                _registry[id] = g.Path;
                bool hasIcon = false;
                if (g.IconData is { Length: > 0 } icon)
                {
                    _server?.RegisterIcon(id, icon);
                    hasIcon = true;
                }
                rows.Add(new LoopDPI.Core.CatalogEntry
                {
                    Id = id,
                    Title = g.Title,
                    TitleId = g.TitleId,
                    Version = g.Version,
                    Size = g.SizeBytes,
                    SizeText = g.SizeText,
                    Role = g.Role,
                    FamilyKey = g.FamilyKey,
                    Platform = g.IsPs4 ? "PS4" : "PS5",
                    Format = g.Format,
                    File = System.IO.Path.GetFileName(g.Path),
                    HasIcon = hasIcon,
                });
            }
        }
        catch
        {
        }
        return rows;
    }

    // Pushed items awaiting their download, with server url-id, idle ticks
    // and push time. Guarded by _runLock. (Like DPI: push everything fast,
    // the console queues installs itself and pulls one file at a time.)
    private readonly Dictionary<QueueItem, string> _activeIds = new();
    private readonly Dictionary<QueueItem, int> _activeIdle = new();
    private readonly Dictionary<QueueItem, DateTime> _activeSince = new();
    // ETA tracking: last (time, total served bytes) sample across ticks.
    private DateTime _etaLastTime = DateTime.UtcNow;
    private long _etaLastServed;

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
                    // Paused rows stay queued until started again.
                    toPush = _runQueue.Where(q => !q.IsPaused).ToList();
                    foreach (var q in toPush)
                        _runQueue.Remove(q);
                    hasActive = _activeIds.Count > 0;
                    if (toPush.Count == 0 && !hasActive)
                        break;
                }
                foreach (var qi in toPush)
                {
                    if (_stop)
                        break;
                    await PushOneAsync(qi);
                    // Sequential gate (ticked = PS4 console): next game waits
                    // until this one is downloaded AND installed.
                    if (_m.SequentialMode && !_stop)
                    {
                        bool pushed;
                        lock (_runLock) { pushed = _activeIds.ContainsKey(qi); }
                        if (pushed)
                            await WaitForInstallAsync(qi);
                    }
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
                // A queue of only paused rows is idle: the worker exits,
                // Start restarts it.
                bool pending = _runQueue.Any(q => !q.IsPaused);
                if (!_stop && (pending || _activeIds.Count > 0))
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
                id = _sessionTag + "-" + System.Threading.Interlocked.Increment(ref _nextId).ToString();
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
    /// One-by-one gate: wait until the console fully pulled the file, then
    /// until /api/status reports idle (= install finished). Old receivers
    /// without /api/status fall back to download-done. Honest timeouts so
    /// a dead console never hangs the queue forever.
    /// Stop-exits always fail the row (resumable) — never orphan it, so
    /// Clear done can always remove it.
    /// </summary>
    private void FailStopped(QueueItem item, string msg)
    {
        Post(() =>
        {
            var row = item;
            if (row.State == "sending" || row.State == "queued")
            {
                row.State = "failed";
                row.Message = msg;
                row.CanResume = true;
                UpdateQueueLabel();
            }
        });
        lock (_runLock)
        {
            _activeIds.Remove(item);
            _activeIdle.Remove(item);
            _activeSince.Remove(item);
        }
    }

    private async Task WaitForInstallAsync(QueueItem item)
    {
        string id;
        lock (_runLock)
        {
            if (!_activeIds.TryGetValue(item, out id!))
                return;
        }
        long size = item.Game.SizeBytes;
        DateTime pushedAt = DateTime.UtcNow;
        for (;;)
        {
            if (_stop || item.IsPaused)
            {
                FailStopped(item, _stop ? "stopped" : "paused");
                return;
            }
            long delta = _server!.ServedFor(id);
            var row = item;
            string rsp = delta == 0 ? "" : TrackRowSpeed(item, delta);
            Post(() =>
            {
                if (row.State == "sending")
                {
                    row.Percent = size <= 0 ? 100 : Math.Min(100, delta * 100.0 / size);
                    row.Message = delta == 0
                        ? "queued on console…"
                        : $"{Program.FormatSize(delta)} / {Program.FormatSize(size)}" +
                          (string.IsNullOrEmpty(rsp) ? "" : $" • {rsp}");
                }
            });
            if (delta >= size)
                break;
            if (delta == 0 && (DateTime.UtcNow - pushedAt).TotalMinutes >= 30)
            {
                Post(() =>
                {
                    row.State = "failed";
                    row.Message = "console never pulled it";
                    row.CanResume = true;
                    UpdateQueueLabel();
                });
                lock (_runLock)
                {
                    _activeIds.Remove(item);
                    _activeIdle.Remove(item);
                    _activeSince.Remove(item);
                }
                return;
            }
            await Task.Delay(1000);
        }
        // Downloaded — now wait for the install itself to finish.
        DateTime t0 = DateTime.UtcNow;
        bool wasBusy = false;
        bool confirmed = false;
        bool supported = true;
        for (;;)
        {
            if (_stop || item.IsPaused)
            {
                FailStopped(item, _stop ? "stopped" : "paused");
                return;
            }
            bool busy;
            (supported, busy) = await ConsoleClient.GetStatusAsync(_m.PsIp);
            if (!supported)
                break; // old receiver / DPI: download-done is all we can know
            if (busy)
                wasBusy = true;
            else if (wasBusy)
            {
                confirmed = true;
                break; // was installing, now idle = finished
            }
            else if ((DateTime.UtcNow - t0).TotalMinutes >= 5)
                break; // never reported busy — don't hang the queue
            if ((DateTime.UtcNow - t0).TotalHours >= 3)
                break;
            Post(() =>
            {
                if (item.State == "sending" || item.State == "sent")
                    item.Message = "installing on console…";
            });
            await Task.Delay(3000);
        }
        if (!supported && !_stop)
        {
            // No status endpoint (e.g. PS4 DPI): give the console a moment
            // to start the install before the next push lands.
            Post(() =>
            {
                if (item.State == "sending" || item.State == "sent")
                    item.Message = "sent — install starting on console…";
            });
            for (int i = 0; i < 10 && !_stop; i++)
                await Task.Delay(1000);
        }
        Post(() =>
        {
            var row = item;
            if (row.State == "failed")
                return;
            if (_stop && !confirmed)
            {
                row.State = "failed";
                row.Message = "stopped";
                row.CanResume = true;
            }
            else
            {
                row.State = "sent";
                row.Percent = 100;
                row.Message = confirmed ? "installed ✓ (check console)" : "sent to console queue";
            }
                lock (_runLock)
                {
                    _activeIds.Remove(row);
                    _activeIdle.Remove(row);
                    _activeSince.Remove(row);
                }
                UpdateQueueLabel();
        });
    }

    private static string FormatEta(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "calculating…";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"≈ {(int)ts.TotalHours}h {ts.Minutes}m left";
        if (ts.TotalMinutes >= 1)
            return $"≈ {(int)ts.TotalMinutes}m {ts.Seconds:D2}s left";
        return $"≈ {(int)ts.TotalSeconds}s left";
    }

    private static string FormatSpeed(double bps)
    {
        if (double.IsNaN(bps) || double.IsInfinity(bps) || bps < 0)
            return "";
        if (bps >= 1024 * 1024 * 1024)
            return $"{bps / 1024 / 1024 / 1024:F1} GB/s";
        if (bps >= 1024 * 1024)
            return $"{bps / 1024 / 1024:F1} MB/s";
        if (bps >= 1024)
            return $"{bps / 1024:F0} KB/s";
        return $"{bps:F0} B/s";
    }

    // Per-row speed samples (sliding ~3s window). Guarded by _runLock.
    private readonly Dictionary<QueueItem, Queue<(DateTime T, long Bytes)>> _speedSamples = new();

    /// <summary>Track one row's served bytes; returns "" until ~1s of samples.</summary>
    private string TrackRowSpeed(QueueItem item, long bytes)
    {
        double speed = -1;
        lock (_runLock)
        {
            if (!_speedSamples.TryGetValue(item, out var q))
            {
                q = new Queue<(DateTime, long)>();
                _speedSamples[item] = q;
            }
            var now = DateTime.UtcNow;
            q.Enqueue((now, bytes));
            while (q.Count > 4)
                q.Dequeue();
            var first = q.Peek();
            double dt = (now - first.T).TotalSeconds;
            if (q.Count >= 2 && dt >= 1)
                speed = Math.Max(0, (bytes - first.Bytes) / dt);
        }
        return speed < 0 ? "" : FormatSpeed(speed);
    }

    private void DropRowSpeed(QueueItem item)
    {
        lock (_runLock)
        {
            _speedSamples.Remove(item);
        }
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
        long totalSize = 0, totalServed = 0;
        foreach (var (item, id, _, _) in snap)
        {
            long size = item.Game.SizeBytes;
            long delta = _server!.ServedFor(id);
            totalSize += size;
            totalServed += Math.Min(delta, size);
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
                string rsp = delta == 0 ? "" : TrackRowSpeed(item, delta);
                Post(() =>
                {
                    row.Percent = size <= 0 ? 100 : Math.Min(100, delta * 100.0 / size);
                    row.Message = delta == 0
                        ? "queued on console…"
                        : $"{Program.FormatSize(delta)} / {Program.FormatSize(size)}" +
                          (string.IsNullOrEmpty(rsp) ? "" : $" • {rsp}");
                });
            }
        }
        if (done.Count == 0 && giveUp.Count == 0)
        {
            UpdateEta(snap.Count, totalSize, totalServed);
            return;
        }
        lock (_runLock)
        {
            foreach (var item in done.Concat(giveUp))
            {
                _activeIds.Remove(item);
                _activeIdle.Remove(item);
                _activeSince.Remove(item);
                _speedSamples.Remove(item);
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
        long remSize = 0, remServed = 0;
        int remCount;
        lock (_runLock)
        {
            remCount = _activeIds.Count;
            foreach (var kv in _activeIds)
            {
                remSize += kv.Key.Game.SizeBytes;
                remServed += Math.Min(_server!.ServedFor(kv.Value), kv.Key.Game.SizeBytes);
            }
        }
        UpdateEta(remCount, remSize, remServed);
    }

    /// <summary>
    /// Overall ETA + speed line for the compact bar (speed from the last
    /// samples of total served bytes). Must be called holding _runLock
    /// only for the remaining-count query — the Post itself is lock-free.
    /// </summary>
    private double _lastSpeed;

    private void UpdateEta(int activeCount, long totalSize, long totalServed)
    {
        string eta, speed;
        if (activeCount == 0)
        {
            eta = "";
            speed = "";
            _lastSpeed = 0;
            _etaLastServed = 0;
            _etaLastTime = DateTime.UtcNow;
        }
        else
        {
            var now = DateTime.UtcNow;
            double dt = (now - _etaLastTime).TotalSeconds;
            if (dt >= 1)
            {
                _lastSpeed = Math.Max(0, (totalServed - _etaLastServed) / dt);
                _etaLastTime = now;
                _etaLastServed = totalServed;
            }
            speed = FormatSpeed(_lastSpeed);
            if (totalSize > 0 && totalServed >= totalSize)
                eta = "finishing…";
            else if (_lastSpeed > 0)
                eta = FormatEta((totalSize - totalServed) / _lastSpeed);
            else
                eta = totalServed > 0 ? "stalled…" : "calculating…";
        }
        Post(() => { _m.EtaText = eta; _m.SpeedText = speed; });
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

    /// <summary>
    /// Translate the receiver's pull reply into an actionable hint:
    /// old payload (no endpoint), TESTONLY build, or unreachable PC.
    /// </summary>
    private static string CopyHint(string reply)
    {
        if (reply.Contains("unknown endpoint"))
            return "console runs an old payload — send the newest ELF first.";
        if (reply.Contains("test build"))
            return "console runs the TESTONLY payload — use the normal ELF.";
        if (reply.Contains("bad url/path"))
            return "receiver refused url/path (" + Short(reply) + ").";
        return Short(reply);
    }

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
