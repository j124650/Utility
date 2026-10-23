using Microsoft.EntityFrameworkCore;
using Utility.Server.ML.Data;
using Utility.Server.ML.Entities;

namespace Utility.Server.ML.Services;

/// <summary>
/// Read-only query surface over the <c>ml.</c> schema. Every read goes through this service;
/// callers never see <see cref="MlDbContext"/> or build queries against EF directly.
/// <para>
/// The schema is owned by another system, so nothing here mutates. Large tables are always
/// paged or projected — <c>ml.media</c> holds ~765k rows.
/// </para>
/// </summary>
public interface IMlDataService
{
    // ---- Settings ----

    /// <summary>Reads the keyless single-row <c>ml.Settings</c> table.</summary>
    Task<MlSettings?> GetSettingsAsync(CancellationToken ct = default);

    // ---- Lookups ----

    Task<IReadOnlyList<MediaType>> GetMediaTypesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaCategory>> GetMediaCategoriesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaSubCategory>> GetMediaSubCategoriesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<MediaSubType>> GetMediaSubTypesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ExportStatus>> GetExportStatusesAsync(CancellationToken ct = default);

    // ---- Media ----

    Task<Media?> GetMediaAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Paged, filtered selection from <c>ml.media</c>.
    /// Returns one extra row's worth of lookahead as <c>HasMore</c> instead of a second COUNT.
    /// </summary>
    Task<MediaPage> GetMediaAsync(MediaQuery query, CancellationToken ct = default);

    /// <summary>Counts media matching the same filters as <see cref="GetMediaAsync(MediaQuery, CancellationToken)"/>.</summary>
    Task<long> CountMediaAsync(MediaQuery? query = null, CancellationToken ct = default);

    /// <summary>
    /// Streams matching media without loading it all into memory — for exports or bulk scans.
    /// </summary>
    IAsyncEnumerable<Media> StreamMediaAsync(MediaQuery? query = null, CancellationToken ct = default);

    // ---- Exports ----

    Task<IReadOnlyList<ExportHeader>> GetExportHeadersAsync(int take = 50, CancellationToken ct = default);

    Task<ExportHeader?> GetExportHeaderAsync(int id, CancellationToken ct = default);

    Task<IReadOnlyList<ExportDetail>> GetExportDetailsAsync(int exportHeaderId, CancellationToken ct = default);

    Task<IReadOnlyList<ExportTemplate>> GetExportTemplatesAsync(int take = 100, CancellationToken ct = default);

    // ---- Temp tables ----

    Task<IReadOnlyList<MediaTempFilesNames>> GetTempFileNamesAsync(int take = 100, CancellationToken ct = default);

    Task<IReadOnlyList<MediaTempCatalogPaths>> GetTempCatalogPathsAsync(int take = 100, CancellationToken ct = default);
}

/// <summary>One page of media results plus the lookahead flag.</summary>
public sealed record MediaPage(IReadOnlyList<Media> Items, bool HasMore, int PageIndex, int PageSize)
{
    public static MediaPage Empty(int pageIndex, int pageSize) => new([], false, pageIndex, pageSize);
}

public sealed class MlDataService(MlDbContext db) : IMlDataService
{
    public async Task<MlSettings?> GetSettingsAsync(CancellationToken ct = default) =>
        await db.Settings.AsNoTracking().FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<MediaType>> GetMediaTypesAsync(CancellationToken ct = default) =>
        await db.MediaTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<MediaCategory>> GetMediaCategoriesAsync(CancellationToken ct = default) =>
        await db.MediaCategories.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<MediaSubCategory>> GetMediaSubCategoriesAsync(CancellationToken ct = default) =>
        await db.MediaSubCategories.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<MediaSubType>> GetMediaSubTypesAsync(CancellationToken ct = default) =>
        await db.MediaSubTypes.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public async Task<IReadOnlyList<ExportStatus>> GetExportStatusesAsync(CancellationToken ct = default) =>
        await db.ExportStatuses.AsNoTracking().OrderBy(x => x.Name).ToListAsync(ct);

    public Task<Media?> GetMediaAsync(Guid id, CancellationToken ct = default) =>
        db.Media.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<MediaPage> GetMediaAsync(MediaQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var pageIndex = query.NormalizedPageIndex;
        var pageSize = query.NormalizedPageSize;

        var items = await ApplyFilters(ApplySort(db.Media.AsNoTracking(), query), query)
            .Skip(pageIndex * pageSize)
            .Take(pageSize + 1) // lookahead
            .ToListAsync(ct);

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        return new MediaPage(items, hasMore, pageIndex, pageSize);
    }

