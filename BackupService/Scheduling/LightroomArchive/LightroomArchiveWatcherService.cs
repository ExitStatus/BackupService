using System.Collections.Concurrent;
using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Background service that keeps a live <see cref="FileSystemWatcher"/> on each item of every enabled
    /// <see cref="ProfileType.LightroomArchive"/> profile, mirroring changes into the target (plus the matching
    /// raw sidecars) after a per-item debounce window. Also the <see cref="ILightroomArchiveManager"/> the rest
    /// of the app calls to keep the watchers in step with profile changes. Registered once and shared in all
    /// three roles (singleton, <see cref="ILightroomArchiveManager"/>, hosted service) — the LightroomArchive
    /// counterpart to <see cref="InstantSyncWatcherService"/>, whose retry/catch-up behaviour it mirrors.
    ///
    /// Unlike the instant-sync watcher there is no local/remote split: <see cref="ILightroomArchiveProcessor"/>
    /// is endpoint-aware, so the watcher always flushes through it regardless of the target. A catch-up pass feeds
    /// it every in-scope source file (copy-if-changed makes the unchanged ones cheap).
    /// </summary>
    public sealed class LightroomArchiveWatcherService(
        IDatabaseContextFactory contextFactory,
        ILightroomArchiveProcessor processor,
        IOperationLogFactory operationLogFactory,
        IBackupRunRecorder runRecorder,
        ILogger<LightroomArchiveWatcherService> logger) : BackgroundService, ILightroomArchiveManager
    {
        private readonly Dictionary<int, List<ItemWatcher>> _watchers = new();
        // Items that couldn't be watched yet (source folder missing), retried every WatchRetryInterval.
        private readonly Dictionary<int, List<(LightroomArchiveItem Item, int? TargetConnectionId, LightroomArchiveSettings Settings)>> _unstarted = new();
        // One pass at a time per item, across watcher instances (see InstantSyncWatcherService).
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _itemGates = new();
        private readonly object _lock = new();
        private Timer? _watchRetryTimer;

        private CancellationToken _stoppingToken;

        /// <summary>The first retry delay after a failed flush (doubles per attempt, up to 30 minutes). Test seam.</summary>
        internal TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromMinutes(1);

        /// <summary>How often an item whose source folder is missing is retried. Test seam.</summary>
        internal TimeSpan WatchRetryInterval { get; set; } = TimeSpan.FromMinutes(1);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _stoppingToken = stoppingToken;
            logger.LogInformation("Lightroom archive watcher service started.");

            await LoadAllAsync(stoppingToken);

            try
            {
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Shutting down.
            }

            DisposeAllWatchers();
            logger.LogInformation("Lightroom archive watcher service stopping.");
        }

        public async Task SyncAsync(int profileId, CancellationToken cancellationToken = default)
        {
            var profile = await ReadProfileAsync(profileId, cancellationToken);

            lock (_lock)
            {
                RemoveWatchers(profileId);

                if (profile is { Enabled: true, Type: ProfileType.LightroomArchive })
                {
                    RegisterProfile(profile);
                }
            }
        }

        /// <summary>Diagnostic/test helper: number of live item watchers for a profile.</summary>
        public int WatcherCount(int profileId)
        {
            lock (_lock)
            {
                return _watchers.TryGetValue(profileId, out var list) ? list.Count : 0;
            }
        }

        private async Task LoadAllAsync(CancellationToken cancellationToken)
        {
            List<Profile> profiles;
            await using (var db = contextFactory.CreateDbContext())
            {
                profiles = await db.Profiles
                    .AsNoTracking()
                    .Include(p => p.LightroomArchiveItems)
                    .Where(p => p.Enabled && p.Type == ProfileType.LightroomArchive)
                    .ToListAsync(cancellationToken);
            }

            lock (_lock)
            {
                DisposeAllWatchers();
                foreach (var profile in profiles)
                {
                    RegisterProfile(profile);
                }
            }

            logger.LogInformation("Lightroom archive watcher service loaded {Count} profile(s).", profiles.Count);
        }

        /// <summary>Creates and starts a watcher per item. Caller holds <see cref="_lock"/>.</summary>
        private void RegisterProfile(Profile profile)
        {
            var settings = LightroomArchiveSettings.FromProfile(profile);
            foreach (var item in profile.LightroomArchiveItems)
            {
                if (TryStartWatcher(profile.Id, item, profile.TargetConnectionId, settings, catchUpOnStart: false) is { } failure)
                {
                    // A missing/unreadable source folder must not stop the other items being watched — and it's
                    // retried, so a drive that isn't mounted yet at logon is picked up once it is.
                    logger.LogWarning(failure, "Could not watch lightroom archive item '{Item}' (source '{Source}') for profile {ProfileId}; retrying every {Interval}.",
                        item.Name, item.SourceFolder, profile.Id, WatchRetryInterval);
                    AddUnstarted(profile.Id, item, profile.TargetConnectionId, settings);
                }
            }
        }

        // Starts watching an item; returns the failure, or null on success. Caller holds _lock.
        private Exception? TryStartWatcher(int profileId, LightroomArchiveItem item, int? targetConnectionId, LightroomArchiveSettings settings, bool catchUpOnStart)
        {
            try
            {
                var watcher = new ItemWatcher(item, targetConnectionId, settings, profileId, processor, operationLogFactory, runRecorder, logger,
                    _itemGates.GetOrAdd(item.Id, _ => new SemaphoreSlim(1, 1)), RetryBaseDelay, catchUpOnStart, _stoppingToken);
                if (!_watchers.TryGetValue(profileId, out var list))
                {
                    _watchers[profileId] = list = [];
                }
                list.Add(watcher);
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }

        private void AddUnstarted(int profileId, LightroomArchiveItem item, int? targetConnectionId, LightroomArchiveSettings settings)
        {
            if (!_unstarted.TryGetValue(profileId, out var pending))
            {
                _unstarted[profileId] = pending = [];
            }
            pending.Add((item, targetConnectionId, settings));
            _watchRetryTimer ??= new Timer(_ => RetryUnstarted(), null, Timeout.Infinite, Timeout.Infinite);
            _watchRetryTimer.Change(WatchRetryInterval, Timeout.InfiniteTimeSpan);
        }

        // Retries the items that couldn't be watched; one that now can is caught up with a full pass straight away.
        private void RetryUnstarted()
        {
            lock (_lock)
            {
                foreach (var profileId in _unstarted.Keys.ToList())
                {
                    var stillPending = _unstarted[profileId]
                        .Where(p => TryStartWatcher(profileId, p.Item, p.TargetConnectionId, p.Settings, catchUpOnStart: true) is not null)
                        .ToList();
                    if (stillPending.Count == 0)
                    {
                        _unstarted.Remove(profileId);
                        logger.LogInformation("Lightroom archive profile {ProfileId}: watching resumed.", profileId);
                    }
                    else
                    {
                        _unstarted[profileId] = stillPending;
                    }
                }

                if (_unstarted.Count > 0)
                {
                    _watchRetryTimer?.Change(WatchRetryInterval, Timeout.InfiniteTimeSpan);
                }
            }
        }

        /// <summary>Disposes and forgets a profile's watchers. Caller holds <see cref="_lock"/>.</summary>
        private void RemoveWatchers(int profileId)
        {
            _unstarted.Remove(profileId);
            if (_watchers.Remove(profileId, out var list))
            {
                foreach (var watcher in list)
                {
                    watcher.Dispose();
                }
            }
        }

        private void DisposeAllWatchers()
        {
            lock (_lock)
            {
                foreach (var list in _watchers.Values)
                {
                    foreach (var watcher in list)
                    {
                        watcher.Dispose();
                    }
                }
                _watchers.Clear();
                _unstarted.Clear();
                _watchRetryTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private async Task<Profile?> ReadProfileAsync(int profileId, CancellationToken cancellationToken)
        {
            await using var db = contextFactory.CreateDbContext();

            return await db.Profiles
                .AsNoTracking()
                .Include(p => p.LightroomArchiveItems)
                .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        }

        public override void Dispose()
        {
            DisposeAllWatchers();
            _watchRetryTimer?.Dispose();
            base.Dispose();
        }

        // What a flush leaves owing (see InstantSyncWatcherService).
        private enum FlushOutcome
        {
            Done,
            RetryChanges,
            RetryWithCatchUp,
            Cancelled,
        }

        /// <summary>
        /// Watches a single item's source folder and batches its change events. A debounce timer is re-armed
        /// on every event; when it fires (after the source has been quiet for the debounce window) the queued
        /// changes are processed on a single in-flight pass. Mirrors <c>InstantSyncWatcherService.ItemWatcher</c>.
        /// </summary>
        private sealed class ItemWatcher : IDisposable
        {
            private const int MaxCatchUpRetries = 6;
            private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

            private readonly LightroomArchiveItem _item;
            private readonly int? _targetConnectionId;
            private readonly LightroomArchiveSettings _settings;
            private readonly int _profileId;
            private readonly ILightroomArchiveProcessor _processor;
            private readonly IOperationLogFactory _logFactory;
            private readonly IBackupRunRecorder _runRecorder;
            private readonly ILogger _logger;
            private readonly SemaphoreSlim _itemGate;
            private readonly TimeSpan _retryBaseDelay;
            private readonly CancellationTokenSource _cts;

            private readonly FileSystemWatcher _watcher;
            private readonly Timer _timer;
            private readonly Timer _restartTimer;
            private readonly TimeSpan _debounce;

            private readonly object _gate = new();
            private readonly HashSet<string> _pendingChanges = new(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> _pendingDeletes = new(StringComparer.OrdinalIgnoreCase);
            private bool _catchUpNeeded;
            private int _retryAttempt;
            private DateTime _backoffUntilUtc = DateTime.MinValue;
            private bool _processing;
            private bool _disposed;

            public ItemWatcher(
                LightroomArchiveItem item,
                int? targetConnectionId,
                LightroomArchiveSettings settings,
                int profileId,
                ILightroomArchiveProcessor processor,
                IOperationLogFactory logFactory,
                IBackupRunRecorder runRecorder,
                ILogger logger,
                SemaphoreSlim itemGate,
                TimeSpan retryBaseDelay,
                bool catchUpOnStart,
                CancellationToken stoppingToken)
            {
                _item = item;
                _targetConnectionId = targetConnectionId;
                _settings = settings;
                _profileId = profileId;
                _processor = processor;
                _logFactory = logFactory;
                _runRecorder = runRecorder;
                _logger = logger;
                _itemGate = itemGate;
                _retryBaseDelay = retryBaseDelay;
                _debounce = TimeSpan.FromMilliseconds(Math.Max(0, item.DebounceMilliseconds));

                // Throws if the source folder doesn't exist — the caller logs it and retries later.
                _watcher = new FileSystemWatcher(item.SourceFolder)
                {
                    IncludeSubdirectories = item.IncludeSubFolders,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 64 * 1024,
                };

                _cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                _timer = new Timer(OnDebounceElapsed, state: null, Timeout.Infinite, Timeout.Infinite);
                _restartTimer = new Timer(OnRestartTimer, state: null, Timeout.Infinite, Timeout.Infinite);

                _watcher.Created += OnChanged;
                _watcher.Changed += OnChanged;
                _watcher.Deleted += OnDeleted;
                _watcher.Renamed += OnRenamed;
                _watcher.Error += OnError;
                _watcher.EnableRaisingEvents = true;

                if (catchUpOnStart)
                {
                    RequestCatchUp();
                }
            }

            private void OnChanged(object sender, FileSystemEventArgs e)
            {
                // A folder's own "changed" event just means an entry inside it changed — and those entries raise
                // their own events. Only a folder that arrives (created or renamed) is queued, because the processor
                // copies a queued folder's entire contents.
                if (e.ChangeType == WatcherChangeTypes.Changed && Directory.Exists(e.FullPath))
                {
                    return;
                }
                QueueChange(e.FullPath);
            }

            private void OnDeleted(object sender, FileSystemEventArgs e) => QueueDelete(e.FullPath);

            private void OnRenamed(object sender, RenamedEventArgs e)
            {
                QueueDelete(e.OldFullPath);
                QueueChange(e.FullPath);
            }

            private void OnError(object sender, ErrorEventArgs e)
            {
                if (e.GetException() is InternalBufferOverflowException)
                {
                    _logger.LogWarning("Lightroom archive item '{Item}' (profile {ProfileId}): too many changes at once — some were missed; catching up with a full pass.",
                        _item.Name, _profileId);
                    RequestCatchUp();
                    return;
                }

                _logger.LogWarning(e.GetException(),
                    "File watcher for lightroom archive item '{Item}' (profile {ProfileId}) stopped; retrying.", _item.Name, _profileId);
                ScheduleRestart();
            }

            private void ScheduleRestart()
            {
                try
                {
                    _restartTimer.Change(_retryBaseDelay, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Disposed concurrently.
                }
            }

            private void OnRestartTimer(object? state)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                }
                try
                {
                    _watcher.EnableRaisingEvents = false;
                    _watcher.EnableRaisingEvents = true; // throws while the folder still isn't there
                    _logger.LogInformation("File watcher for lightroom archive item '{Item}' (profile {ProfileId}) restarted.", _item.Name, _profileId);
                    RequestCatchUp();
                }
                catch (Exception ex) when (ex is not ObjectDisposedException)
                {
                    ScheduleRestart();
                }
                catch (ObjectDisposedException)
                {
                    // Disposed concurrently.
                }
            }

            private void RequestCatchUp()
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _catchUpNeeded = true;
                }
                ArmTimer(_debounce);
            }

            private void QueueChange(string fullPath)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _pendingChanges.Add(fullPath);
                    _pendingDeletes.Remove(fullPath);
                }
                ArmTimer(_debounce);
            }

            private void QueueDelete(string fullPath)
            {
                if (!_item.AllowDeletions)
                {
                    return;
                }
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _pendingDeletes.Add(fullPath);
                    _pendingChanges.Remove(fullPath);
                }
                ArmTimer(_debounce);
            }

            // Fires the flush after `due` — but never before a pending retry's backoff is over.
            private void ArmTimer(TimeSpan due)
            {
                lock (_gate)
                {
                    var backoffLeft = _backoffUntilUtc - DateTime.UtcNow;
                    if (backoffLeft > due)
                    {
                        due = backoffLeft;
                    }
                }
                try
                {
                    _timer.Change(due, Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                    // Disposed concurrently — nothing to schedule.
                }
            }

            private void OnDebounceElapsed(object? state)
            {
                HashSet<string> changes, deletes;
                bool catchUp;
                lock (_gate)
                {
                    if (_processing || _disposed)
                    {
                        return;
                    }
                    if (_pendingChanges.Count == 0 && _pendingDeletes.Count == 0 && !_catchUpNeeded)
                    {
                        return;
                    }

                    changes = new HashSet<string>(_pendingChanges, StringComparer.OrdinalIgnoreCase);
                    deletes = new HashSet<string>(_pendingDeletes, StringComparer.OrdinalIgnoreCase);
                    catchUp = _catchUpNeeded;
                    _pendingChanges.Clear();
                    _pendingDeletes.Clear();
                    _catchUpNeeded = false;
                    _processing = true;
                }

                _ = Task.Run(() => ProcessThenReleaseAsync(changes, deletes, catchUp));
            }

            private async Task ProcessThenReleaseAsync(HashSet<string> changes, HashSet<string> deletes, bool catchUp)
            {
                var token = _cts.Token;
                var outcome = FlushOutcome.Done;
                try
                {
                    await _itemGate.WaitAsync(token);
                    try
                    {
                        outcome = await ProcessFlushAsync(changes, deletes, catchUp, token);
                    }
                    finally
                    {
                        _itemGate.Release();
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    outcome = FlushOutcome.Cancelled;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Lightroom archive flush failed for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                    outcome = FlushOutcome.RetryChanges;
                }
                finally
                {
                    AfterFlush(outcome, changes, deletes, catchUp);
                }
            }

            private void AfterFlush(FlushOutcome outcome, HashSet<string> changes, HashSet<string> deletes, bool catchUp)
            {
                TimeSpan? due = null;
                var gaveUp = false;
                bool disposed;
                lock (_gate)
                {
                    _processing = false;
                    disposed = _disposed;
                    if (!disposed)
                    {
                        switch (outcome)
                        {
                            case FlushOutcome.RetryChanges:
                                foreach (var path in changes.Where(p => !_pendingDeletes.Contains(p)))
                                {
                                    _pendingChanges.Add(path);
                                }
                                foreach (var path in deletes.Where(p => !_pendingChanges.Contains(p)))
                                {
                                    _pendingDeletes.Add(path);
                                }
                                _catchUpNeeded |= catchUp;
                                due = BackOff();
                                break;

                            case FlushOutcome.RetryWithCatchUp:
                                if (_retryAttempt < MaxCatchUpRetries)
                                {
                                    _catchUpNeeded = true;
                                    due = BackOff();
                                }
                                else
                                {
                                    gaveUp = true;
                                    ResetBackOff();
                                }
                                break;

                            case FlushOutcome.Done:
                                ResetBackOff();
                                break;
                        }

                        if (due is null && (_pendingChanges.Count > 0 || _pendingDeletes.Count > 0 || _catchUpNeeded))
                        {
                            due = _debounce;
                        }
                    }
                }

                if (gaveUp)
                {
                    _logger.LogWarning("Lightroom archive item '{Item}' (profile {ProfileId}): some files still couldn't be archived after {Attempts} catch-up passes; they'll be retried when they next change, or on Run now.",
                        _item.Name, _profileId, MaxCatchUpRetries);
                }
                if (due is { } delay)
                {
                    ArmTimer(delay);
                }
                if (disposed)
                {
                    _cts.Dispose();
                }
            }

            private TimeSpan BackOff()
            {
                _retryAttempt++;
                var ticks = Math.Min(_retryBaseDelay.Ticks * (1L << Math.Min(_retryAttempt - 1, 20)), MaxRetryDelay.Ticks);
                var delay = TimeSpan.FromTicks(Math.Max(ticks, _retryBaseDelay.Ticks));
                _backoffUntilUtc = DateTime.UtcNow + delay;
                return delay;
            }

            private void ResetBackOff()
            {
                _retryAttempt = 0;
                _backoffUntilUtc = DateTime.MinValue;
            }

            private async Task<FlushOutcome> ProcessFlushAsync(HashSet<string> changes, HashSet<string> deletes, bool catchUp, CancellationToken token)
            {
                var startedUtc = DateTimeOffset.UtcNow;
                var stopwatch = Stopwatch.StartNew();
                var changeCount = changes.Count + deletes.Count;
                var title = catchUp
                    ? $"Lightroom Archive '{_item.Name}' — catch-up pass"
                    : $"Lightroom Archive '{_item.Name}' — {changeCount} change(s)";

                // Deferred: the log is only created once the processor writes its first line, so an all-no-op
                // flush leaves no noise behind.
                var log = new DeferredOperationLogger(_logFactory, title, profileId: _profileId);

                BackupResult result;
                try
                {
                    // A catch-up feeds every in-scope source file (copy-if-changed skips the unchanged ones), plus
                    // whatever changed and was deleted since.
                    if (catchUp)
                    {
                        changes.UnionWith(SourceFiles());
                    }
                    result = await _processor.ProcessBatchAsync(_item, _targetConnectionId, _settings, changes, deletes, log, progress: null, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await log.ErrorAsync($"Lightroom archive '{_item.Name}' failed — it will be retried", ex);
                    await RecordRunAsync(startedUtc, stopwatch, new BackupResult { Errors = 1 }, RunOutcome.Failed, log.OperationLogId);
                    await log.SetSummaryAsync($"Lightroom Archive '{_item.Name}' failed in {FormatDuration(stopwatch.Elapsed)}", OperationLogLevel.Error);
                    return FlushOutcome.RetryChanges;
                }

                var owing = result.Errors > 0 || result.Warnings > 0 ? FlushOutcome.RetryWithCatchUp : FlushOutcome.Done;

                // No real work (a no-op flush) — leave no log and no run row.
                if (!log.WasCreated)
                {
                    return owing;
                }

                stopwatch.Stop();

                // Record the run before the summary write (whose ILogWatcher.Notify the dashboard refreshes on).
                var outcome = result.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : result.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;
                await RecordRunAsync(startedUtc, stopwatch, result, outcome, log.OperationLogId);

                var duration = FormatDuration(stopwatch.Elapsed);
                var counts = $"{result.Copied} copied, {result.Updated} updated, {result.Deleted} deleted";
                var what = catchUp ? "caught up" : $"synced {changeCount} change(s)";
                await log.SetSummaryAsync(
                    result.Errors == 0
                        ? $"Lightroom Archive '{_item.Name}' {what} in {duration} — {counts}"
                        : $"Lightroom Archive '{_item.Name}' completed with {result.Errors} error(s) in {duration} — {counts}",
                    result.Errors == 0 ? OperationLogLevel.Info : OperationLogLevel.Error);
                return owing;
            }

            // Every file in the item's scope (the source is always local), skipping folders that can't be read.
            private IEnumerable<string> SourceFiles() =>
                Directory.EnumerateFiles(_item.SourceFolder, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = _item.IncludeSubFolders,
                    IgnoreInaccessible = true,
                });

            // Records one BackupRun row for a flush that did real work, so live lightroom-archive activity
            // shows in the dashboard's Recent Runs (a no-op flush writes nothing and is never recorded).
            private async Task RecordRunAsync(DateTimeOffset startedUtc, Stopwatch stopwatch, BackupResult result, RunOutcome outcome, int operationLogId)
            {
                try
                {
                    await _runRecorder.RecordAsync(
                        _profileId, ProfileType.LightroomArchive, manual: false, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                        result, outcome, operationLogId == 0 ? null : operationLogId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not record lightroom archive run for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                }
            }

            private static string FormatDuration(TimeSpan elapsed) =>
                elapsed.TotalSeconds >= 1
                    ? $"{elapsed.TotalSeconds:0.##}s"
                    : $"{elapsed.TotalMilliseconds:0}ms";

            public void Dispose()
            {
                bool disposeCtsNow;
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }
                    _disposed = true;
                    disposeCtsNow = !_processing;
                }

                try
                {
                    _cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The pass finished and disposed it in between.
                }
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _timer.Dispose();
                _restartTimer.Dispose();
                if (disposeCtsNow)
                {
                    _cts.Dispose();
                }
            }
        }
    }
}
