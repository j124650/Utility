using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Utility.Server.Finder;
using Utility.Server.Finder.Services;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// Liem's List: the pairs the verdict leaves unresolved - no original found, or nothing to
/// restore from at all (§11, §14.5).
/// </summary>
/// <remarks>
/// It is a report on the SAME scan the Finder tab runs (§11), not a second one, so it is
/// only as fresh as the last pass.
/// </remarks>
public sealed class LiemListViewModel(
    IFinderService finder,
    IFinderScanState scanState,
    ILogSink log) : ViewModelBase
{
    private AsyncRelayCommand? _load;

    private FinderRow? _selected;
    private string _summary = string.Empty;

    /// <summary>The unresolved pairs.</summary>
    public ObservableCollection<FinderRow> Rows { get; } = [];

    public AsyncRelayCommand LoadCommand => _load ??= new AsyncRelayCommand(LoadAsync);

    /// <summary>The row the detail pane shows.</summary>
    public FinderRow? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(SelectedFileName));
                OnPropertyChanged(nameof(SelectedVerdict));
                OnPropertyChanged(nameof(SelectedNote));
                OnPropertyChanged(nameof(SelectedCandidates));
            }
        }
    }

    public bool HasSelection => Selected is not null;

    public string SelectedFileName => Selected?.ParentFileName ?? string.Empty;

    public string SelectedVerdict => Selected?.Verdict.Text ?? string.Empty;

    public string SelectedNote => Selected?.VerdictNote ?? string.Empty;

    /// <summary>The rejected candidates - the ones the size rule did not confirm.</summary>
    public string SelectedCandidates
    {
        get
        {
            if (Selected is null)
            {
                return string.Empty;
            }

            return Selected.Candidates.Count == 0
                ? "No candidates."
                : string.Join(Environment.NewLine, Selected.Candidates.Select(c =>
                    $"{c.Rank}  {c.Name}  {c.SizeDisplay}  {c.WhatMatched}"));
        }
    }

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    /// <summary>
    /// Loads the list. It runs the scan only when there is no scan to report on.
    /// </summary>
    public async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            var progress = new Progress<FinderProgressInfo>(info =>
            {
                SetStatus($"{info.Stage}: {info.Detail}");
            });

            var rows = await finder.GetLiemListAsync(new ProgressAdapter(progress));

            Rows.Clear();
            foreach (var row in rows)
            {
                Rows.Add(row);
            }

            Summary = $"{Rows.Count:N0} pair(s) left unresolved.";
            SetStatus(Summary);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            log.Error($"Liem's List failed: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Carries the server's progress callback onto the UI thread.</summary>
    private sealed class ProgressAdapter(IProgress<FinderProgressInfo> inner) : IFinderProgress
    {
        public void Report(FinderProgressInfo info) => inner.Report(info);
    }
}
