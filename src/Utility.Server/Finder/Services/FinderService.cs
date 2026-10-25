using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Utility.Server.Finder;
using Utility.Server.ML.Services;
using Utility.Server.Options;

namespace Utility.Server.Finder.Services;

/// <summary>
/// Gathers the pairs and everything the Finder tab shows about them.
/// </summary>
/// <remarks>
/// The pass runs in four phases:
/// <list type="number">
/// <item><description>One set-based query for the pairs and their name-matched candidates.</description></item>
/// <item><description>A visit to every file on disk, for the two states and the size checks.</description></item>
/// <item><description>The similarity widening, for the pairs that need it.</description></item>
/// <item><description>The checks, the verdict and the row.</description></item>
/// </list>
/// Nothing is written - not to the library, not to the database.
/// </remarks>
public sealed class FinderService(
    IMlDataService ml,
    IFinderLibraryService library,
    IFinderScanState scanState,
    IOptions<ServerOptions> options) : IFinderService
{
    public async Task<IReadOnlyList<FinderRow>> GetMediaPairsAsync(
        IFinderProgress? progress = null,
        bool force = false,
        CancellationToken ct = default)
    {
        // The rows are kept so the Finder tab and Liem's List share one pass. A second pass
        // is run only when the caller asks for one.
        if (!force && scanState.IsLoaded)
        {
            progress?.Report(new FinderProgressInfo("Reading pairs", 1, 1, $"{scanState.Rows.Count} pair(s), from the last pass"));
            return scanState.Rows;
        }

        var settings = await ml.GetSettingsAsync(ct);
        var configuredRoot = settings?.MediaLibraryLocalPath;

        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            throw new InvalidOperationException(
                "ml.Settings.MediaLibraryLocalPath is not configured - the library root is unknown.");
        }

        // The configured root is the starting point, not the answer: the library may sit
        // directly under it, or under a folder named after the network share. It is resolved
        // against the folders the media rows name.
        var mediaFolders = await ml.GetMediaFoldersAsync(ct);

        var libraryRoot = FinderLibraryRoot.Resolve(
            configuredRoot, settings?.MediaLibraryUncPath, mediaFolders);

        // ---- Phase 1: one set-based query for the pairs and their name-matched candidates ----
        progress?.Report(new FinderProgressInfo("Reading pairs", 0, 1, "Querying ml.media and ml.DropBoxNode"));

        var pairs = await LoadPairsAsync(ct);
        progress?.Report(new FinderProgressInfo("Reading pairs", 1, 1, $"{pairs.Count} pair(s)"));

        // ---- Phase 2: visit every file on disk ----
        var states = new FileStates[pairs.Count];
        var done = 0;
        foreach (var index in Enumerable.Range(0, pairs.Count))
        {
            ct.ThrowIfCancellationRequested();

            var pair = pairs[index];
            states[index] = new FileStates(
                await library.GetFileStateAsync(libraryRoot, pair.ParentFolder, pair.ParentId,
                    pair.ParentSize, pair.ParentWidth, pair.ParentHeight, ct),
                await library.GetFileStateAsync(libraryRoot, pair.ChildFolder, pair.ChildId,
                    pair.ChildSize, pair.ChildWidth, pair.ChildHeight, ct));

            done++;
            if (done % 500 == 0 || done == pairs.Count)
            {
                progress?.Report(new FinderProgressInfo("Checking files", done, pairs.Count, $"{done} of {pairs.Count}"));
            }
        }

        // ---- Phase 3: widen the candidates of the pairs that need it ----
        var toWiden = pairs
            .Where((p, i) => !states[i].Parent.IsOk && p.NameMatchedCandidates.Count == 0)
            .ToList();

        if (toWiden.Count > 0)
        {
            progress?.Report(new FinderProgressInfo("Widening candidates", 0, toWiden.Count, $"{toWiden.Count} pair(s)"));

            // One set-based join for all of them, not a query per pair: the pairs share one
            // connection and one scan of the index.
            await WidenCandidatesAsync(toWiden, ct);

            progress?.Report(new FinderProgressInfo("Widening candidates", toWiden.Count, toWiden.Count, $"{toWiden.Count} pair(s)"));
        }

        // ---- Phase 4: the checks, the verdict and the row ----
        var rows = new List<FinderRow>(pairs.Count);
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var state = states[i];

            var candidates = FinderRanking.Rank(
                pair.NameMatchedCandidates.Select(c => new CandidateSeed(c.Name, c.Size, c.Width, c.Height, c.Path, c.Modified)).ToList(),
                pair.ParentFileName,
                pair.ParentSize);

            var selectionSet = FinderRanking.SelectionSet(
                pair.NameMatchedCandidates.Select(c => new CandidateSeed(c.Name, c.Size, c.Width, c.Height, c.Path, c.Modified)).ToList(),
                pair.ParentFileName);

            var exact = selectionSet.Where(c => pair.ParentSize.HasValue && c.Size == pair.ParentSize).ToList();

            var checkParentOpens = state.Parent.State == FileState.Ok;
            var checkChildOpens = state.Child.State == FileState.Ok;
            var checkCandidatesFound = candidates.Count > 0;
            var checkOriginalConfirmed = exact.Count > 0;
            var tiebreakApplied = exact.Count > 1;

            var verdict = FinderVerdictRules.Decide(
                checkParentOpens, checkChildOpens, checkCandidatesFound, checkOriginalConfirmed);

            rows.Add(new FinderRow
            {
                ParentFileName = pair.ParentFileName,
                ParentFolder = pair.ParentFolder,
                ParentId = pair.ParentId,
                ChildId = pair.ChildId,
                ChildCategory = pair.ChildCategory,
                ChildVariant = pair.ChildVariant,
                Parent = state.Parent,
                Child = state.Child,
                Verdict = verdict,
                VerdictNote = FinderVerdictRules.Note(
                    verdict, checkOriginalConfirmed, state.Parent.Fault, state.Child.Fault, tiebreakApplied),
                CheckParentOpens = checkParentOpens,
                CheckChildOpens = checkChildOpens,
                CheckCandidatesFound = checkCandidatesFound,
                CheckOriginalConfirmed = checkOriginalConfirmed,
                CheckTiebreakApplied = tiebreakApplied ? FinderCheck.Yes : null,
                Candidates = candidates
            });
        }

        var summary = new FinderScanSummary(
            rows.Count,
            rows.GroupBy(r => r.Verdict).ToDictionary(g => g.Key, g => g.Count()));

        scanState.Publish(rows, summary);

        progress?.Report(new FinderProgressInfo("Done", rows.Count, rows.Count, $"{rows.Count} pair(s)"));
        return rows;
    }

    public async Task<IReadOnlyList<FinderCandidate>> GetDropBoxCandidatesAsync(
        string parentFileName,
        long? parentSize = null,
        CancellationToken ct = default)
    {
        var seed = new PairSeed
        {
            ParentId = Guid.Empty,
            ParentFileName = parentFileName,
            ParentFolder = string.Empty,
            ParentSize = parentSize,
            ChildId = Guid.Empty,
            ChildFolder = string.Empty,
            NameMatchedCandidates = []
        };

        // The name matches come from one indexed seek on the name, and the widening adds the
        // similar ones when the name matched nothing (§9.3).
        await LoadNameMatchesAsync(seed, ct);

        if (seed.NameMatchedCandidates.Count == 0)
        {
            await WidenCandidatesAsync([seed], ct);
        }

        return FinderRanking.Rank(
            seed.NameMatchedCandidates
                .Select(c => new CandidateSeed(c.Name, c.Size, c.Width, c.Height, c.Path, c.Modified))
                .ToList(),
            parentFileName,
            parentSize);
    }

    public async Task<IReadOnlyList<FinderRow>> GetLiemListAsync(
        IFinderProgress? progress = null,
        CancellationToken ct = default)
    {
        // Liem's List is a report on the SAME scan (§11), so it runs the pass only when
        // there is no scan to report on.
        var rows = await GetMediaPairsAsync(progress, force: false, ct);

        return rows.Where(r => FinderVerdictRules.LeavesUnresolved(r.Verdict)).ToList();
    }

    /// <summary>
    /// The one set-based query: the pairs, and the candidates whose name matches the parent's
    /// file name.
    /// </summary>
    /// <remarks>
    /// The join runs over the whole index, because <c>ml.DropBoxNode.Name</c> has an index and
    /// the query is a join - a per-parent lookup would scan the index once per pair.
    /// </remarks>
    private async Task<List<PairSeed>> LoadPairsAsync(CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);

        const string sql = """
            SELECT p.Id                AS ParentId,
                   p.FileName          AS ParentFileName,
                   p.Path              AS ParentFolder,
                   p.Size              AS ParentSize,
                   p.WidthInPixels     AS ParentWidth,
                   p.HeightInPixels    AS ParentHeight,
                   c.Id                AS ChildId,
                   c.Path              AS ChildFolder,
                   c.Size              AS ChildSize,
                   c.WidthInPixels     AS ChildWidth,
                   c.HeightInPixels    AS ChildHeight,
                   c.MediaCategoryId   AS ChildCategory,
                   c.MediaSubCategoryId AS ChildVariant
            FROM ml.media p
            JOIN ml.media c ON c.OriginalMediaId = p.Id
            WHERE p.OriginalMediaId IS NULL
              AND p.ExternalKey IS NOT NULL
              AND c.ExternalKey IS NOT NULL
              AND p.MediaTypeId IN (1, 2, 15)
              AND c.MediaTypeId IN (1, 2, 15)
            ORDER BY p.Id
            """;

        var pairs = new List<PairSeed>();

        await using (var command = new SqlCommand(sql, connection))
        {
            command.CommandTimeout = options.Value.CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                pairs.Add(new PairSeed
                {
                    ParentId = reader.GetGuid(0),
                    ParentFileName = reader.GetString(1),
                    ParentFolder = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                    ParentSize = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                    ParentWidth = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    ParentHeight = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                    ChildId = reader.GetGuid(6),
                    ChildFolder = reader.IsDBNull(7) ? string.Empty : reader.GetString(7),
                    ChildSize = reader.IsDBNull(8) ? null : reader.GetInt64(8),
                    ChildWidth = reader.IsDBNull(9) ? null : reader.GetInt32(9),
                    ChildHeight = reader.IsDBNull(10) ? null : reader.GetInt32(10),
                    ChildCategory = reader.IsDBNull(11) ? (short?)null : reader.GetInt16(11),
                    ChildVariant = reader.IsDBNull(12) ? (short?)null : reader.GetInt16(12),
                    NameMatchedCandidates = []
                });
            }
        }

        // The candidates that matched by name, for the same parents - one more set-based query,
        // because a per-parent lookup would scan the index once per pair.
        await LoadNameMatchedCandidatesAsync(pairs, ct);

        return pairs;
    }

    /// <summary>
    /// The candidates whose name matches the parent's file name, for a set of pairs.
    /// </summary>
    private async Task LoadNameMatchedCandidatesAsync(
        IReadOnlyList<PairSeed> pairs,
        CancellationToken ct)
    {
        if (pairs.Count == 0)
        {
            return;
        }

        await using var connection = await OpenConnectionAsync(ct);

        const string sql = """
            SELECT d.Name, d.SizeBytes, d.WidthPixels, d.HeightPixels, d.PathDisplay, d.ServerModified, p.Id
            FROM ml.DropBoxNode d
            JOIN ml.media p ON p.FileName = d.Name
            WHERE d.IsFolder = 0
              AND p.OriginalMediaId IS NULL
              AND p.ExternalKey IS NOT NULL
              AND p.MediaTypeId IN (1, 2, 15)
            """;

        var byParent = pairs.ToDictionary(p => p.ParentId);

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var parentId = reader.GetGuid(6);
            if (!byParent.TryGetValue(parentId, out var pair))
            {
                continue;
            }

            pair.NameMatchedCandidates.Add(new CandidateRow
            {
                Name = reader.GetString(0),
                Size = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Width = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Height = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Path = reader.GetString(4),
                Modified = reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5)
            });
        }
    }

    /// <summary>
    /// The candidates whose name matches one parent's file name.
    /// </summary>
    /// <remarks>
    /// One indexed seek on the name - <c>IX_DropBoxNode_Name</c> - rather than the pair
    /// query's join, because there is no pair set to join against.
    /// </remarks>
    private async Task LoadNameMatchesAsync(PairSeed pair, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);

        const string sql = """
            SELECT Name, SizeBytes, WidthPixels, HeightPixels, PathDisplay, ServerModified
            FROM ml.DropBoxNode
            WHERE IsFolder = 0
              AND Name = @name
            """;

        await using var command = new SqlCommand(sql, connection);
        command.CommandTimeout = options.Value.CommandTimeoutSeconds;
        command.Parameters.AddWithValue("@name", pair.ParentFileName);

        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            pair.NameMatchedCandidates.Add(new CandidateRow
            {
                Name = reader.GetString(0),
                Size = reader.IsDBNull(1) ? null : reader.GetInt64(1),
                Width = reader.IsDBNull(2) ? null : reader.GetInt32(2),
                Height = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                Path = reader.GetString(4),
                Modified = reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5)
            });
        }
    }

    /// <summary>
    /// The candidates whose name is SIMILAR to the parent's file name - the widening, for the
    /// pairs whose name matched nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One set-based join for every pair that needs widening. The pairs' names go into a
    /// temp table and are joined to the index on the same key, so the join - not a query per
    /// pair - is what narrows the index.
    /// </para>
    /// <para>
    /// The join is deliberately loose: it brings back everything that could be similar, and
    /// §10's fuzzy rules decide what actually is. A per-pair query would scan the index once
    /// per pair, which for tens of thousands of pairs is tens of thousands of scans.
    /// </para>
    /// </remarks>
    private async Task WidenCandidatesAsync(List<PairSeed> pairs, CancellationToken ct)
    {
        await using var connection = await OpenConnectionAsync(ct);

        await using (var create = new SqlCommand(
            """
            CREATE TABLE #wanted (
                ParentId UNIQUEIDENTIFIER NOT NULL PRIMARY KEY,
                Prefix    NVARCHAR(64) NOT NULL
            );
            """, connection))
        {
            await create.ExecuteNonQueryAsync(ct);
        }

        // The pairs are inserted in batches, so a run with tens of thousands of pairs does
        // not send tens of thousands of round trips.
        const int BatchSize = 500;

        for (var offset = 0; offset < pairs.Count; offset += BatchSize)
        {
            ct.ThrowIfCancellationRequested();

            var batch = pairs.Skip(offset).Take(BatchSize).ToList();

            // Each row gets its own parameter name: a batch reuses @id and @prefix, and
            // SQL Server rejects the same name twice in one statement.
            var values = new List<string>(batch.Count);
            var parameters = new List<SqlParameter>(batch.Count * 2);

            for (var i = 0; i < batch.Count; i++)
            {
                values.Add($"(@id{i}, @prefix{i})");

                parameters.Add(new SqlParameter($"@id{i}", batch[i].ParentId));
                parameters.Add(new SqlParameter($"@prefix{i}", Prefix(batch[i].ParentFileName)));
            }

            await using var insert = new SqlCommand(
                $"INSERT INTO #wanted (ParentId, Prefix) VALUES {string.Join(", ", values)}", connection);

            foreach (var parameter in parameters)
            {
                insert.Parameters.Add(parameter);
            }

            await insert.ExecuteNonQueryAsync(ct);
        }

        // The join: a candidate is a candidate for a pair when the two names reduce to the
        // same key - the extension dropped, upper-cased, first eight characters.
        // ml.DropBoxNode.NameKey is that key, computed by the database, and the pair's
        // key is computed the same way in C# (see Prefix).
        //
        // The join is loose. §10's fuzzy rules, applied in memory below, decide what is
        // actually similar.
        const string joinSql = """
            SELECT w.ParentId, d.Name, d.SizeBytes, d.WidthPixels, d.HeightPixels, d.PathDisplay, d.ServerModified
            FROM #wanted w
            JOIN ml.DropBoxNode d
              ON d.IsFolder = 0
             AND d.NameKey = w.Prefix
            """;

        var byParent = pairs.ToDictionary(p => p.ParentId);

        await using (var command = new SqlCommand(joinSql, connection))
        {
            command.CommandTimeout = options.Value.CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var parentId = reader.GetGuid(0);
                if (!byParent.TryGetValue(parentId, out var pair))
                {
                    continue;
                }

                var name = reader.GetString(1);

                // The join is loose; §10's fuzzy rules decide what is similar enough to be
                // shown. A name that is not similar is not a candidate.
                if (!FinderSimilarity.IsSimilar(pair.ParentFileName, name))
                {
                    continue;
                }

                pair.NameMatchedCandidates.Add(new CandidateRow
                {
                    Name = name,
                    Size = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    Width = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    Height = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Path = reader.GetString(5),
                    Modified = reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6)
                });
            }
        }

        await using (var drop = new SqlCommand("DROP TABLE #wanted", connection))
        {
            await drop.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    /// The join key: the name up to its last dot, upper-cased, first eight characters.
    /// </summary>
    /// <remarks>
    /// This must produce exactly what <c>ml.DropBoxNode.NameKey</c> produces — the same
    /// computed column, with the same rule. The two agreeing is the only reason the join
    /// finds anything, so the rule is stated in both places and must be changed in both.
    /// </remarks>
    private static string Prefix(string name)
    {
        // The LAST dot, and a name that STARTS with a dot has an empty stem - the
        // same rule the computed column uses.
        var dot = name.LastIndexOf('.');
        var stem = dot >= 0 ? name[..dot] : name;

        var upper = stem.ToUpperInvariant();
        return upper.Length <= 8 ? upper : upper[..8];
    }

    private async Task<SqlConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqlConnection(new SqlConnectionStringBuilder(options.Value.ConnectionString)
        {
            ConnectTimeout = options.Value.ConnectTimeoutSeconds
        }.ConnectionString);

        await connection.OpenAsync(ct);
        return connection;
    }

    private sealed record FileStates(FinderFileState Parent, FinderFileState Child);

    private sealed class PairSeed
    {
        public required Guid ParentId { get; init; }

        public required string ParentFileName { get; init; }

        public required string ParentFolder { get; init; }

        public long? ParentSize { get; init; }

        public int? ParentWidth { get; init; }

        public int? ParentHeight { get; init; }

        public required Guid ChildId { get; init; }

        public required string ChildFolder { get; init; }

        public long? ChildSize { get; init; }

        public int? ChildWidth { get; init; }

        public int? ChildHeight { get; init; }

        public short? ChildCategory { get; init; }

        public short? ChildVariant { get; init; }

        public required List<CandidateRow> NameMatchedCandidates { get; init; }
    }

    private sealed class CandidateRow
    {
        public required string Name { get; init; }

        public long? Size { get; init; }

        public int? Width { get; init; }

        public int? Height { get; init; }

        public required string Path { get; init; }

        public DateTimeOffset? Modified { get; init; }
    }
}
