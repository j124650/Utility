using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
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

    private FinderRow? _selected;
    private FinderCandidate? _selectedCandidate;
    private BitmapSource? _previewImage;
    private string _previewState = string.Empty;
    private string _filterText = string.Empty;
    private FinderVerdict? _verdictFilter;
    private string _summary = string.Empty;

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

    /// <summary>The parent states the table can be filtered by.</summary>
    public IReadOnlyList<FileState> ParentStateFilterOptions { get; } = FileState.All;

    /// <summary>The child states the table can be filtered by.</summary>
    public IReadOnlyList<FileState> ChildStateFilterOptions { get; } = FileState.All;

    public AsyncRelayCommand RunCommand => _run ??= new AsyncRelayCommand(RunAsync);

    public AsyncRelayCommand PreviewCommand => _previewCommand ??= new AsyncRelayCommand(PreviewAsync);

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
    /// Free-text filter over the file name, the folder, a candidate's path or a candidate's
    /// name (§14.3).
    /// </summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
                _filter.Text = value;
                ApplyFilters();
            }
        }
    }

    /// <summary>Which verdict to show. Null shows all of them.</summary>
    public FinderVerdict? VerdictFilter
    {
        get => _verdictFilter;
        set
        {
            if (SetProperty(ref _verdictFilter, value))
            {
                _filter.Verdicts.Clear();
                if (value.HasValue)
                {
                    _filter.Verdicts.Add(value.Value);
                }

                ApplyFilters();
            }
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
    /// the UI thread and reports progress through the status line.
    /// </remarks>
    public async Task RunAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<FinderProgressInfo>(info =>
            {
                SetStatus($"{info.Stage}: {info.Detail}");
            });

            var rows = await finder.GetMediaPairsAsync(new ProgressAdapter(progress), force: true);

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
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            log.Error($"Finder pass failed: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
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
