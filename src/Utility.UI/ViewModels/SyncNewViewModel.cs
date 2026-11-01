using Utility.Server.DropBoxSync;
using Utility.UI.Mvvm;

namespace Utility.UI.ViewModels;

/// <summary>
/// The two-phase sync's busy screen: the run, the phase it is in, and what it has done.
/// <para>
/// <b>It owns the run.</b> The busy screen covers the whole window, so its state has to
/// survive the page being navigated away from - the same reason the existing sync's
/// overlay binds to the sync service rather than to a page.
/// </para>
/// <para>
/// <b>The listing phase's numbers are the sync service's own.</b> The two-phase service
/// runs phase 1 through <see cref="IDropBoxSyncService"/>, so the width, the traffic
/// and the level table are that service's live properties, read rather than copied.
/// The enrichment's numbers are this view model's own, fed by the progress callback
/// the two-phase service publishes.
/// </para>
/// </summary>
public sealed class SyncNewViewModel : ObservableObject
{
    private readonly IDropBoxSyncTwoPhaseService _twoPhase;
    private readonly IDropBoxSyncService _sync;

    private CancellationTokenSource? _cts;

    private bool _isBusy;
    private bool _showLevels = true;
    private string _phase = "Idle";
    private string _currentPath = string.Empty;
    private int _enrichDone;
    private int _enrichTotal;
    private int _enrichFailed;
    private int _enrichInFlight;
    private int _enrichWidth;
    private string _outcome = string.Empty;
    private string? _error;

    /// <summary>Creates the view model over the two-phase service and the sync service.</summary>
    public SyncNewViewModel(IDropBoxSyncTwoPhaseService twoPhase, IDropBoxSyncService sync)
    {
        _twoPhase = twoPhase;
        _sync = sync;

        // The service raises for its own live state, so the screen re-reads it rather
        // than keeping a copy that would go stale.
        _sync.PropertyChanged += (_, _) => OnSyncStateChanged();

        StopCommand = new RelayCommand(_ => Stop());
    }

    /// <summary>True while a run is in flight. The screen shows while this is true.</summary>
    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    /// <summary>
    /// True when the level table belongs on the screen.
    /// <para>
    /// It belongs to the walk. An enrichment-only run has no walk, so the table would
    /// be showing the PREVIOUS walk's levels - which is worse than showing none.
    /// </para>
    /// </summary>
    public bool ShowLevels
    {
        get => _showLevels;
        private set => SetProperty(ref _showLevels, value);
    }

    /// <summary>Which of the two phases is running, in words.</summary>
    public string Phase
    {
        get => _phase;
        private set => SetProperty(ref _phase, value);
    }

    /// <summary>The folder being listed, or the file being read in the enrichment.</summary>
    public string CurrentPath
    {
        get => _currentPath;
        private set => SetProperty(ref _currentPath, value);
    }

    /// <summary>The walk's levels. The sync service's own list, replaced as it updates.</summary>
    public IReadOnlyList<DropBoxSyncLevel> Levels => _sync.Levels;

    /// <summary>
    /// How many Dropbox requests are in flight right now.
    /// <para>
    /// Whichever phase is running. The enrichment has its OWN gate and does not share
    /// the walk's, so the walk's counters stand still while it works - and an
    /// enrichment-only run has no walk at all.
    /// </para>
    /// </summary>
    public int ActiveDownloaders => IsEnriching ? _enrichInFlight : _sync.ActiveDownloaders;

    /// <summary>
    /// How many Dropbox requests may be in flight at once - the width of whichever
    /// phase is running.
    /// </summary>
    public int MaxConcurrency => IsEnriching ? _enrichWidth : _sync.MaxConcurrency;

    /// <summary>The walk's own traffic, measured: the rate, the refusals and the latency.</summary>
    public string Traffic => _sync.RateSnapshot.Display;

