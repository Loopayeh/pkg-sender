using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using ReactiveUI;

namespace PkgSender.ViewModels;

public sealed class GameItem : ReactiveObject
{
    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public string Meta { get; init; } = "";
    public string SizeText { get; init; } = "";
    public long SizeBytes { get; init; }
    public string Platform { get; init; } = "";
    public string Format { get; init; } = "pkg";
    public bool IsFolder { get; init; }
    public string ContentId { get; init; } = "";
    public string TitleId { get; init; } = "";
    public string Version { get; init; } = "";
    public Bitmap? Cover { get; init; }
    public bool HasCover { get; init; }
    public bool NoCover => !HasCover;
    public byte[]? IconData { get; init; }
    public bool IsPs5 { get; init; }
    public bool IsPs4 { get; init; }
    public bool IsDlc { get; init; }
    // Family linking: updates/DLCs of one title stay visibly together.
    public string FamilyKey { get; init; } = "";
    public string Role { get; init; } = "Game"; // Game | Patch | DLC
    public int FamilyCount { get; set; }
    public bool HasFamily { get; set; }
    public string FamilyTip { get; set; } = "";
    // Shape language: cards always rectangular; only the PS5 cover image is round.
    public CornerRadius CardRadius { get; init; }
    public CornerRadius ImageRadius { get; init; }

    private bool _isSelected;
    public bool IsSelected { get => _isSelected; set => this.RaiseAndSetIfChanged(ref _isSelected, value); }
}

public sealed class QueueItem : ReactiveObject
{
    public GameItem Game { get; init; } = null!;

    private string _state = "queued";
    public string State
    {
        get => _state;
        set { this.RaiseAndSetIfChanged(ref _state, value); this.RaisePropertyChanged(nameof(CanPause)); }
    }

    private double _percent;
    public double Percent { get => _percent; set => this.RaiseAndSetIfChanged(ref _percent, value); }

    private string _message = "";
    public string Message { get => _message; set => this.RaiseAndSetIfChanged(ref _message, value); }

    private bool _canResume;
    public bool CanResume { get => _canResume; set => this.RaiseAndSetIfChanged(ref _canResume, value); }

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        set { this.RaiseAndSetIfChanged(ref _isPaused, value); this.RaisePropertyChanged(nameof(PauseText)); }
    }

    /// <summary>Pause button glyph: ⏸ while running, ▶ while paused.</summary>
    public string PauseText => IsPaused ? "▶" : "⏸";

    /// <summary>Pause only makes sense before the row is done.</summary>
    public bool CanPause => State is "queued" or "sending";
}

public sealed class LibraryViewModel : ReactiveObject
{
    private string _psIp = "";
    public string PsIp { get => _psIp; set => this.RaiseAndSetIfChanged(ref _psIp, value); }

    private string _pcIp = "";
    public string PcIp { get => _pcIp; set => this.RaiseAndSetIfChanged(ref _pcIp, value); }

    private string _remoteDir = "/data/homebrew";
    public string RemoteDir { get => _remoteDir; set => this.RaiseAndSetIfChanged(ref _remoteDir, value); }

    private string _search = "";
    public string Search { get => _search; set => this.RaiseAndSetIfChanged(ref _search, value); }

    private bool _isFiltering;
    public bool IsFiltering { get => _isFiltering; set => this.RaiseAndSetIfChanged(ref _isFiltering, value); }

    private string _filterLabel = "";
    public string FilterLabel { get => _filterLabel; set => this.RaiseAndSetIfChanged(ref _filterLabel, value); }

    private string _platformFilter = "All";
    public string PlatformFilter { get => _platformFilter; set => this.RaiseAndSetIfChanged(ref _platformFilter, value); }

    private string _sortMode = "Name";
    public string SortMode { get => _sortMode; set => this.RaiseAndSetIfChanged(ref _sortMode, value); }

    private bool _hideExtras;
    public bool HideExtras { get => _hideExtras; set => this.RaiseAndSetIfChanged(ref _hideExtras, value); }

    private bool _sequentialMode;
    public bool SequentialMode { get => _sequentialMode; set => this.RaiseAndSetIfChanged(ref _sequentialMode, value); }

    private string _status = "Ready.";
    public string Status { get => _status; set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _testResult = "";
    public string TestResult { get => _testResult; set => this.RaiseAndSetIfChanged(ref _testResult, value); }

    private double _totalProgress;
    public double TotalProgress { get => _totalProgress; set => this.RaiseAndSetIfChanged(ref _totalProgress, value); }

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => this.RaiseAndSetIfChanged(ref _isBusy, value); }

    private bool _isSending;
    public bool IsSending { get => _isSending; set => this.RaiseAndSetIfChanged(ref _isSending, value); }

    private string _gamesLabel = "Library";
    public string GamesLabel { get => _gamesLabel; set => this.RaiseAndSetIfChanged(ref _gamesLabel, value); }

    private string _queueLabel = "Queue is empty";
    public string QueueLabel { get => _queueLabel; set => this.RaiseAndSetIfChanged(ref _queueLabel, value); }

    private string _etaText = "";
    public string EtaText { get => _etaText; set => this.RaiseAndSetIfChanged(ref _etaText, value); }

    private string _speedText = "";
    public string SpeedText { get => _speedText; set => this.RaiseAndSetIfChanged(ref _speedText, value); }

    public ObservableCollection<GameItem> Games { get; } = new();
    public ObservableCollection<QueueItem> Queue { get; } = new();
    public ObservableCollection<string> PcIps { get; } = new();
}