    public async Task<long> CountMediaAsync(MediaQuery? query = null, CancellationToken ct = default)
    {
        query ??= new MediaQuery();
        return await ApplyFilters(db.Media.AsNoTracking(), query).LongCountAsync(ct);
    }

    public async IAsyncEnumerable<Media> StreamMediaAsync(
        MediaQuery? query = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        query ??= new MediaQuery();

        await foreach (var item in ApplySort(db.Media.AsNoTracking(), query)
                           .AsAsyncEnumerable()
                           .WithCancellation(ct))
        {
            yield return item;
        }
    }

    public async Task<IReadOnlyList<ExportHeader>> GetExportHeadersAsync(int take = 50, CancellationToken ct = default) =>
        await db.ExportHeaders
            .AsNoTracking()
            .OrderByDescending(x => x.CreateDate)
            .ThenByDescending(x => x.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);

    public Task<ExportHeader?> GetExportHeaderAsync(int id, CancellationToken ct = default) =>
        db.ExportHeaders.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct);

    public async Task<IReadOnlyList<ExportDetail>> GetExportDetailsAsync(int exportHeaderId, CancellationToken ct = default) =>
        await db.ExportDetails
            .AsNoTracking()
            .Where(x => x.ExportHeaderId == exportHeaderId)
            .OrderBy(x => x.Id)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ExportTemplate>> GetExportTemplatesAsync(int take = 100, CancellationToken ct = default) =>
        await db.ExportTemplates
            .AsNoTracking()
            .OrderByDescending(x => x.Created)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MediaTempFilesNames>> GetTempFileNamesAsync(int take = 100, CancellationToken ct = default) =>
        await db.MediaTempFilesNames
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MediaTempCatalogPaths>> GetTempCatalogPathsAsync(int take = 100, CancellationToken ct = default) =>
        await db.MediaTempCatalogPaths
            .AsNoTracking()
            .OrderBy(x => x.Id)
            .Take(Math.Clamp(take, 1, 1000))
            .ToListAsync(ct);

    private static IQueryable<Media> ApplySort(IQueryable<Media> query, MediaQuery q) => q.SortBy switch
    {
        MediaSortField.DateAdded => q.Descending
            ? query.OrderByDescending(x => x.DateAdded).ThenBy(x => x.Id)
            : query.OrderBy(x => x.DateAdded).ThenBy(x => x.Id),
        MediaSortField.Size => q.Descending
            ? query.OrderByDescending(x => x.Size).ThenBy(x => x.Id)
            : query.OrderBy(x => x.Size).ThenBy(x => x.Id),
        MediaSortField.FileName => q.Descending
            ? query.OrderByDescending(x => x.FileName).ThenBy(x => x.Id)
            : query.OrderBy(x => x.FileName).ThenBy(x => x.Id),
        _ => q.Descending
            ? query.OrderByDescending(x => x.Id)
            : query.OrderBy(x => x.Id)
    };

    private static IQueryable<Media> ApplyFilters(IQueryable<Media> query, MediaQuery q)
    {
        if (q.MediaId.HasValue)
        {
            query = query.Where(x => x.Id == q.MediaId.Value);
        }

        if (q.MediaTypeId.HasValue)
        {
            query = query.Where(x => x.MediaTypeId == q.MediaTypeId.Value);
        }

        if (q.MediaCategoryId.HasValue)
        {
            query = query.Where(x => x.MediaCategoryId == q.MediaCategoryId.Value);
        }

        if (q.MediaSubCategoryId.HasValue)
        {
            query = query.Where(x => x.MediaSubCategoryId == q.MediaSubCategoryId.Value);
        }

        if (!string.IsNullOrWhiteSpace(q.ExternalKey))
        {
            query = query.Where(x => x.ExternalKey == q.ExternalKey);
        }

        if (!string.IsNullOrWhiteSpace(q.FileNameContains))
        {
            // EF.Functions.Like translates to SQL LIKE; the column is nvarchar and nullable.
            var pattern = $"%{q.FileNameContains}%";
            query = query.Where(x => x.FileName != null && EF.Functions.Like(x.FileName, pattern));
        }

        if (q.AddedOnOrAfter.HasValue)
        {
            query = query.Where(x => x.DateAdded >= q.AddedOnOrAfter.Value);
        }

        if (q.MinSizeBytes.HasValue)
        {
            query = query.Where(x => x.Size >= q.MinSizeBytes.Value);
        }

        return query;
    }
}