    /// <summary>Files enriched so far in this run's second phase.</summary>
    public int EnrichDone
    {
        get => _enrichDone;
        private set
        {
            if (SetProperty(ref _enrichDone, value))
            {
                OnPropertyChanged(nameof(EnrichFraction));
                OnPropertyChanged(nameof(EnrichDisplay));
            }
        }
    }

    /// <summary>How many files the enrichment's work list held when the phase started.</summary>
    public int EnrichTotal
    {
        get => _enrichTotal;
        private set
        {
            if (SetProperty(ref _enrichTotal, value))
            {
                OnPropertyChanged(nameof(EnrichFraction));
                OnPropertyChanged(nameof(EnrichDisplay));
            }
        }
    }

    /// <summary>Files whose metadata could not be fetched.</summary>
    public int EnrichFailed
    {
        get => _enrichFailed;
        private set => SetProperty(ref _enrichFailed, value);
    }

    /// <summary>True while the second phase is running, which is when the bar is determinate.</summary>
    public bool IsEnriching => string.Equals(Phase, PhaseEnriching, StringComparison.Ordinal);

    /// <summary>The enrichment's progress, 0 to 1. Zero when there is nothing to enrich.</summary>
    public double EnrichFraction => EnrichTotal <= 0
        ? 0
        : Math.Clamp((double)EnrichDone / EnrichTotal, 0, 1);

    /// <summary>The enrichment's progress in words: "882 of 882 enriched, 0 failed".</summary>
    public string EnrichDisplay =>
        $"{EnrichDone:N0} of {EnrichTotal:N0} enriched, {EnrichFailed:N0} failed";

    /// <summary>What the run did, in one line.</summary>
    public string Outcome
    {
        get => _outcome;
        private set => SetProperty(ref _outcome, value);
    }

    /// <summary>The failure, when there was one.</summary>
    public string? Error
    {
        get => _error;
        private set => SetProperty(ref _error, value);
    }

    /// <summary>Stops the run. The rows already written are kept.</summary>
    public RelayCommand StopCommand { get; }

    /// <summary>The phase's name, as the screen shows it.</summary>
    public const string PhaseListing = "Listing the structure";

    /// <summary>The phase's name, as the screen shows it.</summary>
    public const string PhaseEnriching = "Enriching the changed files";

