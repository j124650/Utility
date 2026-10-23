using System.Collections.ObjectModel;
using Utility.Server.DropBoxSync;

namespace Utility.UI.Services;

/// <summary>
/// Holds the synced Dropbox tree across page navigations.
/// <para>
/// A singleton because the tree is read ONCE. The dashboard page is transient, so keeping
/// the nodes on a view model would reload them every time the user navigated to the page -
/// exactly the behaviour to avoid. Refresh happens only when explicitly requested.
/// </para>
/// </summary>
public interface IDropBoxTreeState
{
    ObservableCollection<DropBoxTreeNode> Nodes { get; }

    DropBoxTreeSummary? Summary { get; }

    bool IsLoaded { get; }

    bool IsBusy { get; }

    string? Error { get; }

    /// <summary>
    /// Raised whenever Summary, IsLoaded, IsBusy or Error change.
    /// <para>
    /// Required because the tree is read at startup - before any DashboardViewModel
    /// exists. Without it a page created afterwards has no way to learn what already
    /// happened and renders as empty.
    /// </para>
    /// </summary>
    event EventHandler? StateChanged;

    /// <summary>
    /// Loads the tree from the database, but only when it has never been loaded or when
    /// <paramref name="force"/> is set (the toolbar's read/refresh button).
    /// </summary>
    Task LoadAsync(bool force = false, CancellationToken ct = default);
}

public sealed class DropBoxTreeState(IDropBoxTreeService tree, ILogSink log) : IDropBoxTreeState
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? StateChanged;

    private DropBoxTreeSummary? _summary;
    private bool _isLoaded;
    private bool _isBusy;
    private string? _error;

    public ObservableCollection<DropBoxTreeNode> Nodes { get; } = [];

    public DropBoxTreeSummary? Summary
    {
        get => _summary;
        private set => Set(ref _summary, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        private set => Set(ref _isLoaded, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => Set(ref _isBusy, value);
    }

    public string? Error
    {
        get => _error;
        private set => Set(ref _error, value);
    }

    private void Set<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadAsync(bool force = false, CancellationToken ct = default)
    {
        // Without the gate, the Read button plus a startup load could race and interleave
        // Clear() with Add() on the collection the UI is binding to.
        await _gate.WaitAsync(ct);
        try
        {
            if (IsLoaded && !force)
            {
                return;
            }

            IsBusy = true;
            Error = null;

            var result = await tree.GetSyncedTreeAsync(ct: ct);
            var roots = result.Roots;
            var summary = result.Summary;

            Nodes.Clear();
            foreach (var root in roots)
            {
                Nodes.Add(root);
            }

            Summary = summary;
            IsLoaded = true;

            log.Info($"Synced tree: {summary.Folders} folder(s), {summary.Files} file(s), " +
                     $"{summary.WithDimensions} with dimensions.");
        }
        catch (OperationCanceledException)
        {
            // A cancelled read leaves the previous tree intact.
            Error = "Cancelled.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            log.Error($"Reading the synced tree failed: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _gate.Release();
        }
    }
}