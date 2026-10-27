using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Utility.Server.Finder;
using Utility.Server.ML.Services;
using Utility.Server.Options;
using Utility.Server.Services;

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
    IParallelism parallelism,
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
            progress?.Report(new FinderProgressInfo("Reading Pairs", 1, 1, $"{scanState.Rows.Count} pair(s), from the last pass"));
            return scanState.Rows;
        }

        var rows = await RunPassAsync(parentIds: null, progress, ct);

        var summary = new FinderScanSummary(
            rows.Count,
            rows.GroupBy(r => r.Verdict).ToDictionary(g => g.Key, g => g.Count()),
            DateTimeOffset.Now);

        scanState.Publish(rows, summary);

        progress?.Report(new FinderProgressInfo("Done", rows.Count, rows.Count, $"{rows.Count} pair(s)"));
        return rows;
    }

    /// <summary>
    /// The pass itself: the four phases, and the rows they produce.
    /// </summary>
    /// <remarks>
    /// The ids narrow it to the pairs they name - null is the whole library. The rows are
    /// NOT published here: the caller decides whether they go into the scan state.
    /// </remarks>
    private async Task<IReadOnlyList<FinderRow>> RunPassAsync(
        IReadOnlyList<Guid>? parentIds,
        IFinderProgress? progress,
        CancellationToken ct)
    {
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
        // Total 0: the row count is not known until the query returns, so this stage's bar
        // is indeterminate (§18.8).
        //
        // The message names what is being read, not the tables it is read from: the reader
        // of this screen is a person, and ml.media and ml.DropBoxNode are not their
        // vocabulary.
        progress?.Report(new FinderProgressInfo("Reading Pairs", 0, 0, "Querying the media library and the DropBox Index"));

        var pairs = await LoadPairsAsync(parentIds, ct);
        progress?.Report(new FinderProgressInfo("Reading Pairs", 1, 1, $"{pairs.Count} pair(s)"));

        // ---- Phase 2: visit every file on disk ----
        //
        // The pairs are visited by a pool of workers, one pair each. The pool's size is the
        // machine's logical processor count (§19.2) - the same number for the existence
        // checks and for the decodes.
        var states = new FileStates[pairs.Count];
        var done = 0;
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism.LogicalProcessorCount,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(
            Enumerable.Range(0, pairs.Count),
            options,
            async (index, token) =>
            {
                var pair = pairs[index];

                states[index] = new FileStates(
                    await library.GetFileStateAsync(libraryRoot, pair.ParentFolder, pair.ParentId,
                        pair.ParentSize, pair.ParentWidth, pair.ParentHeight, token),
                    await library.GetFileStateAsync(libraryRoot, pair.ChildFolder, pair.ChildId,
                        pair.ChildSize, pair.ChildWidth, pair.ChildHeight, token));

                // Each index is written by exactly one worker, so the result is the same
                // whatever order the workers finish in.
                var n = Interlocked.Increment(ref done);
                if (n % 500 == 0 || n == pairs.Count)
                {
                    progress?.Report(new FinderProgressInfo("Checking Files", n, pairs.Count, $"{n} of {pairs.Count}"));
                }
            });

        // ---- Phase 3: find the candidates of the pairs that need them ----
        var toWiden = pairs
            .Where((p, i) => !states[i].Parent.IsOk && p.NameMatchedCandidates.Count == 0)
            .ToList();

        if (toWiden.Count > 0)
        {
            progress?.Report(new FinderProgressInfo("Finding Candidates", 0, toWiden.Count, $"{toWiden.Count} pair(s)"));

            // The pairs are split into chunks and the chunks are looked for at the same time.
            await FindCandidatesAsync(toWiden, progress, ct);

            progress?.Report(new FinderProgressInfo("Finding Candidates", toWiden.Count, toWiden.Count, $"{toWiden.Count} pair(s)"));
        }

        // ---- Phase 4: the checks, the verdict and the row ----
        var rows = new List<FinderRow>(pairs.Count);
        progress?.Report(new FinderProgressInfo("Building Rows", 0, pairs.Count, $"{pairs.Count} row(s)"));

        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var state = states[i];

            var candidates = FinderRanking.Rank(
                pair.NameMatchedCandidates.Select(c => new CandidateSeed(c.Name, c.Size, c.Width, c.Height, c.Path, c.Modified, c.NamespaceId, c.NodeId, c.DropboxId, c.ContentHash)).ToList(),
                pair.ParentFileName,
                pair.ParentSize,
                pair.ParentFolder);

            var selectionSet = FinderRanking.SelectionSet(
                pair.NameMatchedCandidates.Select(c => new CandidateSeed(c.Name, c.Size, c.Width, c.Height, c.Path, c.Modified, c.NamespaceId, c.NodeId, c.DropboxId, c.ContentHash)).ToList(),
                pair.ParentFileName);

            var exact = selectionSet
                .Where(c => pair.ParentSize.HasValue && c.Size == pair.ParentSize)
                .Where(c => FinderRanking.FolderMatches(pair.ParentFolder, c.Path))
                .ToList();

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
                ChildCategoryName = pair.ChildCategoryName,
                ChildVariantName = pair.ChildVariantName,
                ParentDateAdded = pair.ParentDateAdded,
                Parent = state.Parent,
                Child = state.Child,
                ChildWidth = pair.ChildWidth,
                ChildHeight = pair.ChildHeight,
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

            if ((i + 1) % 5000 == 0 || i + 1 == pairs.Count)
            {
                progress?.Report(new FinderProgressInfo("Building Rows", i + 1, pairs.Count, $"{i + 1} of {pairs.Count}"));
            }
        }

        return rows;
    }

    /// <summary>
    /// Re-reads the pairs it is given: their files are visited again and their verdicts are
    /// decided again (§10.9).
    /// </summary>
    /// <remarks>
    /// The pass, narrowed to the pairs it names. The rows are NOT published to the scan
    /// state: the state holds the whole library, and publishing a narrowed set into it would
    /// make a later read of the state return only that set.
    /// </remarks>
    public async Task<IReadOnlyList<FinderRow>> RereadMediaPairsAsync(
        IReadOnlyList<Guid>? parentIds,
        IFinderProgress? progress = null,
        CancellationToken ct = default)
    {
        var rows = await RunPassAsync(parentIds, progress, ct);

        progress?.Report(new FinderProgressInfo("Done", rows.Count, rows.Count, $"{rows.Count} pair(s)"));
        return rows;
    }

    public async Task<IReadOnlyList<FinderCandidate>> GetDropBoxCandidatesAsync(
        string parentFileName,
        long? parentSize = null,
        string? parentFolder = null,
        CancellationToken ct = default)
    {
        var seed = new PairSeed
        {
            ParentId = Guid.Empty,
            ParentFileName = parentFileName,
            ParentFolder = parentFolder ?? string.Empty,
            ParentSize = parentSize,
            ChildId = Guid.Empty,
            ChildFolder = string.Empty,
            ChildCategoryName = string.Empty,
            ChildVariantName = string.Empty,
            NameMatchedCandidates = []
        };

        // The name matches come from one indexed seek on the name, and the widening adds the
        // similar ones when the name matched nothing (§9.3).
        await LoadNameMatchesAsync(seed, ct);

        if (seed.NameMatchedCandidates.Count == 0)
        {
            await FindCandidatesAsync([seed], null, ct);
        }

        return FinderRanking.Rank(
            seed.NameMatchedCandidates
                .Select(c => new CandidateSeed(c.Name, c.Size, c.Width, c.Height, c.Path, c.Modified, c.NamespaceId, c.NodeId, c.DropboxId, c.ContentHash))
                .ToList(),
            parentFileName,
            parentSize,
            seed.ParentFolder);
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
    private async Task<List<PairSeed>> LoadPairsAsync(
        IReadOnlyList<Guid>? parentIds,
        CancellationToken ct)
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
                   c.MediaSubCategoryId AS ChildVariant,
                   mc.Name             AS ChildCategoryName,
                   msc.Name            AS ChildVariantName,
                   p.DateAdded         AS ParentDateAdded
            FROM ml.media p
            JOIN ml.media c ON c.OriginalMediaId = p.Id
            LEFT JOIN ml.MediaCategory mc ON mc.Id = c.MediaCategoryId
            LEFT JOIN ml.MediaSubCategory msc ON msc.Id = c.MediaSubCategoryId
            WHERE p.OriginalMediaId IS NULL
              AND p.ExternalKey IS NOT NULL
              AND c.ExternalKey IS NOT NULL
              AND p.MediaTypeId IN (1, 2, 15)
              AND c.MediaTypeId IN (1, 2, 15)
            ORDER BY p.Id
            """;

        var pairs = new List<PairSeed>();

        // The narrowed pass reads only the pairs it was given (§10.9). The ids are checked
        // here, as the rows are read: the query itself is not narrowed, because a list of
        // thousands of ids is not a query the server can plan and the whole set is read
        // anyway.
        var wanted = parentIds is null ? null : new HashSet<Guid>(parentIds);

        await using (var command = new SqlCommand(sql, connection))
        {
            command.CommandTimeout = options.Value.CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var parentId = reader.GetGuid(0);

                if (wanted is not null && !wanted.Contains(parentId))
                {
                    continue;
                }

                pairs.Add(new PairSeed
                {
                    ParentId = parentId,
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
                    ChildCategoryName = reader.IsDBNull(13) ? string.Empty : reader.GetString(13),
                    ChildVariantName = reader.IsDBNull(14) ? string.Empty : reader.GetString(14),
                    // ml.media.DateAdded is datetime2, which arrives as a DateTime and
                    // carries no offset of its own. It is read as UTC and given that
                    // offset, so the row's date is a DateTimeOffset like the rest of the
                    // app's dates.
                    ParentDateAdded = reader.IsDBNull(15)
                        ? null
                        : new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(15), DateTimeKind.Utc)),
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
            SELECT d.Name, d.SizeBytes, d.WidthPixels, d.HeightPixels, d.PathDisplay, d.ServerModified,
                   d.Id, d.NamespaceId, d.DropboxId, d.ContentHash, p.Id
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
            var parentId = reader.GetGuid(10);
            if (!byParent.TryGetValue(parentId, out var pair))
            {
                continue;
            }

            // A candidate has to be in the library's folder to be a candidate at all
            // (§9.1 rule 1): the library's folder IS the Dropbox product folder, so a
            // candidate somewhere else is a different file that happens to share the name.
            var path = reader.GetString(4);
            if (!FinderRanking.FolderMatches(pair.ParentFolder, path))
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
                Modified = reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5),
                NodeId = reader.GetInt64(6),
                NamespaceId = reader.GetString(7),
                DropboxId = reader.IsDBNull(8) ? null : reader.GetString(8),
                ContentHash = reader.IsDBNull(9) ? null : reader.GetString(9)
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
            SELECT Name, SizeBytes, WidthPixels, HeightPixels, PathDisplay, ServerModified,
                   Id, NamespaceId, DropboxId, ContentHash
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
                Modified = reader.IsDBNull(5) ? null : reader.GetDateTimeOffset(5),
                NodeId = reader.GetInt64(6),
                NamespaceId = reader.GetString(7),
                DropboxId = reader.IsDBNull(8) ? null : reader.GetString(8),
                ContentHash = reader.IsDBNull(9) ? null : reader.GetString(9)
            });
        }
    }

    /// <summary>
    /// The candidates whose name is SIMILAR to the parent's file name - the widening, for the
    /// pairs whose name matched nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pairs are split into chunks, and the chunks are looked for AT THE SAME TIME. Each
    /// chunk has its own connection and its own temp table, so the round trips of one chunk
    /// wait behind the round trips of another instead of behind each other.
    /// </para>
    /// <para>
    /// The join is still set-based: a chunk's names go into a temp table and are joined to
    /// the index on the same key, so the join - not a query per pair - is what narrows the
    /// index. A per-pair query would scan the index once per pair, which for tens of
    /// thousands of pairs is tens of thousands of scans.
    /// </para>
    /// <para>
    /// The join is deliberately loose: it brings back everything that could be similar, and
    /// §10's fuzzy rules decide what actually is.
    /// </para>
    /// </remarks>
    private async Task FindCandidatesAsync(
        List<PairSeed> pairs,
        IFinderProgress? progress,
        CancellationToken ct)
    {
        // A chunk is small enough that the chunks finish at staggered times - so the bar
        // moves all the way through the stage - and big enough that the join still has
        // something to do.
        const int ChunkSize = 6_250;

        var chunks = new List<List<PairSeed>>(pairs.Count / ChunkSize + 1);
        for (var i = 0; i < pairs.Count; i += ChunkSize)
        {
            chunks.Add(pairs.GetRange(i, Math.Min(ChunkSize, pairs.Count - i)));
        }

        // One counter for the whole stage: the pairs whose candidates are known.
        var stage = new StageProgress(progress, pairs.Count);

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism.LogicalProcessorCount,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(chunks, options, async (chunk, token) =>
        {
            await FindCandidatesForChunkAsync(chunk, stage, token);
        });
    }

    /// <summary>
    /// Finds the candidates of one chunk of pairs, on its own connection.
    /// </summary>
    private async Task FindCandidatesForChunkAsync(
        List<PairSeed> chunk,
        StageProgress stage,
        CancellationToken ct)
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

        // The chunk's pairs are inserted in batches, so a chunk does not send one round trip
        // per pair.
        const int BatchSize = 500;

        for (var offset = 0; offset < chunk.Count; offset += BatchSize)
        {
            var batch = chunk.GetRange(offset, Math.Min(BatchSize, chunk.Count - offset));

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
            SELECT w.ParentId, d.Name, d.SizeBytes, d.WidthPixels, d.HeightPixels, d.PathDisplay, d.ServerModified,
                   d.Id, d.NamespaceId, d.DropboxId, d.ContentHash
            FROM #wanted w
            JOIN ml.DropBoxNode d
              ON d.IsFolder = 0
             AND d.NameKey = w.Prefix
            """;

        var byParent = chunk.ToDictionary(p => p.ParentId);

        // The parents this chunk has answered for. A parent whose name matched nothing in
        // the index is never in here - it is counted when the chunk is finished.
        var answered = new HashSet<Guid>();

        await using (var command = new SqlCommand(joinSql, connection))
        {
            command.CommandTimeout = options.Value.CommandTimeoutSeconds;

            await using var reader = await command.ExecuteReaderAsync(ct);

            // The rows are judged in batches and the batch is dropped once it is judged, so
            // the memory does not grow with the size of the join.
            var batch = new List<JoinedCandidate>(25_000);

            while (await reader.ReadAsync(ct))
            {
                batch.Add(new JoinedCandidate
                {
                    ParentId = reader.GetGuid(0),
                    Name = reader.GetString(1),
                    Size = reader.IsDBNull(2) ? null : reader.GetInt64(2),
                    Width = reader.IsDBNull(3) ? null : reader.GetInt32(3),
                    Height = reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    Path = reader.GetString(5),
                    Modified = reader.IsDBNull(6) ? null : reader.GetDateTimeOffset(6),
                    NodeId = reader.GetInt64(7),
                    NamespaceId = reader.GetString(8),
                    DropboxId = reader.IsDBNull(9) ? null : reader.GetString(9),
                    ContentHash = reader.IsDBNull(10) ? null : reader.GetString(10)
                });

                if (batch.Count >= 25_000)
                {
                    stage.Add(CountAnswered(batch, answered));
                    KeepSimilar(batch, byParent);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                stage.Add(CountAnswered(batch, answered));
                KeepSimilar(batch, byParent);
            }
        }

        // The parents whose name matched nothing in the index are answered too - they have
        // no candidates, and that IS their answer.
        stage.Add(chunk.Count - answered.Count);
    }

    /// <summary>
    /// Counts the parents in a batch that have not been counted before.
    /// </summary>
    private static int CountAnswered(List<JoinedCandidate> batch, HashSet<Guid> answered)
    {
        var count = 0;

        foreach (var row in batch)
        {
            if (answered.Add(row.ParentId))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Keeps the rows whose name is similar to the parent's file name, and adds them to the
    /// pair's candidates.
    /// </summary>
    /// <remarks>
    /// The join is loose; §10's fuzzy rules decide what is similar enough to be shown.
    /// </remarks>
    private static void KeepSimilar(List<JoinedCandidate> batch, Dictionary<Guid, PairSeed> byParent)
    {
        foreach (var row in batch)
        {
            if (!byParent.TryGetValue(row.ParentId, out var pair))
            {
                continue;
            }

            if (!FinderSimilarity.IsSimilar(pair.ParentFileName, row.Name))
            {
                continue;
            }

            // The same rule as the name matches (§9.1 rule 1): a candidate has to be in the
            // library's folder to be a candidate at all. Without it, the widening's loose
            // pool hands over the neighbours of the product code - a different product that
            // happens to be one character away - and those are not restore sources.
            if (!FinderRanking.FolderMatches(pair.ParentFolder, row.Path))
            {
                continue;
            }

            pair.NameMatchedCandidates.Add(new CandidateRow
            {
                Name = row.Name,
                Size = row.Size,
                Width = row.Width,
                Height = row.Height,
                Path = row.Path,
                Modified = row.Modified,
                NodeId = row.NodeId,
                NamespaceId = row.NamespaceId,
                DropboxId = row.DropboxId,
                ContentHash = row.ContentHash
            });
        }
    }

    /// <summary>
    /// One row of the widening join, before §10's rules have judged it.
    /// </summary>
    private sealed class JoinedCandidate
    {
        public Guid ParentId { get; init; }
        public required string Name { get; init; }
        public long? Size { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }
        public required string Path { get; init; }
        public DateTimeOffset? Modified { get; init; }
        public long NodeId { get; init; }
        public string NamespaceId { get; init; } = string.Empty;
        public string? DropboxId { get; init; }
        public string? ContentHash { get; init; }
    }

    /// <summary>
    /// The progress of the *Finding Candidates* stage.
    /// </summary>
    /// <remarks>
    /// One counter, and it only ever goes UP: the pairs whose candidates are known, out of
    /// the pairs that need them. The chunks are looked for at the same time, so several
    /// threads report at once — a report carrying a smaller number than the bar already
    /// shows is dropped, so the bar never jumps back.
    /// </remarks>
    private sealed class StageProgress
    {
        private readonly IFinderProgress? _progress;
        private readonly int _total;
        private int _done;
        private int _reported;

        public StageProgress(IFinderProgress? progress, int total)
        {
            _progress = progress;
            _total = total;
        }

        /// <summary>Counts more pairs whose candidates are known.</summary>
        public void Add(int count)
        {
            if (count <= 0)
            {
                return;
            }

            var done = Interlocked.Add(ref _done, count);

            var last = Volatile.Read(ref _reported);
            if (done <= last)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _reported, done, last) != last)
            {
                return;
            }

            _progress?.Report(new FinderProgressInfo("Finding Candidates", done, _total, $"{done:N0} of {_total:N0} pair(s)"));
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

        /// <summary>The category's name, from ml.MediaCategory. Empty when the id has none.</summary>
        public required string ChildCategoryName { get; init; }

        /// <summary>The variant's name, from ml.MediaSubCategory. Empty when the id has none.</summary>
        public required string ChildVariantName { get; init; }

        /// <summary>When the parent row was added to the media library.</summary>
        public DateTimeOffset? ParentDateAdded { get; init; }

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

        /// <summary>The namespace the file is indexed under.</summary>
        public string NamespaceId { get; init; } = string.Empty;

        /// <summary>The index row's own id.</summary>
        public long NodeId { get; init; }

        /// <summary>Dropbox's own object id for the file.</summary>
        public string? DropboxId { get; init; }

        /// <summary>Dropbox's own hash of the file's bytes.</summary>
        public string? ContentHash { get; init; }
    }
}
