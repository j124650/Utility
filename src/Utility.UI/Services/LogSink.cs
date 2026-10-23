using System.Collections.ObjectModel;

namespace Utility.UI.Services;

/// <summary>
/// In-memory log pane. Keeps the most recent lines for the UI to bind to.
/// <para>
/// Lived at the bottom of DashboardViewModel.cs until that file was rewritten for the
/// tree-only dashboard; it is its own file now because several services consume it.
/// </para>
/// </summary>
public interface ILogSink
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);

    ObservableCollection<string> Lines { get; }
}

public sealed class LogSink : ILogSink
{
    private const int MaxLines = 500;

    public ObservableCollection<string> Lines { get; } = [];

    public void Info(string message) => Add("INFO", message);

    public void Warn(string message) => Add("WARN", message);

    public void Error(string message) => Add("ERR ", message);

    private void Add(string level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss} [{level}] {message}";

        // Log lines can arrive from a background thread; the collection is bound to the
        // UI, so the insert must happen on the dispatcher.
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => Insert(line));
            return;
        }

        Insert(line);
    }

    private void Insert(string line)
    {
        Lines.Insert(0, line);
        while (Lines.Count > MaxLines)
        {
            Lines.RemoveAt(Lines.Count - 1);
        }
    }
}