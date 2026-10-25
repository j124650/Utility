using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Utility.Server.Finder;
using Utility.Server.Finder.Services;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// The Finder tab: the pairs, what state each side is in, and what the Finder would do
/// about it. Read-only - it gathers and displays, and repairs nothing.
/// </summary>
public sealed class FinderViewModel(
    IFinderService finder,
    IFinderScanState scanState,
    IDropBoxPreviewClient preview,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _run;
    private AsyncRelayCommand? _previewCommand;
    private RelayCommand? _cancelPass;

    private CancellationTokenSource? _passCts;
    private bool _hasRunPass;

    private string _passStage = string.Empty;
    private string _passDetail = string.Empty;
    private int _passDone;
    private int _passTotal;
    private bool _passIsIndeterminate = true;

    private FinderRow? _selected;
    private FinderCandidate? _selectedCandidate;
    private BitmapSource? _previewImage;
    private string _previewState = string.Empty;
    private string _summary = string.Empty;
    private string _parentFolderFilter = string.Empty;
    private string _parentFileNameFilter = string.Empty;
    private bool _isVerdictFilterOpen;
    private DispatcherTimer? _filterTimer;
    private RelayCommand? _clearVerdictFilter;

    /// <summary>The filter, as the server owns it (§14.3).</summary>
    private readonly FinderFilter _filter = new();

    /// <summary>Every row the pass produced. The filters narrow this list in memory.</summary>
    public ObservableCollection<FinderRow> AllRows { get; } = [];

    /// <summary>The rows the filters leave. This is what the table binds to.</summary>
    public ObservableCollection<FinderRow> Rows { get; } = [];

    /// <summary>The candidates of the selected row.</summary>
    public ObservableCollection<FinderCandidate> Candidates { get; } = [];

    /// <summary>
    /// The rules the verdict rests on (§18.6) - one line each, so the reader sees why the
    /// verdict came out the way it did.
    /// </summary>
    public ObservableCollection<FinderRule> Rules { get; } = [];

    /// <summary>The verdicts the table can be filtered by.</summary>
    public IReadOnlyList<FinderVerdict> VerdictFilterOptions { get; } = FinderVerdict.All;

    /// <summary>
    /// The verdict filter's choices - one per verdict (§7).
    /// </summary>
    /// <remarks>
    /// Choosing a verdict is a single click, so it applies at once: there is no typing to
    /// wait for.
    /// </remarks>
    public IReadOnlyList<VerdictFilterOption> VerdictFilterChoices
    {
        get
        {
            if (_verdictFilterChoices is null)
            {
                _verdictFilterChoices = FinderVerdict.All
                    .Select(v => new VerdictFilterOption(v))
                    .ToList();

                foreach (var choice in _verdictFilterChoices)
                {
                    choice.PropertyChanged += OnVerdictFilterOptionChanged;
                }
            }

            return _verdictFilterChoices;
        }
    }

    private IReadOnlyList<VerdictFilterOption>? _verdictFilterChoices;

    /// <summary>
    /// Unchecks every verdict, which keeps them all (§14.3).
    /// </summary>
    public RelayCommand ClearVerdictFilterCommand => _clearVerdictFilter ??= new(_ => ClearVerdictFilter());

    /// <summary>What the verdict filter's button says: "All", or what is chosen.</summary>
    public string VerdictFilterCaption
    {
        get
        {
            var chosen = VerdictFilterChoices.Where(o => o.IsSelected).ToList();

            return chosen.Count switch
            {
                0 => "All",
                1 => chosen[0].Text,
                _ => $"{chosen.Count} verdicts",
            };
        }
    }

    /// <summary>Whether the verdict filter's list is open.</summary>
    public bool IsVerdictFilterOpen
    {
        get => _isVerdictFilterOpen;
        set => SetProperty(ref _isVerdictFilterOpen, value);
    }

    /// <summary>
    /// The parent's folder, filtered by a substring of it (§14.3).
    /// </summary>
    /// <remarks>
    /// The filter waits for the typing to settle: each keystroke restarts a short timer,
    /// and the rows are re-filtered when it stops, so a long word is not filtered once per
    /// letter.
    /// </remarks>
    public string ParentFolderFilter
    {
        get => _parentFolderFilter;
        set
        {
            if (SetProperty(ref _parentFolderFilter, value))
            {
                _filter.ParentFolderText = value;
                ScheduleFilter();
            }
        }
    }

    /// <summary>
    /// The parent's file name, filtered by a substring of it (§14.3).
    /// </summary>
    /// <remarks>
    /// The filter waits for the typing to settle, the same way the folder's does.
    /// </remarks>
    public string ParentFileNameFilter
    {
        get => _parentFileNameFilter;
        set
        {
            if (SetProperty(ref _parentFileNameFilter, value))
            {
                _filter.ParentFileNameText = value;
                ScheduleFilter();
            }
        }
    }

    /// <summary>The parent states the table can be filtered by.</summary>
    public IReadOnlyList<FileState> ParentStateFilterOptions { get; } = FileState.All;

    /// <summary>The child states the table can be filtered by.</summary>
    public IReadOnlyList<FileState> ChildStateFilterOptions { get; } = FileState.All;

    public AsyncRelayCommand RunCommand => _run ??= new AsyncRelayCommand(RunAsync);

    public AsyncRelayCommand PreviewCommand => _previewCommand ??= new AsyncRelayCommand(PreviewAsync);

    /// <summary>Stops the running pass (§18.8).</summary>
    public RelayCommand CancelPassCommand => _cancelPass ??= new RelayCommand(_ => CancelPass());

    /// <summary>The row the info area shows.</summary>
    public FinderRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(ParentFileName));
                OnPropertyChanged(nameof(ParentState));
                OnPropertyChanged(nameof(ParentSize));
                OnPropertyChanged(nameof(ParentSizeCheck));
                OnPropertyChanged(nameof(ParentDimensions));
                OnPropertyChanged(nameof(ParentFault));
                OnPropertyChanged(nameof(HasParentFault));
                OnPropertyChanged(nameof(ChildState));
                OnPropertyChanged(nameof(ChildSize));
                OnPropertyChanged(nameof(ChildSizeCheck));
                OnPropertyChanged(nameof(ChildDimensions));
                OnPropertyChanged(nameof(ChildFault));
                OnPropertyChanged(nameof(HasChildFault));
                OnPropertyChanged(nameof(ChildCategoryName));
                OnPropertyChanged(nameof(ChildVariantName));
                OnPropertyChanged(nameof(VerdictText));
                OnPropertyChanged(nameof(VerdictIcon));
                OnPropertyChanged(nameof(NoteText));

                Candidates.Clear();
                Rules.Clear();

                if (value is not null)
                {
                    foreach (var candidate in value.Candidates)
                    {
                        Candidates.Add(candidate);
                    }

                    foreach (var rule in FinderVerdictRules.ChecksFor(value))
                    {
                        Rules.Add(rule);
                    }
                }

                // These two depend on the lists above, so they are raised AFTER the lists
                // are filled. Raising them first would leave the pane showing the
                // previous selection's answer.
                OnPropertyChanged(nameof(HasCandidates));
                OnPropertyChanged(nameof(HasPreviewImage));

                // The preview follows the candidate the reader picks (§18.7). Until they
                // pick one, it is the best candidate.
                _selectedCandidate = value?.BestCandidate;
                OnPropertyChanged(nameof(SelectedCandidate));

                ClearPreview();
            }
        }
    }

    public bool HasSelection => Selected is not null;

    // ---- the busy screen (§18.8) ----

    /// <summary>True while a pass is running. Drives the busy screen.</summary>
    public bool IsPassing => IsBusy;

    /// <summary>What the pass is doing right now: Reading pairs, Checking files, ...</summary>
    public string PassStage
    {
        get => _passStage;
        private set => SetProperty(ref _passStage, value);
    }

    /// <summary>The stage's own words: "N of M".</summary>
    public string PassDetail
    {
        get => _passDetail;
        private set => SetProperty(ref _passDetail, value);
    }

    /// <summary>
    /// True while the stage has no total to divide by — only the database pass (§18.8).
    /// </summary>
    public bool PassIsIndeterminate
    {
        get => _passIsIndeterminate;
        private set => SetProperty(ref _passIsIndeterminate, value);
    }

    /// <summary>How far the stage has got.</summary>
    public int PassDone
    {
        get => _passDone;
        private set
        {
            if (SetProperty(ref _passDone, value))
            {
                OnPropertyChanged(nameof(PassPercent));
            }
        }
    }

    /// <summary>What the stage is counting towards.</summary>
    public int PassTotal
    {
        get => _passTotal;
        private set
        {
            if (SetProperty(ref _passTotal, value))
            {
                OnPropertyChanged(nameof(PassPercent));
            }
        }
    }

    /// <summary>The bar's value, as a percentage. Zero while the stage is indeterminate.</summary>
    public double PassPercent =>
        PassIsIndeterminate || PassTotal <= 0 ? 0 : Math.Clamp(PassDone * 100.0 / PassTotal, 0, 100);

    /// <summary>True while there is a pass to cancel.</summary>
    public bool CanCancelPass => IsBusy;

    public string ParentFileName => Selected?.ParentFileName ?? string.Empty;

    public string ParentState => Selected?.Parent.State.Text ?? string.Empty;

    public string ParentSize => Selected?.Parent.Size?.ToString("N0") ?? string.Empty;

    public string ParentSizeCheck => Selected?.Parent.SizeCheck?.Text ?? string.Empty;

    public string ParentDimensions => Selected?.Parent.Dimensions ?? string.Empty;

    /// <summary>Why the parent would not open - worth a reader's attention when it is there but broken.</summary>
    public string ParentFault => Selected?.Parent.Fault ?? string.Empty;

    public bool HasParentFault => !string.IsNullOrEmpty(ParentFault);

    public string ChildState => Selected?.Child.State.Text ?? string.Empty;

    public string ChildSize => Selected?.Child.Size?.ToString("N0") ?? string.Empty;

    public string ChildSizeCheck => Selected?.Child.SizeCheck?.Text ?? string.Empty;

    public string ChildDimensions => Selected?.Child.Dimensions ?? string.Empty;

    /// <summary>Which rendition the child is, by name (§14.2).</summary>
    public string ChildCategoryName => Selected?.ChildCategoryName ?? string.Empty;

    /// <summary>Which variant of the rendition the child is, by name (§14.2).</summary>
    public string ChildVariantName => Selected?.ChildVariantName ?? string.Empty;

    /// <summary>Why the child would not open.</summary>
    public string ChildFault => Selected?.Child.Fault ?? string.Empty;

    public bool HasChildFault => !string.IsNullOrEmpty(ChildFault);

    public string VerdictText => Selected?.Verdict.Text ?? string.Empty;

    public string VerdictIcon => Selected?.VerdictIcon ?? string.Empty;

    public string NoteText => Selected?.VerdictNote ?? string.Empty;

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>
    /// The candidate the preview shows (§18.7).
    /// </summary>
    /// <remarks>
    /// The candidate list and the preview are ONE selection: picking a candidate in the list
    /// shows that candidate's image. Until the reader picks one, it is the best candidate.
    /// </remarks>
    public FinderCandidate? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetProperty(ref _selectedCandidate, value) && value is not null)
            {
                _ = PreviewAsync();
            }
        }
    }

    /// <summary>
    /// Starts the timer that waits for the typing to settle (§14.3).
    /// </summary>
    private void ScheduleFilter()
    {
        if (_filterTimer is null)
        {
            _filterTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(100),
            };
            _filterTimer.Tick += OnFilterTimerTick;
        }

        _filterTimer.Stop();
        _filterTimer.Start();
    }

    /// <summary>
    /// Re-filters the rows once the typing has settled (§14.3).
    /// </summary>
    private void OnFilterTimerTick(object? sender, EventArgs e)
    {
        _filterTimer?.Stop();
        ApplyFilters();
    }

    /// <summary>
    /// Keeps the filter's verdicts in step with the reader's choices (§14.3).
    /// </summary>
    private void OnVerdictFilterOptionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VerdictFilterOption.IsSelected))
        {
            return;
        }

        _filter.Verdicts.Clear();
        foreach (var choice in VerdictFilterChoices)
        {
            if (choice.IsSelected)
            {
                _filter.Verdicts.Add(choice.Verdict);
            }
        }

        OnPropertyChanged(nameof(VerdictFilterCaption));
        ApplyFilters();
    }

    /// <summary>
    /// Unchecks every verdict, which keeps them all (§14.3).
    /// </summary>
    private void ClearVerdictFilter()
    {
        foreach (var choice in VerdictFilterChoices)
        {
            choice.IsSelected = false;
        }
    }

    /// <summary>The image fetched from Dropbox for the preview, or null.</summary>
    public BitmapSource? PreviewImage
    {
        get => _previewImage;
        private set
        {
            if (SetProperty(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreviewImage));
            }
        }
    }

    public bool HasPreviewImage => PreviewImage is not null;

    /// <summary>What the preview area says when there is no image.</summary>
    public string PreviewState
    {
        get => _previewState;
        private set => SetProperty(ref _previewState, value);
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>
    /// Runs the pass and fills the table.
    /// </summary>
    /// <remarks>
    /// The pass opens every parent and child file in the library, so it is long. It runs off
    /// the UI thread and reports progress through the busy screen (§18.8).
    /// </remarks>
    public Task RunAsync() => RunPassAsync(force: true);

    /// <summary>
    /// Runs the pass the first time the tab is shown (§14.1).
    /// </summary>
    /// <remarks>
    /// It does not force a pass: if the scan state already holds one — because Liem's List
    /// ran it, or because this tab was left and came back — those rows are used instead.
    /// </remarks>
    public async Task EnsurePassAsync()
    {
        if (_hasRunPass || IsBusy)
        {
            return;
        }

        // Set before the await, so a pass that fails or is cancelled is not retried behind
        // the reader's back. The Run the pass button is there for another attempt.
        _hasRunPass = true;

        await RunPassAsync(force: false);
    }

    /// <summary>Stops the running pass. Whatever was already loaded stays loaded (§18.8).</summary>
    public void CancelPass() => _passCts?.Cancel();

    private async Task RunPassAsync(bool force)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        PassStage = "Starting";
        PassDetail = string.Empty;
        PassDone = 0;
        PassTotal = 0;
        PassIsIndeterminate = true;
        OnPropertyChanged(nameof(IsPassing));
        OnPropertyChanged(nameof(CanCancelPass));

        _passCts = new CancellationTokenSource();

        try
        {
            var progress = new Progress<FinderProgressInfo>(OnProgress);

            var rows = await finder.GetMediaPairsAsync(
                new ProgressAdapter(progress), force: force, ct: _passCts.Token);

            AllRows.Clear();
            foreach (var row in rows)
            {
                AllRows.Add(row);
            }

            ApplyFilters();

            var counts = rows
                .GroupBy(r => r.Verdict)
                .ToDictionary(g => g.Key, g => g.Count());

            Summary = $"{rows.Count:N0} pair(s). " +
                      string.Join("  ", counts.OrderBy(c => c.Key.Id)
                          .Select(c => $"{c.Key.Text} {c.Value:N0}"));

            SetStatus($"Finder pass finished — {rows.Count:N0} pair(s).");
        }
        catch (OperationCanceledException)
        {
            SetStatus("Finder pass cancelled.");
            log.Info("Finder pass cancelled.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            log.Error($"Finder pass failed: {ex}");
        }
        finally
        {
            _passCts.Dispose();
            _passCts = null;
            IsBusy = false;

            // IsPassing is derived from IsBusy, and the base class only announces IsBusy.
            OnPropertyChanged(nameof(IsPassing));
            OnPropertyChanged(nameof(CanCancelPass));
        }
    }

    /// <summary>
    /// Carries a progress report onto the busy screen (§18.8).
    /// </summary>
    private void OnProgress(FinderProgressInfo info)
    {
        PassStage = info.Stage;
        PassDetail = info.Detail;
        PassDone = info.Done;
        PassTotal = info.Total;

        // Only the database pass has no total: the row count is not known until the query
        // returns. The other three stages count towards a number they already have.
        PassIsIndeterminate = info.Total <= 0;

        SetStatus($"{info.Stage}: {info.Detail}");
    }

    /// <summary>
    /// Fetches the selected candidate's image from Dropbox, for the preview.
    /// </summary>
    public async Task PreviewAsync()
    {
        var candidate = SelectedCandidate;
        if (candidate is null)
        {
            PreviewState = "No candidate to preview.";
            return;
        }

        PreviewState = "Loading…";

        try
        {
            var bytes = await preview.DownloadAsync(candidate.Path);
            if (bytes is null || bytes.Length == 0)
            {
                PreviewImage = null;
                PreviewState = "The image could not be fetched.";
                return;
            }

            // Decoded into a bitmap the WPF image can show. The bytes are held only as long
            // as the bitmap needs them, and nothing is written to disk.
            var decoder = BitmapDecoder.Create(
                new MemoryStream(bytes),
                BitmapCreateOptions.None,
                BitmapCacheOption.OnLoad);

            PreviewImage = decoder.Frames[0];
            PreviewState = string.Empty;
        }
        catch (Exception ex)
        {
            PreviewImage = null;
            PreviewState = ex.Message;
            log.Error($"Candidate preview failed: {ex.Message}");
        }
    }

    /// <summary>Applies the filters to the rows the pass produced.</summary>
    private void ApplyFilters()
    {
        Rows.Clear();
        foreach (var row in AllRows)
        {
            if (_filter.Matches(row))
            {
                Rows.Add(row);
            }
        }

        OnPropertyChanged(nameof(RowsDisplay));
    }

    public string RowsDisplay => $"{Rows.Count:N0} of {AllRows.Count:N0} row(s)";

    private void ClearPreview()
    {
        PreviewImage = null;
        PreviewState = string.Empty;
    }

    /// <summary>Carries the server's progress callback onto the UI thread.</summary>
    private sealed class ProgressAdapter(IProgress<FinderProgressInfo> inner) : IFinderProgress
    {
        public void Report(FinderProgressInfo info) => inner.Report(info);
    }
}
