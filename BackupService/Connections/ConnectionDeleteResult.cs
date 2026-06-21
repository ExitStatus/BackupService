namespace BackupService.Connections
{
    /// <summary>
    /// Outcome of a delete attempt. <see cref="Deleted"/> is false when the connection is still
    /// referenced (e.g. by a folder pair); <see cref="Error"/> then carries a friendly reason.
    /// </summary>
    public sealed record ConnectionDeleteResult(bool Deleted, string? Error)
    {
        public static ConnectionDeleteResult Success { get; } = new(true, null);

        public static ConnectionDeleteResult Blocked(string error) => new(false, error);
    }
}
