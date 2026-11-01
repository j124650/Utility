using System.Text;
using System.Text.Json;

namespace Utility.Server.Finder.Repair;

/// <summary>
/// One operation, as the log records it — read back out of the file.
/// </summary>
public sealed record RecoveryLogOperation
{
    /// <summary>The operation's number within the run — what ties its lines together.</summary>
    public int Number { get; init; }

    public string ParentId { get; init; } = string.Empty;

    public string ChildId { get; init; } = string.Empty;

    public string ParentName { get; init; } = string.Empty;

    public string Folder { get; init; } = string.Empty;

    /// <summary>What the run set out to do — "restore the parent", and so on.</summary>
    public string Plan { get; init; } = string.Empty;

    /// <summary>The Dropbox path the bytes came from.</summary>
    public string? Source { get; init; }

    /// <summary>Where the parent's bytes were written.</summary>
    public string? CopiedTo { get; init; }

    public long? CopiedBytes { get; init; }

    /// <summary>Where the regenerated child was written.</summary>
    public string? ResizedTo { get; init; }

    /// <summary>What the file became — its length and its dimensions.</summary>
    public string? Result { get; init; }

    /// <summary>How it turned out — "recovered" or "not recovered".</summary>
    public string Outcome { get; init; } = string.Empty;

    /// <summary>Why it was not recovered, when it was not.</summary>
    public string? Reason { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset? EndedAt { get; init; }

    /// <summary>
    /// True when the operation began and never ended — the run was interrupted, or died,
    /// while this pair was in flight.
    /// </summary>
    public bool IsInterrupted { get; init; }

    /// <summary>
    /// True when the operation's lines do not fit — an end with no begin, or two ends for
    /// one operation.
    /// </summary>
    public bool IsMalformed { get; init; }

    /// <summary>How long the operation took, or null when it never ended.</summary>
    public TimeSpan? Duration => EndedAt is { } end ? end - StartedAt : null;

    /// <summary>
    /// The reason in high-signal words — "Path not found in Dropbox" rather than
    /// "path/not_found/". The raw reason stays in <see cref="Reason"/>.
    /// </summary>
    public string ShortReason => RecoveryReasonPhrase.Shorten(Reason);

    /// <summary>True when the operation did not recover its pair.</summary>
    public bool Failed =>
        IsInterrupted || IsMalformed
        || Outcome.StartsWith("not recovered", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// A recovery log, read back: its figures, and its operations.
/// </summary>
public sealed record RecoveryLogRead
{
    /// <summary>The format's version, as the header named it.</summary>
    public int Format { get; init; }

    public DateTimeOffset? Started { get; init; }

    public int Shown { get; init; }

    public int Recoverable { get; init; }

    public int Folders { get; init; }

    /// <summary>How many the run recovered, as its summary said.</summary>
    public int Recovered { get; init; }

    /// <summary>How many failed, as its summary said.</summary>
    public int Failed { get; init; }

    /// <summary>How many pairs were not recovered at all, as its summary said.</summary>
    public int NotRecovered { get; init; }

    public long DownloadedBytes { get; init; }

    public int Replaced { get; init; }

    /// <summary>Every operation in the log, in the log's order.</summary>
    public IReadOnlyList<RecoveryLogOperation> Operations { get; init; } = [];

    /// <summary>How many lines were not valid JSON.</summary>
    public int MalformedLines { get; init; }
}

/// <summary>
/// Reads a recovery log back — the JSON Lines format the recovery writes (§10.5.3).
/// </summary>
/// <remarks>
/// <para>
/// The reader PARSES the log, so it is the first thing that can see a broken one: an
/// operation that began and never ended, a line that is not valid JSON, and an operation
/// whose lines do not fit. None of them is dropped silently.
/// </para>
/// <para>
/// The log's TEXT is never held in memory. It is read a line at a time, and each line is
/// parsed and dropped — what is kept is the operations, which is what the reader sees.
/// </para>
/// </remarks>
public static class RecoveryLogReader
{
    /// <summary>
    /// Whether the file is in the JSON Lines format — the first line that says anything
    /// begins with a brace.
    /// </summary>
    /// <remarks>
    /// The logs written before the format changed are <c>key=value</c> pairs on a line,
    /// and the browser shows those as their own text rather than growing a second reader
    /// for them.
    /// </remarks>
    public static bool IsJsonLines(string path)
    {
        try
        {
            using var reader = new StreamReader(path, Encoding.UTF8);

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim().Length > 0)
                {
                    return line.TrimStart().StartsWith('{');
                }
            }
        }
        catch (IOException)
        {
            return false;
        }

        return false;
    }

    /// <summary>
    /// Reads a recovery log back: its figures, and every operation in it.
    /// </summary>
    public static RecoveryLogRead Read(string path)
    {
        var format = 0;
        DateTimeOffset? started = null;
        var shown = 0;
        var recoverable = 0;
        var folders = 0;
        var recovered = 0;
        var failed = 0;
        var notRecovered = 0;
        long downloaded = 0;
        var replaced = 0;
        var malformedLines = 0;

        // The operations, by their number, and the order they arrived in.
        var byNumber = new Dictionary<int, RecoveryLogOperation>();
        var order = new List<int>();

        // The state of each operation while it is being built.
        var ends = new Dictionary<int, int>();

        using (var reader = new StreamReader(path, Encoding.UTF8))
        {
            string? line;

            while ((line = reader.ReadLine()) is not null)
            {
                if (line.Trim().Length == 0)
                {
                    continue;
                }

                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    // A line that is not JSON is COUNTED, never skipped silently: a log
                    // that cannot be trusted is worse than a log that says it is damaged.
                    malformedLines++;
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;

                    if (!root.TryGetProperty("type", out var typeElement))
                    {
                        malformedLines++;
                        continue;
                    }

                    var type = typeElement.GetString() ?? string.Empty;

                    switch (type)
                    {
                        case "header":
                            format = Int(root, "format");
                            started = Stamp(root, "started");
                            shown = Int(root, "shown");
                            recoverable = Int(root, "recoverable");
                            folders = Int(root, "folders");
                            break;

                        case "summary":
                            recovered = Int(root, "recovered");
                            failed = Int(root, "failed");
                            notRecovered = Int(root, "not_recovered");
                            downloaded = Long(root, "downloaded_bytes");
                            replaced = Int(root, "replaced");
                            break;

                        case "begin":
                        {
                            var number = Int(root, "n");

                            byNumber[number] = new RecoveryLogOperation
                            {
                                Number = number,
                                ParentId = Text(root, "parent"),
                                ChildId = Text(root, "child"),
                                ParentName = Text(root, "parent_name"),
                                Folder = Text(root, "folder"),
                                Plan = Text(root, "plan"),
                                StartedAt = Stamp(root, "ts") ?? DateTimeOffset.MinValue,
                                Outcome = "not recovered",
                                Reason = "the operation never ended",
                                IsInterrupted = true,
                            };

                            order.Add(number);
                            break;
                        }

                        case "candidate":
                        {
                            var number = Int(root, "n");
                            if (byNumber.TryGetValue(number, out var operation))
                            {
                                byNumber[number] = operation with { Source = Text(root, "source") };
                            }

                            break;
                        }

                        case "parent_restored":
                        {
                            var number = Int(root, "n");
                            if (byNumber.TryGetValue(number, out var operation))
                            {
                                byNumber[number] = operation with
                                {
                                    CopiedTo = Text(root, "copied_to"),
                                    CopiedBytes = Long(root, "copied_bytes"),
                                };
                            }

                            break;
                        }

                        case "child_regenerated":
                        {
                            var number = Int(root, "n");
                            if (byNumber.TryGetValue(number, out var operation))
                            {
                                byNumber[number] = operation with
                                {
                                    ResizedTo = Text(root, "resized_to"),
                                    Result = Text(root, "result"),
                                };
                            }

                            break;
                        }

                        case "end":
                        {
                            var number = Int(root, "n");

                            ends.TryGetValue(number, out var endCount);
                            ends[number] = endCount + 1;

                            if (!byNumber.TryGetValue(number, out var operation))
                            {
                                // An end with no begin: the operation does not fit.
                                byNumber[number] = new RecoveryLogOperation
                                {
                                    Number = number,
                                    StartedAt = Stamp(root, "ts") ?? DateTimeOffset.MinValue,
                                    Outcome = "not recovered",
                                    Reason = "the operation ended without beginning",
                                    IsMalformed = true,
                                };

                                order.Add(number);
                                break;
                            }

                            var ended = Stamp(root, "ts");

                            byNumber[number] = operation with
                            {
                                Outcome = Text(root, "outcome"),
                                Reason = Text(root, "reason"),
                                EndedAt = ended,
                                IsInterrupted = false,
                                // Two ends for one operation: the second does not fit.
                                IsMalformed = endCount > 0,
                            };

                            break;
                        }
                    }
                }
            }
        }

        var operations = order
            .Select(n => byNumber[n])
            .ToList();

        return new RecoveryLogRead
        {
            Format = format,
            Started = started,
            Shown = shown,
            Recoverable = recoverable,
            Folders = folders,
            Recovered = recovered,
            Failed = failed,
            NotRecovered = notRecovered,
            DownloadedBytes = downloaded,
            Replaced = replaced,
            Operations = operations,
            MalformedLines = malformedLines,
        };
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : 0;

    private static long Long(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : 0;

    private static DateTimeOffset? Stamp(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParse(
            value.GetString(),
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind,
            out var moment)
            ? moment
            : null;
}
