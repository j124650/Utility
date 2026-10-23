using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Utility.Server.ML.Entities;
using Utility.Server.ML.Services;
using Utility.UI.Mvvm;
using Utility.UI.Services;

namespace Utility.UI.ViewModels;

/// <summary>
/// Browse media stored in the <c>ml.media</c> table. Read-only, always paged —
/// the table holds several hundred thousand rows.
/// </summary>
public sealed class MediaViewModel(IMlDataService ml, ILogSink log) : ViewModelBase
{
    private const int PageSize = 100;

    private AsyncRelayCommand? _search;
    private AsyncRelayCommand? _next;
    private AsyncRelayCommand? _first;

    private string _filterText = string.Empty;
    private int _pageIndex;
    private bool _hasMore;
    private string _summary = string.Empty;
    private short? _mediaTypeId;
    private short? _mediaCategoryId;

    public ObservableCollection<Media> Media { get; } = [];

    public ObservableCollection<MediaType> MediaTypes { get; } = [];

    public ObservableCollection<MediaCategory> MediaCategories { get; } = [];

    public AsyncRelayCommand SearchCommand => _search ??= new AsyncRelayCommand(SearchAsync);

    public AsyncRelayCommand FirstPageCommand => _first ??= new AsyncRelayCommand(FirstPageAsync);

    public AsyncRelayCommand NextPageCommand => _next ??= new AsyncRelayCommand(NextPageAsync);

    /// <summary>Free-text filter matched against the file name.</summary>
    public string FilterText
    {
        get => _filterText;
        set => SetProperty(ref _filterText, value);
    }

    public short? MediaTypeId
    {
        get => _mediaTypeId;
        set => SetProperty(ref _mediaTypeId, value);
    }

    public short? MediaCategoryId
    {
        get => _mediaCategoryId;
        set => SetProperty(ref _mediaCategoryId, value);
    }

    public bool HasMore
    {
        get => _hasMore;
        private set
        {
            if (SetProperty(ref _hasMore, value))
            {
                OnPropertyChanged(nameof(NextPageEnabled));
            }
        }
    }

    public bool NextPageEnabled => HasMore;

    public int PageIndex
    {
        get => _pageIndex;
        private set => SetProperty(ref _pageIndex, value);
    }

    public string PageDisplay => $"Page {PageIndex + 1} — {Media.Count} row(s)";

    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }

    public async Task SearchAsync()
    {
        PageIndex = 0;
        await LoadAsync();
    }

    public async Task FirstPageAsync()
    {
        PageIndex = 0;
        await LoadAsync();
    }

    public async Task NextPageAsync()
    {
        if (!HasMore)
        {
            return;
        }

        PageIndex++;
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        IsBusy = true;
        try
        {
            // Lookups power the filter dropdowns; load once per page visit.
            if (MediaTypes.Count == 0)
            {
                foreach (var type in await ml.GetMediaTypesAsync())
                {
                    MediaTypes.Add(type);
                }
            }

            if (MediaCategories.Count == 0)
            {
                foreach (var category in await ml.GetMediaCategoriesAsync())
                {
                    MediaCategories.Add(category);
                }
            }

            var query = new MediaQuery
            {
                PageIndex = PageIndex,
                PageSize = PageSize,
                MediaTypeId = MediaTypeId,
                MediaCategoryId = MediaCategoryId,
                FileNameContains = string.IsNullOrWhiteSpace(FilterText) ? null : FilterText.Trim()
            };

            var page = await ml.GetMediaAsync(query);

            Media.Clear();
            foreach (var item in page.Items)
            {
                Media.Add(item);
            }

            HasMore = page.HasMore;
            SetStatus(page.HasMore
                ? $"{Media.Count} row(s) loaded. More available."
                : $"{Media.Count} row(s) loaded. End of results.");
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message);
            log.Error($"Media search failed: {ex.Message}");
        }
        finally
        {
            OnPropertyChanged(nameof(PageDisplay));
            IsBusy = false;
        }
    }
}
