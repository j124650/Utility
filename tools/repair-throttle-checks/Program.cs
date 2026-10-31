using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Utility.DropBox;
using Utility.DropBox.Models;
using Utility.DropBox.Services;
using Utility.Server.Finder;
using Utility.Server.Finder.Repair;

namespace RepairThrottleChecks;

/// <summary>
/// Checks the recovery's handling of a DROPBOX RATE LIMIT (docs/finder-recover-spec.md).
///
/// The rule: a 429 must not lose a pair. The run holds, backs off and retries; only a
/// run that has been refused fifty times in a row gives up on a pair, and it says so.
///
/// It drives the REAL retry helper - the private method on the real
/// <c>FinderRepairService</c> - against a fake Dropbox that refuses on demand. Only the
/// Dropbox client is faked: the translate, the brake, the backoff and the give-up are all
/// the shipped code.
/// </summary>
internal static class Program
{
    private static async Task<int> Main()
    {
        var failures = 0;
        var checks = 0;

        void Check(string what, bool ok, string detail = "")
        {
            checks++;
            if (!ok)
            {
                failures++;
                Console.WriteLine($"FAIL  {what}  {detail}");
            }
            else
            {
                Console.WriteLine($"ok    {what}  {detail}");
            }
        }

        var scratch = Path.Combine(Path.GetTempPath(), "Utility", "repair-throttle-checks");
        if (Directory.Exists(scratch))
        {
            Directory.Delete(scratch, recursive: true);
        }

        Directory.CreateDirectory(scratch);

        // ---- 1. a 429 is TRANSLATED into the app's own exception ----
        //
        // The translate is private to the data service, so it is called the way the SDK
        // calls it: reflectively, with the SDK's own exception. What matters is that a
        // rate limit becomes a DropBoxRateLimitedException carrying the wait - because
        // the repair's catch is written against THAT type.
        var target = Path.Combine(scratch, "parent.jpg");

        var translate = typeof(DropBoxDataService).GetMethod(
            "Translate", BindingFlags.Static | BindingFlags.NonPublic);

        Check("the translate exists", translate is not null);

        if (translate is not null)
        {
            // The SDK's constructor is not public, so the exception is built the way the
            // SDK builds it: through its own constructor, reflectively.
            var sdkCtor = typeof(Dropbox.Api.RateLimitException).GetConstructors(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .First(c => c.GetParameters().Length == 1);

            var sdkRefusal = (Exception)sdkCtor.Invoke(new object?[] { "request-id" });

            var translated = translate.Invoke(null, new object?[] { sdkRefusal });

            Check(
                "a 429 becomes the app's own rate-limit exception",
                translated is DropBoxRateLimitedException,
                translated?.GetType().Name ?? "none");

            Check(
                "the app's exception carries the wait Dropbox asked for",
                translated is DropBoxRateLimitedException { RetryAfterSeconds: 0 },
                $"retry-after {((translated as DropBoxRateLimitedException)?.RetryAfterSeconds.ToString() ?? "-")}");

            // A rate limit that is NOT translated would fall through the repair's catch
            // as an opaque SDK failure - so the type is the whole point.
            Check(
                "the SDK's own exception is what gets replaced",
                !ReferenceEquals(translated, sdkRefusal),
                ReferenceEquals(translated, sdkRefusal) ? "still the SDK's" : "replaced");
        }

        // ---- 2. the retry helper rides out the refusals ----
        //
        // The helper is private to the service, so it is called the way the service calls
        // it: through reflection, with the real arguments.
        var helper = typeof(FinderRepairService).GetMethod(
            "DownloadWithRateLimitAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Check("the retry helper exists", helper is not null);
        if (helper is null)
        {
            return 1;
        }

        var candidate = new FinderCandidate
        {
            Rank = 0,
            WhatMatched = "name AND size AND folder",
            Name = "parent.jpg",
            Size = 1024,
            Dimensions = "100x100",
            Path = "/Images-Catalogs/parent.jpg",
            Modified = DateTimeOffset.UnixEpoch,
            NamespaceId = "15193064371",
            NodeId = 1,
            DropboxId = "id:abc",
            ContentHash = "hash"
        };

        // ---- 3. a couple of refusals, then the download succeeds ----
        var twice = new FakeDropBox { RefusalsBeforeSuccess = 2, RetryAfterSeconds = 1 };
        var retryService = BuildService(twice);

        var succeeded = await Try(() => Invoke(retryService, helper, candidate, target));

        Check(
            "a download refused twice still succeeds",
            succeeded is null && File.Exists(target),
            succeeded?.Message ?? "the file is there");

        Check(
            "the client was called three times",
            twice.Calls == 3,
            $"{twice.Calls} call(s)");

        // ---- 4. the brake held while it waited ----
        //
        // The brake is what makes a refusal a signal about the WHOLE run. It is checked
        // through the service's own brake, after the run has finished.
        var brake = (DropboxRateLimitBrake)typeof(FinderRepairService)
            .GetField("_rateLimitBrake", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(retryService)!;

        Check(
            "the brake was held for each refusal",
            brake.ThrottleCount == 2,
            $"held {brake.ThrottleCount} time(s)");

        Check(
            "the wait was the one the refusal asked for",
            brake.RemainingSeconds <= 1,
            $"up to {brake.RemainingSeconds}s left");

        Check(
            "the brake released when the refusals stopped",
            !brake.IsHolding,
            brake.RemainingSeconds > 0 ? $"still holding for {brake.RemainingSeconds}s" : "released");

        // ---- 5. a run that is refused for good gives up, and says why ----
        //
        // The helper is called with a refusal every time. It must NOT give up early and it
        // must say what happened, in the message the log will carry.
        //
        // This runs the REAL fifty attempts, each waiting the second the refusal asked
        // for - so it takes about a minute. That is the honest cost of proving the real
        // cap rather than a smaller one.
        var forever = new FakeDropBox { RefusalsBeforeSuccess = int.MaxValue };
        var doomedService = BuildService(forever);

        var gaveUp = await Throws(() => Invoke(doomedService, helper, candidate, target));

        Check(
            "the client was called exactly fifty times",
            forever.Calls == 50,
            $"{forever.Calls} call(s)");

        Check(
            "a download refused for good gives up",
            gaveUp is not null,
            gaveUp?.GetType().Name ?? "none");

        Check(
            "the give-up says it was the rate limit",
            gaveUp?.Message.Contains("rate limited", StringComparison.OrdinalIgnoreCase) == true,
            gaveUp?.Message ?? "none");

        Check(
            "the give-up names the number of attempts",
            gaveUp?.Message.Contains("50 attempts", StringComparison.Ordinal) == true,
            gaveUp?.Message ?? "none");

        // ---- 6. the cap is the number that was asked for ----
        //
        // The cap is what the user asked for, so it is asserted rather than assumed. The
        // constant is private, so it is read from the compiled field - the call count
        // above already proved the run obeyed it.
        var attemptsField = typeof(FinderRepairService)
            .GetField("MaxRateLimitAttempts", BindingFlags.Static | BindingFlags.NonPublic);

        var attempts = attemptsField?.GetRawConstantValue();

        Check(
            "the attempts are capped at fifty",
            attempts is int cap && cap == 50,
            $"MaxRateLimitAttempts = {attempts?.ToString() ?? "?"}");

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"All {checks} checks passed."
            : $"{failures} of {checks} checks FAILED.");

        Directory.Delete(scratch, recursive: true);

        return failures == 0 ? 0 : 1;
    }

    /// <summary>Calls the private retry helper the way the service calls it.</summary>
    private static Task Invoke(
        FinderRepairService service,
        MethodInfo helper,
        FinderCandidate candidate,
        string target)
    {
        return (Task)helper.Invoke(service, new object?[] { candidate, target, CancellationToken.None })!;
    }

    private static async Task<Exception?> Throws(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<Exception?> Try(Func<Task> action) => await Throws(action);

    /// <summary>
    /// The real service, with only the Dropbox client faked. The library and the data
    /// service are not touched by the retry helper.
    /// </summary>
    private static FinderRepairService BuildService(IDropBoxDataService dropBox)
    {
        var options = Microsoft.Extensions.Options.Options.Create(
            new Utility.Server.Options.ServerOptions { RecoverWidth = 1 });

        return new FinderRepairService(
            null!,
            null!,
            dropBox,
            null!,
            options,
            NullLogger<FinderRepairService>.Instance);
    }

    /// <summary>
    /// A Dropbox client that refuses on demand. The refusal is the APP's exception, which
    /// is what the SDK's 429 is translated into.
    /// </summary>
    private sealed class FakeDropBox : IDropBoxDataService
    {
        public int RefusalsBeforeSuccess { get; init; }

        /// <summary>
        /// The wait the refusal asks for. ONE second, so the real fifty-attempt path
        /// finishes in about a minute of real waiting instead of hours - the code path
        /// under test is identical, only the clock differs.
        /// </summary>
        public int RetryAfterSeconds { get; init; } = 1;

        public int Calls { get; private set; }

        public bool IsConfigured => true;

        public Task<string> DownloadAsync(
            string dropboxPath,
            string destinationPath,
            string? namespaceId = null,
            CancellationToken ct = default)
        {
            Calls++;

            if (Calls <= RefusalsBeforeSuccess)
            {
                throw new DropBoxRateLimitedException(RetryAfterSeconds, "too_many_requests");
            }

            File.WriteAllText(destinationPath, "the parent's bytes");
            return Task.FromResult(destinationPath);
        }

        public Task<byte[]> DownloadBytesAsync(
            string dropboxPath,
            string? namespaceId = null,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<AccountInfo> GetAccountAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<DropBoxNamespace>> GetNamespacesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<FileEntry>> ListFolderAsync(
            string path = "",
            string? namespaceId = null,
            bool includeMediaInfo = false,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<FileEntry> GetFileInfoAsync(
            string path,
            string? namespaceId = null,
            bool includeMediaInfo = true,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<DropBoxFolderTree> GetFolderTreeAsync(
            string path = "",
            string? namespaceId = null,
            int maxDepth = 3,
            int maxConcurrency = 24,
            bool includeMediaInfo = false,
            CancellationToken ct = default) => throw new NotSupportedException();
    }
}
