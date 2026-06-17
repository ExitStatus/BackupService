using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// An <see cref="IOperationLogger"/> that defers creating the underlying <see cref="Database.OperationLog"/>
    /// until the first line is actually written, so a unit of work that turns out to do nothing leaves
    /// no log behind. Used by the instant-sync watcher: a debounced flush often contains only directory
    /// touch-events or files that vanished before the flush ran, producing no copies/deletes and so no
    /// lines — those should not show up as a noisy "synced N change(s) — 0 copied" entry.
    ///
    /// <para><see cref="WasCreated"/> reports whether anything was written. <see cref="SetSummaryAsync"/>
    /// is a no-op while nothing has been written (there is nothing to summarise), so the caller can call
    /// it unconditionally and still leave no trace for an empty flush.</para>
    /// </summary>
    public sealed class DeferredOperationLogger(
        IOperationLogFactory factory,
        string initialName,
        OperationLogLevel initialLevel = OperationLogLevel.Info,
        int? profileId = null) : IOperationLogger
    {
        private readonly object _lock = new();
        private Task<IOperationLogger>? _creation;

        /// <summary>True once a line has been written (and so the underlying log was actually created).</summary>
        public bool WasCreated => _creation is not null;

        public int OperationLogId =>
            _creation is { IsCompletedSuccessfully: true } created ? created.Result.OperationLogId : 0;

        public async Task AppendAsync(params string[] messages) =>
            await (await EnsureCreatedAsync()).AppendAsync(messages);

        public async Task AppendAsync(OperationLogLevel level, params string[] messages) =>
            await (await EnsureCreatedAsync()).AppendAsync(level, messages);

        public async Task ErrorAsync(string message, Exception? exception = null) =>
            await (await EnsureCreatedAsync()).ErrorAsync(message, exception);

        public async Task SetSummaryAsync(string message, OperationLogLevel level)
        {
            // Nothing was written — there is no log to summarise, and we deliberately don't create one.
            if (_creation is null)
            {
                return;
            }

            await (await EnsureCreatedAsync()).SetSummaryAsync(message, level);
        }

        private Task<IOperationLogger> EnsureCreatedAsync()
        {
            if (_creation is null)
            {
                // Cache the creation Task (not the logger) so concurrent first-writers share one create.
                lock (_lock)
                {
                    _creation ??= factory.CreateAsync(initialName, initialLevel, profileId);
                }
            }

            return _creation;
        }
    }
}
