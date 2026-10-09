using System.Collections.Concurrent;
using System.Diagnostics;
using BackupService.Database;
using BackupService.Enumerations;
using BackupService.Logging;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Scheduling
{
    /// <summary>
    /// Background service that keeps a live <see cref="FileSystemWatcher"/> on each item of every
    /// enabled <see cref="ProfileType.InstantSync"/> profile, mirroring changes into the target after a
    /// per-item debounce window. Also the <see cref="IInstantSyncManager"/> the rest of the app calls to
    /// keep the watchers in step with profile changes. Registered once and shared in all three roles
    /// (singleton, <see cref="IInstantSyncManager"/>, hosted service) — the instant-sync counterpart to
    /// <see cref="BackupSchedulerService"/>.
    ///
    /// Each item batches change events and processes them on a single in-flight pass, so changes that
    /// arrive while a copy is running are never lost and never start a second concurrent pass. Nothing a
    /// watcher reports is dropped silently: a flush that fails is retried (with backoff) until it succeeds; a flush
    /// that couldn't copy some files, a watcher buffer overflow (events lost) and a watcher restarted after an error
    /// are followed by a full catch-up pass; and an item whose source folder isn't there yet (a drive not mounted at
    /// logon) keeps being retried until it can be watched.
    /// </summary>
    public sealed class InstantSyncWatcherService(
        IDatabaseContextFactory contextFactory,
        IInstantSyncProcessor processor,
        IOneWaySyncSynchronizer synchronizer,
        IOperationLogFactory operationLogFactory,
        IBackupRunRecorder runRecorder,
        ILogger<InstantSyncWatcherService> logger) : BackgroundService, IInstantSyncManager
    {
        private readonly Dictionary<int, List<ItemWatcher>> _watchers = new();
        // Items that couldn't be watched yet (source folder missing), retried every WatchRetryInterval.
        private readonly Dictionary<int, List<(InstantSyncItem Item, int? TargetConnectionId)>> _unstarted = new();
        // One pass at a time per item, across watcher instances: an edit replaces an item's watcher while its
        // (now cancelled) pass may still be unwinding, and two passes must never write the same target at once.
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
            logger.LogInformation("Instant sync watcher service started.");

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
            logger.LogInformation("Instant sync watcher service stopping.");
        }

        public async Task SyncAsync(int profileId, CancellationToken cancellationToken = default)
        {
            var profile = await ReadProfileAsync(profileId, cancellationToken);

            lock (_lock)
            {
                RemoveWatchers(profileId);

                if (profile is { Enabled: true, Type: ProfileType.InstantSync })
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
                    .Include(p => p.InstantSyncItems)
                    .Where(p => p.Enabled && p.Type == ProfileType.InstantSync)
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

            logger.LogInformation("Instant sync watcher service loaded {Count} profile(s).", profiles.Count);
        }

        /// <summary>Creates and starts a watcher per item. Caller holds <see cref="_lock"/>.</summary>
        private void RegisterProfile(Profile profile)
        {
            // A source on a remote connection can't be watched (no FileSystemWatcher over SMB) — it
            // only syncs via a manual "Run now". A remote target is fine (the flush reconciles to it).
            // The connection is profile-level, so this gates the whole profile.
            if (profile.SourceConnectionId is not null)
            {
                logger.LogInformation(
                    "Instant sync profile {ProfileId} has a remote source — live watching is not supported; use Run now.",
                    profile.Id);
                return;
            }

            foreach (var item in profile.InstantSyncItems)
            {
                if (TryStartWatcher(profile.Id, item, profile.TargetConnectionId, catchUpOnStart: false) is { } failure)
                {
                    // A missing/unreadable source folder must not stop the other items being watched — and it's
                    // retried, so a drive that isn't mounted yet at logon is picked up once it is.
                    logger.LogWarning(failure, "Could not watch instant sync item '{Item}' (source '{Source}') for profile {ProfileId}; retrying every {Interval}.",
                        item.Name, item.SourceFolder, profile.Id, WatchRetryInterval);
                    AddUnstarted(profile.Id, item, profile.TargetConnectionId);
                }
            }
        }

        // Starts watching an item; returns the failure, or null on success. Caller holds _lock.
        private Exception? TryStartWatcher(int profileId, InstantSyncItem item, int? targetConnectionId, bool catchUpOnStart)
        {
            try
            {
                var watcher = new ItemWatcher(item, profileId, targetConnectionId, processor, synchronizer, operationLogFactory, runRecorder, logger,
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

        private void AddUnstarted(int profileId, InstantSyncItem item, int? targetConnectionId)
        {
            if (!_unstarted.TryGetValue(profileId, out var pending))
            {
                _unstarted[profileId] = pending = [];
            }
            pending.Add((item, targetConnectionId));
            _watchRetryTimer ??= new Timer(_ => RetryUnstarted(), null, Timeout.Infinite, Timeout.Infinite);
            _watchRetryTimer.Change(WatchRetryInterval, Timeout.InfiniteTimeSpan);
        }

        // Retries the items that couldn't be watched. One that now can is caught up with a full pass straight away
        // (the folder may have been written to while nothing was watching it).
        private void RetryUnstarted()
        {
            lock (_lock)
            {
                foreach (var profileId in _unstarted.Keys.ToList())
                {
                    var stillPending = _unstarted[profileId]
                        .Where(p => TryStartWatcher(profileId, p.Item, p.TargetConnectionId, catchUpOnStart: true) is not null)
                        .ToList();
                    if (stillPending.Count == 0)
                    {
                        _unstarted.Remove(profileId);
                        logger.LogInformation("Instant sync profile {ProfileId}: watching resumed.", profileId);
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
                .Include(p => p.InstantSyncItems)
                .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);
        }

        public override void Dispose()
        {
            DisposeAllWatchers();
            _watchRetryTimer?.Dispose();
            base.Dispose();
        }

        // What a flush leaves owing: nothing, the same changes again (it failed outright — e.g. the target was
        // unreachable), or a full catch-up pass (it ran, but some files couldn't be copied).
        private enum FlushOutcome
        {
            Done,
            RetryChanges,
            RetryWithCatchUp,
            Cancelled,
        }

        /// <summary>
        /// Watches a single item's source folder and batches its change events. A debounce timer is
        /// re-armed on every event; when it fires (after the source has been quiet for the debounce
        /// window) the queued changes are processed on a single in-flight pass.
        /// </summary>
        private sealed class ItemWatcher : IDisposable
        {
            // A flush that failed outright is retried until it succeeds; one that only left some files uncopied
            // (often a file that's locked) is caught up a limited number of times, so a file that stays locked
            // doesn't produce a warning every half hour forever.
            private const int MaxCatchUpRetries = 6;
            private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(30);

            private readonly InstantSyncItem _item;
            private readonly int _profileId;
            private readonly int? _targetConnectionId;
            private readonly IInstantSyncProcessor _processor;
            private readonly IOneWaySyncSynchronizer _synchronizer;
            private readonly IOperationLogFactory _logFactory;
            private readonly IBackupRunRecorder _runRecorder;
            private readonly ILogger _logger;
            private readonly SemaphoreSlim _itemGate;
            private readonly TimeSpan _retryBaseDelay;
            // Cancelled when this watcher is disposed (profile disabled/edited/deleted) or the app stops, so a pass
            // that's running stops instead of carrying on with the old settings.
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
            // Flushes that have failed outright in a row. Only the first is logged (and recorded as a failed run): a
            // target that's offline for a day would otherwise add an Error log and a Failed run every 30 minutes.
            // Touched only by the flush itself, and flushes never overlap.
            private int _failedFlushes;
            private DateTime _backoffUntilUtc = DateTime.MinValue;
            private bool _processing;
            private bool _disposed;

            public ItemWatcher(
                InstantSyncItem item,
                int profileId,
                int? targetConnectionId,
                IInstantSyncProcessor processor,
                IOneWaySyncSynchronizer synchronizer,
                IOperationLogFactory logFactory,
                IBackupRunRecorder runRecorder,
                ILogger logger,
                SemaphoreSlim itemGate,
                TimeSpan retryBaseDelay,
                bool catchUpOnStart,
                CancellationToken stoppingToken)
            {
                _item = item;
                _profileId = profileId;
                _targetConnectionId = targetConnectionId;
                _processor = processor;
                _synchronizer = synchronizer;
                _logFactory = logFactory;
                _runRecorder = runRecorder;
                _logger = logger;
                _itemGate = itemGate;
                _retryBaseDelay = retryBaseDelay;
                // Guard against a zero/negative debounce (timer requires a non-negative due time).
                _debounce = TimeSpan.FromMilliseconds(Math.Max(0, item.DebounceMilliseconds));

                // Throws if the source folder doesn't exist — the caller logs it and retries later. Created first, so
                // nothing else needs undoing when it does.
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
                // Old name disappeared; new name is a fresh change.
                QueueDelete(e.OldFullPath);
                QueueChange(e.FullPath);
            }

            private void OnError(object sender, ErrorEventArgs e)
            {
                if (e.GetException() is InternalBufferOverflowException)
                {
                    // Too many changes at once (a big unzip or checkout): events were lost, so catch up with a full
                    // pass rather than leave those files un-synced until they next change.
                    _logger.LogWarning("Instant sync item '{Item}' (profile {ProfileId}): too many changes at once — some were missed; catching up with a full pass.",
                        _item.Name, _profileId);
                    RequestCatchUp();
                    return;
                }

                // Anything else (the source folder went away, a network error) stops the watcher: keep trying to
                // restart it, then catch up on whatever happened meanwhile.
                _logger.LogWarning(e.GetException(),
                    "File watcher for instant sync item '{Item}' (profile {ProfileId}) stopped; retrying.", _item.Name, _profileId);
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
                    _logger.LogInformation("File watcher for instant sync item '{Item}' (profile {ProfileId}) restarted.", _item.Name, _profileId);
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
                    _pendingDeletes.Remove(fullPath); // a re-created path is a change, not a delete
                }
                ArmTimer(_debounce);
            }

            private void QueueDelete(string fullPath)
            {
                if (!_item.AllowDeletions)
                {
                    return; // deletions aren't mirrored for this item
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

            // Fires the flush after `due` — but never before a pending retry's backoff is over, so a busy source
            // doesn't hammer a target that's down.
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
                    // A pass is already running — it will pick up these events, or the re-arm in the
                    // pass's finally will trigger another timer fire. Single in-flight pass per item.
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
                    outcome = FlushOutcome.Cancelled; // disposed (profile edited/disabled) or shutting down
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Instant sync flush failed for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                    outcome = FlushOutcome.RetryChanges;
                }
                finally
                {
                    AfterFlush(outcome, changes, deletes, catchUp);
                }
            }

            // Re-queues or schedules whatever the flush left owing, and re-arms for events that arrived meanwhile.
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
                                // Put the batch back (events that arrived since win: they're newer), and try again later.
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
                            due = _debounce; // events arrived during processing — flush them after the debounce
                        }
                    }
                }

                if (gaveUp)
                {
                    _logger.LogWarning("Instant sync item '{Item}' (profile {ProfileId}): some files still couldn't be copied after {Attempts} catch-up passes; they'll be retried when they next change, or on Run now.",
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

            // Next retry delay: the base delay doubling per consecutive failure, capped. Caller holds _gate.
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
                    ? $"Instant Sync '{_item.Name}' — catch-up pass"
                    : $"Instant Sync '{_item.Name}' — {changeCount} change(s)";

                // Deferred: the log is only created once the processor writes its first line, so a flush
                // that turns out to be all no-ops (directory touch-events, files that vanished before the
                // flush ran) leaves no "synced N change(s) — 0 copied" noise behind.
                var log = new DeferredOperationLogger(_logFactory, title, profileId: _profileId);

                BackupResult result;
                try
                {
                    // A catch-up (events were lost, or files failed last time) reconciles the whole item, as does a
                    // remote target, which the local processor can't write incrementally. Otherwise the fast
                    // incremental path. (The source is always local here — remote sources aren't watched.)
                    result = catchUp || _targetConnectionId is not null
                        ? await ReconcileAsync(log, token)
                        : await _processor.ProcessBatchAsync(_item, changes, deletes, log, token);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    if (_failedFlushes++ == 0)
                    {
                        await log.ErrorAsync($"Instant sync '{_item.Name}' failed — it will be retried until it succeeds (further failures aren't logged)", ex);
                        await RecordRunAsync(startedUtc, stopwatch, new BackupResult { Errors = 1 }, RunOutcome.Failed, log.OperationLogId);
                        await log.SetSummaryAsync($"Instant Sync '{_item.Name}' failed in {FormatDuration(stopwatch.Elapsed)}", OperationLogLevel.Error);
                    }
                    else
                    {
                        _logger.LogWarning(ex, "Instant sync item '{Item}' (profile {ProfileId}): retry {Attempt} failed; retrying later.",
                            _item.Name, _profileId, _failedFlushes - 1);
                    }
                    return FlushOutcome.RetryChanges;
                }

                if (_failedFlushes > 0)
                {
                    // Closes the failure that was logged: the changes held back since then are going through now.
                    await log.AppendAsync($"Instant sync '{_item.Name}' is working again after {_failedFlushes} failed attempt(s)");
                    _failedFlushes = 0;
                }

                var owing = result.Errors > 0 || result.Warnings > 0 ? FlushOutcome.RetryWithCatchUp : FlushOutcome.Done;

                // Nothing was actually copied/deleted and no folders/errors were written — no log exists,
                // so there is nothing to summarise. Leave no entry for a no-op flush (and no run row).
                if (!log.WasCreated)
                {
                    return owing;
                }

                stopwatch.Stop();

                // Record the run before the summary write (whose ILogWatcher.Notify the dashboard refreshes
                // on) so the new row is visible when the dashboard reloads.
                var outcome = result.Errors > 0 ? RunOutcome.CompletedWithErrors
                    : result.Warnings > 0 ? RunOutcome.CompletedWithWarnings
                    : RunOutcome.Success;
                await RecordRunAsync(startedUtc, stopwatch, result, outcome, log.OperationLogId);

                var duration = FormatDuration(stopwatch.Elapsed);
                var counts = $"{result.Copied} copied, {result.Deleted} deleted";
                var what = catchUp ? "caught up" : $"synced {changeCount} change(s)";
                await log.SetSummaryAsync(
                    result.Errors == 0
                        ? $"Instant Sync '{_item.Name}' {what} in {duration} — {counts}"
                        : $"Instant Sync '{_item.Name}' completed with {result.Errors} error(s) in {duration} — {counts}",
                    result.Errors == 0 ? OperationLogLevel.Info : OperationLogLevel.Error);
                return owing;
            }

            // Records one BackupRun row for a flush that did real work, so live instant-sync activity shows
            // in the dashboard's Recent Runs (a no-op flush writes nothing and is never recorded).
            private async Task RecordRunAsync(DateTimeOffset startedUtc, Stopwatch stopwatch, BackupResult result, RunOutcome outcome, int operationLogId)
            {
                try
                {
                    await _runRecorder.RecordAsync(
                        _profileId, ProfileType.InstantSync, manual: false, startedUtc, stopwatch.Elapsed.TotalMilliseconds,
                        result, outcome, operationLogId == 0 ? null : operationLogId, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Could not record instant sync run for item '{Item}' (profile {ProfileId}).", _item.Name, _profileId);
                }
            }

            // Full reconcile of the item via the connection-aware folder-pair engine — for a target on a connection,
            // and for a catch-up pass. Instant sync is source-authoritative → always overwrite.
            private Task<BackupResult> ReconcileAsync(IOperationLogger log, CancellationToken token)
            {
                var pair = new OneWaySyncItem
                {
                    Name = _item.Name,
                    SourceFolder = _item.SourceFolder,
                    TargetFolder = _item.TargetFolder,
                    AllowDeletions = _item.AllowDeletions,
                    IncludeSubFolders = _item.IncludeSubFolders,
                    OverwriteBehaviour = OverwriteBehaviour.AlwaysOverwrite,
                };
                // Source is always local here (a remote source isn't watched); target is the profile connection.
                return _synchronizer.SyncAsync(pair, sourceConnectionId: null, _targetConnectionId, log, token);
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
                    disposeCtsNow = !_processing; // otherwise the running pass disposes it when it unwinds
                }

                try
                {
                    _cts.Cancel(); // stop a running pass — it would otherwise carry on with the old settings
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
