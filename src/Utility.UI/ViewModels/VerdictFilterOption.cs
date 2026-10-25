using System.ComponentModel;
using Utility.Server.Finder;

namespace Utility.UI.ViewModels;

/// <summary>
/// One verdict in the table's verdict filter (§14.3).
/// </summary>
/// <remarks>
/// The verdict itself is the server's own (§7) — this adds nothing to it. What this carries
/// is the reader's choice: whether the table keeps the rows with this verdict.
/// </remarks>
public sealed class VerdictFilterOption : INotifyPropertyChanged
{
    private bool _isSelected;

    public VerdictFilterOption(FinderVerdict verdict)
    {
        Verdict = verdict;
    }

    /// <summary>The verdict (§7).</summary>
    public FinderVerdict Verdict { get; }

    /// <summary>The verdict's wording, as the reader knows it (§7).</summary>
    public string Text => Verdict.Text;

    /// <summary>Whether the table keeps the rows with this verdict (§14.3).</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
