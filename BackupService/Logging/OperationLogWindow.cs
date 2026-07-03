namespace BackupService.Logging
{
    /// <summary>
    /// A contiguous window of one operation log's lines, plus whole-file facts gathered on the same
    /// streaming pass: <paramref name="StartIndex"/> is the 0-based logical index of the first returned
    /// line, <paramref name="TotalCount"/> the file's full logical line count, and
    /// <paramref name="WarningCount"/>/<paramref name="ErrorCount"/> the file-wide per-level counts
    /// (so the UI's level toggles can label themselves accurately without loading the whole file).
    /// </summary>
    public sealed record OperationLogWindow(
        IReadOnlyList<OperationLogLine> Lines,
        int StartIndex,
        int TotalCount,
        int WarningCount,
        int ErrorCount)
    {
        /// <summary>An empty window (missing file / nothing to read).</summary>
        public static readonly OperationLogWindow Empty = new([], 0, 0, 0, 0);

        /// <summary>Whether lines exist before this window (older lines can still be loaded).</summary>
        public bool HasEarlier => StartIndex > 0;
    }
}
