using BackupService.Database;

namespace BackupService.Options
{
    /// <summary>
    /// Owns the single-row <see cref="AppOptions"/> (the Settings → Options toggles): start-with-Windows,
    /// show-tray-icon, allow-notifications. Singleton. Raises <see cref="Changed"/> after a save so the tray
    /// service can refresh its cached state.
    /// </summary>
    public interface IAppOptionsService
    {
        /// <summary>Current options, lazily seeding the default row (all off) if none exists.</summary>
        Task<AppOptions> GetSettingsAsync(CancellationToken cancellationToken = default);

        /// <summary>Persists the three option flags, then raises <see cref="Changed"/> with the saved values.</summary>
        Task UpdateSettingsAsync(bool startWithWindows, bool showTrayIcon, bool allowNotifications, CancellationToken cancellationToken = default);

        /// <summary>
        /// Raised after the options are saved, carrying the saved values. The payload lets subscribers (e.g. the
        /// tray service) react without a sync-over-async re-read — the event fires on the caller's thread (the
        /// Blazor circuit), where blocking on a DB query would deadlock.
        /// </summary>
        event Action<AppOptions>? Changed;
    }
}
