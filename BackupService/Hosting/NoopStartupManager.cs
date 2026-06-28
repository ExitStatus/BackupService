namespace BackupService.Hosting
{
    /// <summary>No-op <see cref="IStartupManager"/> used off Windows (autostart is a Windows-only feature).</summary>
    public sealed class NoopStartupManager : IStartupManager
    {
        public void Apply(bool enabled)
        {
            // No autostart mechanism on this platform.
        }

        public bool IsEnabled() => false;
    }
}