    /// <summary>
    /// Runs the sync: the structure first, then whatever it marked for enrichment.
    /// </summary>
    /// <param name="path">The Dropbox folder to start from.</param>
    public async Task StartAsync(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        Phase = PhaseListing;
        ShowLevels = true;
        CurrentPath = path;
        EnrichDone = 0;
        EnrichTotal = 0;
        EnrichFailed = 0;
        Outcome = string.Empty;
        Error = null;
        OnPropertyChanged(nameof(IsEnriching));

        try
        {
            var listProgress = new Progress<DropBoxSyncProgress>(_ => OnListingProgress());
            var enrichProgress = new Progress<DropBoxTwoPhaseEnrichProgress>(OnEnrichProgress);

            var result = await _twoPhase.SyncFrom(path, null, listProgress, enrichProgress, token);

            if (result.Completed)
            {
                Outcome =
                    $"Listed {result.List.FoldersWritten:N0} folder(s), {result.List.FilesWritten:N0} file(s). " +
                    $"Enriched {result.Enrich.Enriched:N0} of {result.Enrich.Pending:N0} changed file(s), " +
                    $"{result.Enrich.Failed:N0} failed.";

                EnrichDone = result.Enrich.Enriched;
                EnrichTotal = result.Enrich.Pending;
                EnrichFailed = result.Enrich.Failed;
            }
            else
            {
                Error = result.Error ?? "The sync did not complete.";
                Outcome = "The sync did not complete.";
            }
        }
        catch (OperationCanceledException)
        {
            Outcome = "The sync was stopped. The rows already written are kept.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Outcome = "The sync did not complete.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEnriching));

            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// Runs the ENRICHMENT pass on its own: the files a previous sync marked as
    /// changed, and nothing else.
    /// <para>
    /// It needs no path. The work list is the set of rows tagged 'pending', so there is
    /// nothing to scope it to - the path box does not apply to this run. The level
    /// table is hidden, because there is no walk in it to show.
    /// </para>
    /// </summary>
    public async Task EnrichOnlyAsync()
    {
        if (IsBusy)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        IsBusy = true;
        Phase = PhaseEnriching;
        ShowLevels = false;
        CurrentPath = string.Empty;
        EnrichDone = 0;
        EnrichTotal = 0;
        EnrichFailed = 0;
        Outcome = string.Empty;
        Error = null;
        OnPropertyChanged(nameof(IsEnriching));

        try
        {
            var progress = new Progress<DropBoxTwoPhaseEnrichProgress>(OnEnrichProgress);

            var result = await _twoPhase.EnrichAsync(null, null, progress, token);

            if (result.Completed)
            {
                Outcome =
                    $"Enriched {result.Enriched:N0} of {result.Pending:N0} changed file(s), " +
                    $"{result.Failed:N0} failed.";

                EnrichDone = result.Enriched;
                EnrichTotal = result.Pending;
                EnrichFailed = result.Failed;
            }
            else
            {
                Error = result.Error ?? "The enrichment did not complete.";
                Outcome = "The enrichment did not complete.";
            }
        }
        catch (OperationCanceledException)
        {
            Outcome = "The enrichment was stopped.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Outcome = "The enrichment did not complete.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(IsEnriching));

            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// The listing phase's progress. The walk's own live state is read by the bindings;
    /// what this adds is the phase change, which the enrichment's callback cannot make
    /// on its own - an enrichment with nothing to do never reports.
    /// </summary>
    private void OnListingProgress()
    {
        if (Phase != PhaseListing)
        {
            Phase = PhaseListing;
            OnPropertyChanged(nameof(IsEnriching));
        }
    }

    /// <summary>The enrichment's progress: how many files have been read, and which one.</summary>
    private void OnEnrichProgress(DropBoxTwoPhaseEnrichProgress progress)
    {
        if (Phase != PhaseEnriching)
        {
            Phase = PhaseEnriching;
            OnPropertyChanged(nameof(IsEnriching));
        }

        EnrichDone = progress.Done;
        EnrichTotal = progress.Total;
        CurrentPath = progress.CurrentPath;

        // The enrichment's own traffic: it does not share the walk's gate, so the
        // walk's counters would show a stalled screen over a working pass.
        _enrichInFlight = progress.InFlight;
        _enrichWidth = progress.Width;

        OnPropertyChanged(nameof(ActiveDownloaders));
        OnPropertyChanged(nameof(MaxConcurrency));
    }

    /// <summary>
    /// The sync service changed something. The screen's numbers come from it, so they
    /// are re-read.
    /// </summary>
    private void OnSyncStateChanged()
    {
        // The walk stops running when the listing phase ends, which is how the phase
        // change is seen when the enrichment has nothing to do and never reports.
        if (IsBusy && Phase == PhaseListing && !_sync.IsRunning)
        {
            Phase = PhaseEnriching;
            OnPropertyChanged(nameof(IsEnriching));
        }

        if (Phase == PhaseListing)
        {
            CurrentPath = _sync.CurrentPath;
        }

        OnPropertyChanged(nameof(Levels));
        OnPropertyChanged(nameof(ActiveDownloaders));
        OnPropertyChanged(nameof(MaxConcurrency));
        OnPropertyChanged(nameof(Traffic));
    }

    /// <summary>Stops the run.</summary>
    private void Stop()
    {
        // The walk is cancelled through the service, which is the path the existing
        // sync's Stop button uses, and through the token, which covers the enrichment.
        _sync.Cancel();
        _cts?.Cancel();
    }
}
