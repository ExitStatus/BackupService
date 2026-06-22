namespace BackupService.Enumerations
{
    /// <summary>
    /// Which Windows account the service is installed to run under, selected by the
    /// <c>--server</c> / <c>--user</c> command-line switch on <c>--install</c>.
    /// </summary>
    public enum ServiceAccountKind
    {
        /// <summary>
        /// The built-in <c>LocalSystem</c> account (machine-wide, no per-user resources such as
        /// the current user's OneDrive). This is the original install behaviour.
        /// </summary>
        Server,

        /// <summary>
        /// The current interactive user, so per-user resources (e.g. OneDrive folders) are reachable.
        /// Requires that user's password and grants it the "Log on as a service" right.
        /// </summary>
        User,
    }
}
