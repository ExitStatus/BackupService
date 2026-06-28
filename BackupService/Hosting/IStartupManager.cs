namespace BackupService.Hosting
{
    /// <summary>
    /// Registers (or removes) the app's "start when the user signs in" entry. On Windows this is the
    /// per-user <c>HKCU\…\Run</c> registry value launching the exe in <c>-background</c> mode; off Windows
    /// it is a no-op (<see cref="NoopStartupManager"/>).
    /// </summary>
    public interface IStartupManager
    {
        /// <summary>Adds the autostart entry when <paramref name="enabled"/>, otherwise removes it. Idempotent.</summary>
        void Apply(bool enabled);

        /// <summary>True when the autostart entry is currently registered.</summary>
        bool IsEnabled();
    }
}
