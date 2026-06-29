using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.FileSystem;
using BackupService.Logging;
using BackupService.Notifications;
using BackupService.Profiles;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Handles a manual "Run now" of a <see cref="ProfileType.LightroomArchive"/> profile: a one-off full
    /// source→target reconcile of every item (every in-scope source file is copied if changed, and its matching
    /// raw sidecars pulled across). Live syncing is driven separately by
    /// <see cref="LightroomArchiveWatcherService"/>; this is the on-demand catch-up path. Owns the single
    /// operation log for the run. The LightroomArchive counterpart to <see cref="InstantSyncHandler"/>.
    /// </summary>
    public sealed class LightroomArchiveHandler(
        IOperationLogFactory operationLogFactory,
        ILightroomArchiveProcessor processor,
        IBackupFileSystem localFileSystem,
        IProfileStatusService statusService,
        IBackupRunRecorder runRecorder,
        ILogger<LightroomArchiveHandler> logger,
        IDesktopNotifier? notifier = null) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.LightroomArchive;

        public async Task HandleAsync(Profile profile, bool manual, CancellationToken cancellationToken)
        {
            var startedUtc = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var prefix = manual ? "[Manual] " : string.Empty;
            var handlerName = $"{prefix}{Type.GetDescription()} Handler"; // e.g. "[Manual] Lightroom Archive Handler"
            var settings = LightroomArchiveSettings.FromProfile(profile);
            var total = new BackupResult();
            var fatal = false;

            var log = await operationLogFactory.CreateAsync(
                $"{handlerName} called with {profile.LightroomArchiveItems.Count} item(s).",
                profileId: profile.Id,
                cancellationToken: cancellationToken);

            if (profile.NotificationsEnabled && profile.NotifyOnStart)
            {
                notifier?.NotifyBackupStarted(profile.Name, Type);
            }

            try
            {
                if (profile.LightroomArchiveItems.Count == 0)
                {
                    await log.AppendAsync("No lightroom archive items configured.");
                }
                else
                {
                    // Enumerate each item's source files once (the reconcile set) and pre-count them so the
                    // grid can show a "Running - {percent}%" progress.
                    var work = profile.LightroomArchiveItems
                        .Select(item => (Item: item, Files: EnumerateSourceFiles(item.SourceFolder, item.IncludeSubFolders)))
                        .ToList();

                    var totalFiles = work.Sum(w => w.Files.Count);
                    statusService.SetProgress(profile.Id, 0);
                    var progress = new ProfileProgressReporter(statusService, profile.Id, totalFiles);

                    foreach (var (item, files) in work)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await RunItemAsync(item, profile.TargetConnectionId, settings, files, log, total, progress, cancellationToken);
                    }
                }
            }
            catch (Exception ex)
            {
                fatal = true;
                statusService.Set(profile.Id, ProfileStatus.Error);
                logger.LogError(ex, "LightroomArchiveHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);
                var outcome = fatal ? RunOutcome.Failed
                    : total.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : total.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;

                await runRecorder.RecordAsync(
                    profile.Id, Type, manual, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                    total, outcome, log.OperationLogId, CancellationToken.None);

                var counts = $"{total.Copied} copied, {total.Updated} updated, {total.Deleted} deleted";
                var (summary, level) = outcome switch
                {
                    RunOutcome.Failed => ($"{handlerName} failed in {duration}", OperationLogLevel.Error),
                    RunOutcome.CompletedWithErrors => ($"{handlerName} completed with {total.Errors} error(s) in {duration} — {counts}", OperationLogLevel.Error),
                    RunOutcome.CompletedWithWarnings => ($"{handlerName} completed with {total.Warnings} warning(s) in {duration} — {counts}", OperationLogLevel.Warning),
                    _ => ($"{handlerName} ran successfully in {duration} — {counts}", OperationLogLevel.Info),
                };
                await log.SetSummaryAsync(summary, level);

                // Desktop notification for the completed manual reconcile (no-op unless on Windows + enabled).
                if (profile.NotificationsEnabled && profile.NotifyOnComplete)
                {
                    notifier?.NotifyBackupCompleted(profile.Name, Type, outcome);
                }
            }
        }

        private async Task RunItemAsync(
            LightroomArchiveItem item, int? targetConnectionId, LightroomArchiveSettings settings, IReadOnlyCollection<string> files,
            IOperationLogger log, BackupResult total, IProgress<int> progress, CancellationToken cancellationToken)
        {
            await log.AppendAsync($"Lightroom archive '{item.Name}': {item.SourceFolder} -> {item.TargetFolder}");

            try
            {
                // A full reconcile feeds every source file as a "change" (no deletions on a manual run).
                total.Add(await processor.ProcessBatchAsync(item, targetConnectionId, settings, files, deletedPaths: [], log, progress, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                total.Errors++;
                await log.ErrorAsync($"Lightroom archive '{item.Name}' failed", ex);
            }
        }

        /// <summary>Walks the local source folder, returning every file (honouring <paramref name="includeSub"/>).</summary>
        private List<string> EnumerateSourceFiles(string sourceFolder, bool includeSub)
        {
            var files = new List<string>();
            if (!localFileSystem.DirectoryExists(sourceFolder))
            {
                return files;
            }

            var stack = new Stack<string>();
            stack.Push(sourceFolder);
            while (stack.Count > 0)
            {
                var dir = stack.Pop();
                try
                {
                    files.AddRange(localFileSystem.GetFiles(dir));
                }
                catch
                {
                    // Unreadable folder — skip its files (the run continues).
                }

                if (includeSub)
                {
                    try
                    {
                        foreach (var sub in localFileSystem.GetDirectories(dir))
                        {
                            stack.Push(sub);
                        }
                    }
                    catch
                    {
                        // Can't enumerate sub-folders — skip them.
                    }
                }
            }

            return files;
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
    }
}
