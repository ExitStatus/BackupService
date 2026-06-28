using BackupService.Database;
using Microsoft.EntityFrameworkCore;

namespace BackupService.Options
{
    /// <summary>
    /// Default <see cref="IAppOptionsService"/>. Reads/writes the single <see cref="AppOptions"/> row via the
    /// DbContext factory, mirroring <c>LogRetentionService</c>'s lazy-seed pattern.
    /// </summary>
    public sealed class AppOptionsService(IDatabaseContextFactory contextFactory) : IAppOptionsService
    {
        public event Action<AppOptions>? Changed;

        public async Task<AppOptions> GetSettingsAsync(CancellationToken cancellationToken = default)
        {
            await using var db = contextFactory.CreateDbContext();
            return await GetOrSeedAsync(db, cancellationToken);
        }

        public async Task UpdateSettingsAsync(bool startWithWindows, bool showTrayIcon, bool allowNotifications, CancellationToken cancellationToken = default)
        {
            await using (var db = contextFactory.CreateDbContext())
            {
                var settings = await GetOrSeedAsync(db, cancellationToken);
                settings.StartWithWindows = startWithWindows;
                settings.ShowTrayIcon = showTrayIcon;
                settings.AllowNotifications = allowNotifications;
                await db.SaveChangesAsync(cancellationToken);
            }

            // Hand subscribers a detached snapshot so they don't need to re-query (avoids a deadlock when the
            // event fires on the Blazor circuit thread).
            Changed?.Invoke(new AppOptions
            {
                StartWithWindows = startWithWindows,
                ShowTrayIcon = showTrayIcon,
                AllowNotifications = allowNotifications,
            });
        }

        private static async Task<AppOptions> GetOrSeedAsync(BackupDbContext db, CancellationToken cancellationToken)
        {
            var settings = await db.AppOptions.FirstOrDefaultAsync(cancellationToken);
            if (settings is null)
            {
                settings = new AppOptions();
                db.AppOptions.Add(settings);
                await db.SaveChangesAsync(cancellationToken);
            }

            return settings;
        }
    }
}
