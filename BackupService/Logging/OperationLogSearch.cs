namespace BackupService.Logging
{
    /// <summary>
    /// The result of a streaming search over one operation log's file: the matching lines in file order
    /// (capped by the caller's max), and the <b>total</b> number of matches in the whole file — when
    /// <see cref="Truncated"/>, only the first <c>Matches.Count</c> of <see cref="TotalMatches"/> are here.
    /// </summary>
    public sealed record OperationLogSearch(IReadOnlyList<OperationLogLine> Matches, int TotalMatches)
    {
        /// <summary>An empty result (missing file / nothing matched).</summary>
        public static readonly OperationLogSearch Empty = new([], 0);

        /// <summary>Whether matches beyond the returned cap exist in the file.</summary>
        public bool Truncated => Matches.Count < TotalMatches;
    }
}
