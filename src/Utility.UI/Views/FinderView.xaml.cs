using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Utility.UI.ViewModels;

namespace Utility.UI.Views;

public partial class FinderView : UserControl
{
    public FinderView(IServiceProvider services)
    {
        InitializeComponent();

        // The app-wide DataGrid style fixes the row height at 26 px, which is right for the
        // flat grids and wrong for this one: a record is a card of seven lines (§18.4), and
        // a fixed height clips it to the first line. NaN is the DataGrid's "size the row to
        // its content" value - it cannot be written in XAML, because RowHeight is a plain
        // double and "Auto" is not a number.
        PairsGrid.RowHeight = double.NaN;

        // The view model is a singleton (§14.1), so the rows, the filter and the selected
        // row survive switching tabs. The view itself is transient - it is just a view.
        DataContext = services.GetRequiredService<FinderViewModel>();

        // The filters sit above the grid, so they have to be told the grid's column widths
        // and the grid's sideways scroll (§20). The grid measures its star columns against
        // its own viewport, which is narrower than the filter row's by the width of the
        // vertical scrollbar, so the numbers cannot simply be written twice in XAML.
        PairsGrid.LayoutUpdated += OnPairsGridLayoutUpdated;
        Loaded += OnLoaded;
    }

    /// <summary>
    /// The page is up.
    /// <para>
    /// The Finder's data is read HERE - on the page's first activation - and not at
    /// startup. The pass opens every file in the library and takes a long time; doing it
    /// at startup would make the app wait for it before showing anything, and would
    /// start a pass the reader may never look at.
    /// </para>
    /// <para>
    /// The view model is a singleton and remembers that it has run, so coming back to
    /// this tab does not start a second pass.
    /// </para>
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SyncFilterColumns();

        if (DataContext is FinderViewModel finder)
        {
            await finder.EnsurePassAsync();
        }
    }

    /// <summary>
    /// Keeps the filter row's columns the same widths as the grid's columns, and moves the
    /// row sideways with the grid (§20).
    /// </summary>
    private void OnPairsGridLayoutUpdated(object? sender, EventArgs e)
    {
        SyncFilterColumns();
    }

    /// <summary>
    /// Copies the grid's column widths onto the filter row's columns (§20).
    /// </summary>
    private void SyncFilterColumns()
    {
        var columns = PairsGrid.Columns;

        if (columns.Count < 4)
        {
            return;
        }

        var filterColumns = new[] { FilterColumn0, FilterColumn1, FilterColumn2, FilterColumn3 };

        for (var i = 0; i < filterColumns.Length; i++)
        {
            if (TryGetColumnWidth(columns[i], out var width))
            {
                SetColumnWidth(filterColumns[i], width);
            }
        }

        SyncFilterScroll();
    }

    /// <summary>
    /// The width a grid column takes in the table (§20).
    ///
    /// A COLLAPSED column takes none - the leading icon column is hidden, so the filter
    /// row's first column must take none either, or the filters sit one column to the
    /// right of the columns they filter. A column that has not been measured yet has no
    /// width to copy, and is left alone.
    /// </summary>
    private static bool TryGetColumnWidth(DataGridColumn column, out double width)
    {
        if (column.Visibility == Visibility.Collapsed)
        {
            width = 0;
            return true;
        }

        width = column.ActualWidth;

        return width > 0 && !double.IsNaN(width);
    }

    /// <summary>
    /// Sets one filter column's width, if it has changed (§20).
    /// </summary>
    private static void SetColumnWidth(ColumnDefinition column, double width)
    {
        if (width < 0 || double.IsNaN(width))
        {
            return;
        }

        if (Math.Abs(column.ActualWidth - width) < 0.5)
        {
            return;
        }

        column.Width = new GridLength(width);
    }

    /// <summary>
    /// Moves the filter row sideways with the grid, so a filter stays over its column when
    /// the table is scrolled (§20).
    /// </summary>
    private void SyncFilterScroll()
    {
        var scroll = FindScrollViewer(PairsGrid);

        if (scroll is null)
        {
            return;
        }

        FilterRowScroll.X = -scroll.HorizontalOffset;
    }

    /// <summary>
    /// Finds the scroll viewer the grid scrolls its rows in (§20).
    /// </summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);

            if (child is ScrollViewer viewer)
            {
                return viewer;
            }

            var found = FindScrollViewer(child);

            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }
}
