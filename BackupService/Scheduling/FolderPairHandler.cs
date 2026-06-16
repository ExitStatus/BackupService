using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Extensions;
using BackupService.Logging;
using BackupService.Profiles;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Handles <see cref="ProfileType.FolderPair"/> profiles. For now it only logs that it was
    /// called (with the profile's folder pairs) — the real source→target copy logic lands here next.
    /// Each run writes exactly one operation log: the header starts as a "called…" message and is
    /// rewritten in a <c>finally</c> to a "ran successfully / failed in {duration}" summary at the
    /// matching level. A failure also sets the profile status to Error and re-throws.
    /// </summary>
    public sealed class FolderPairHandler(
        IOperationLogFactory operationLogFactory,
        IProfileStatusService statusService,
        ILogger<FolderPairHandler> logger) : IProfileTypeHandler
    {
        public ProfileType Type => ProfileType.FolderPair;

        public async Task HandleAsync(Profile profile, CancellationToken cancellationToken)
        {
            var stopwatch = Stopwatch.StartNew();
            var succeeded = false;
            var handlerName = $"{Type.GetDescription()} Handler"; // e.g. "Folder Pairs Handler"

            var log = await operationLogFactory.CreateAsync(
                $"{handlerName} called with {profile.FolderPairs.Count} folder pair(s).",
                profileId: profile.Id,
                cancellationToken: cancellationToken);

            try
            {
                logger.LogInformation(
                    "FolderPairHandler called for profile {ProfileId} ({ProfileName}) with {PairCount} folder pair(s).",
                    profile.Id, profile.Name, profile.FolderPairs.Count);

                if (profile.FolderPairs.Count == 0)
                {
                    await log.AppendAsync("No folder pairs configured.");
                }
                else
                {
                    foreach (var pair in profile.FolderPairs)
                    {
                        await log.AppendAsync($"{pair.Name}: {pair.SourceFolder} -> {pair.TargetFolder}");
                    }
                }

                succeeded = true;
            }
            catch (Exception ex)
            {
                statusService.Set(profile.Id, ProfileStatus.Error);
                logger.LogError(ex, "FolderPairHandler failed for profile {ProfileId} ({ProfileName}).", profile.Id, profile.Name);
                throw;
            }
            finally
            {
                stopwatch.Stop();
                var duration = FormatDuration(stopwatch.Elapsed);

                await log.SetSummaryAsync(
                    succeeded
                        ? $"{handlerName} ran successfully in {duration}"
                        : $"{handlerName} failed in {duration}",
                    succeeded ? OperationLogLevel.Info : OperationLogLevel.Error);
            }
        }

        private static string FormatDuration(TimeSpan elapsed) =>
            elapsed.TotalSeconds >= 1
                ? $"{elapsed.TotalSeconds:0.##}s"
                : $"{elapsed.TotalMilliseconds:0}ms";
    }
}
