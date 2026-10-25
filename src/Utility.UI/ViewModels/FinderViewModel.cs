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
    IDropBoxPreviewClient preview,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _run;
    private AsyncRelayCommand? _previewCommand;

    private FinderRow? _selected;
    private BitmapSource? _previewImage;
    private string _previewState = string.Empty;
    private string _filterText = string.Empty;
    private FinderVerdict? _verdictFilter;
    private string _summary = string.Empty;
    private bool _hasRun;

    /// <summary>Every row the pass produced. The filters narrow this list in memory.</summary>
    public ObservableCollection<FinderRow> AllRows { get; } = [];

    /// <summary>The rows the filters leave. This is what the table binds to.</summary>
    public ObservableCollection<FinderRow> Rows { get; } = [];

    /// <summary>The candidates of the selected row.</summary>
    public ObservableCollection<FinderCandidate> Candidates { get; } = [];

    /// <summary>
    /// The checks the verdict rests on (§18.6) — one line each, so the reader sees why the
    /// verdict came out the way it did.
    /// </summary>
    public ObservableCollection<FinderCheck> Checks { get; } = [];

    /// <summary>The verdicts the table can be filtered by.</summary>
    public IReadOnlyList<FinderVerdict> VerdictFilterOptions { get; } =
    [
        FinderVerdict.NothingToDo,
        FinderVerdict.RestoreParent,
        FinderVerdict.RegenerateChild,
        FinderVerdict.RestoreParentThenRegenerateChild,
        FinderVerdict.NoOriginalNeedsDecision,
        FinderVerdict.CannotRestore
    ];

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
                OnPropertyChanged(nameof(ParentSizeCheck));
                OnPropertyChanged(nameof(ParentDimensions));
                OnPropertyChanged(nameof(ParentSize));
                OnPropertyChanged(nameof(ParentFault));
                OnPropertyChanged(nameof(HasParentFault));
                OnPropertyChanged(nameof(ChildState));
                OnPropertyChanged(nameof(ChildSizeCheck));
                OnPropertyChanged(nameof(ChildDimensions));
                OnPropertyChanged(nameof(ChildSize));
                OnPropertyChanged(nameof(ChildFault));
                OnPropertyChanged(nameof(HasChildFault));
                OnPropertyChanged(nameof(VerdictText));
                OnPropertyChanged(nameof(NoteText));
                OnPropertyChanged(nameof(HasCandidates));
                OnPropertyChanged(nameof(HasPreviewImage));

                Candidates.Clear();
                Checks.Clear();
                if (value is not null)
                {
                    foreach (var candidate in value.Candidates)
                    {
                        Candidates.Add(candidate);
                    }

                    // The checks the verdict rests on, in the order of §7 — ✓ where the rule is
                    // respected, ✕ where it is not.
                    Checks.Add(new FinderCheck(value.CheckParentOpens, "parent file opens"));
                    Checks.Add(new FinderCheck(value.CheckChildOpens, "child file opens"));
                    Checks.Add(new FinderCheck(value.CheckCandidatesFound, "candidates found"));
                    Checks.Add(new FinderCheck(value.CheckOriginalConfirmed, "original confirmed by size"));
                }

                ClearPreview();
            }
        }
    }

    public bool HasSelection => Selected is not null;

    public string ParentFileName => Selected?.ParentFileName ?? string.Empty;

    public string ParentState => Selected is null ? string.Empty : Describe(Selected.Parent);

    public string ParentSizeCheck => Selected is null ? string.Empty : SizeCheckText(Selected.Parent);

    public string ParentDimensions => Selected is null ? string.Empty : Selected.Parent.DimensionsDisplay;

    /// <summary>The parent's recorded size in bytes.</summary>
    public string ParentSize => Selected?.Parent.SizeDisplay ?? string.Empty;

    /// <summary>Why the parent would not open — worth a reader's attention when it is there but broken.</summary>
    public string ParentFault => Selected?.Parent.Fault ?? string.Empty;

    public bool HasParentFault => !string.IsNullOrEmpty(ParentFault);

    public string ChildState => Selected is null ? string.Empty : Describe(Selected.Child);

    public string ChildSizeCheck => Selected is null ? string.Empty : SizeCheckText(Selected.Child);

    public string ChildDimensions => Selected is null ? string.Empty : Selected.Child.DimensionsDisplay;

    /// <summary>The child's recorded size in bytes.</summary>
    public string ChildSize => Selected?.Child.SizeDisplay ?? string.Empty;

    /// <summary>Why the child would not open.</summary>
    public string ChildFault => Selected?.Child.Fault ?? string.Empty;

    public bool HasChildFault => !string.IsNullOrEmpty(ChildFault);

    public string VerdictText => Selected?.Verdict.ToString() ?? string.Empty;

    public string NoteText => Selected?.Note ?? string.Empty;

    public bool HasCandidates => Candidates.Count > 0;

    /// <summary>Free-text filter over the file name and the folder.</summary>
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (SetProperty(ref _filterText, value))
            {
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

            var rows = await finder.GetRowsAsync(new ProgressAdapter(progress));

            AllRows.Clear();
            foreach (var row in rows)
            {
                AllRows.Add(row);
            }

            _hasRun = true;
            ApplyFilters();

            var counts = rows
                .GroupBy(r => r.Verdict)
                .ToDictionary(g => g.Key, g => g.Count());

            Summary = $"{rows.Count:N0} pair(s). " +
                      string.Join("  ", counts.OrderBy(c => c.Key)
                          .Select(c => $"{c.Key} {c.Value:N0}"));

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
        var candidate = Selected?.BestCandidate;
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
        var text = FilterText?.Trim();

        var filtered = AllRows.Where(row =>
        {
            if (VerdictFilter.HasValue && row.Verdict != VerdictFilter.Value)
            {
                return false;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                return true;
            }

            return row.ParentFileName.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.ParentFolder.Contains(text, StringComparison.OrdinalIgnoreCase);
        });

        Rows.Clear();
        foreach (var row in filtered)
        {
            Rows.Add(row);
        }

        OnPropertyChanged(nameof(RowsDisplay));
    }

    public string RowsDisplay => $"{Rows.Count:N0} of {AllRows.Count:N0} row(s)";

    private void ClearPreview()
    {
        PreviewImage = null;
        PreviewState = string.Empty;
    }

    private static string Describe(FinderFileState state) => state.State switch
    {
        FileState.Ok => "ok",
        FileState.Missing => "missing",
        FileState.Corrupted => "corrupted",
        _ => string.Empty
    };

    private static string SizeCheckText(FinderFileState state) => state.SizeCheck switch
    {
        Server.Finder.SizeCheck.Match => "match",
        Server.Finder.SizeCheck.Differs => "differs",
        _ => string.Empty
    };

    /// <summary>Carries the server's progress callback onto the UI thread.</summary>
    private sealed class ProgressAdapter(IProgress<FinderProgressInfo> inner) : IFinderProgress
    {
        public void Report(FinderProgressInfo info) => inner.Report(info);
    }
}

/// <summary>
/// One check the verdict rests on, with the mark that says whether the rule is respected.
/// </summary>
/// <param name="Respected">True where the rule holds, false where it does not.</param>
/// <param name="Text">What the check is about.</param>
public sealed record FinderCheck(bool Respected, string Text)
{
    /// <summary>✓ where the rule is respected, ✕ where it is not.</summary>
    public string Mark => Respected ? "✓" : "✕";
}
