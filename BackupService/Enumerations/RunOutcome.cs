using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>
    /// The result of a single discrete backup run (recorded in <c>Database.BackupRun</c> for the
    /// dashboard). Derived per run from whether the handler failed catastrophically and whether any
    /// per-file/item errors occurred.
    /// </summary>
    public enum RunOutcome
    {
        /// <summary>The run completed with no errors or warnings.</summary>
        [Description("Success")]
        Success = 0,

        /// <summary>The run completed, but one or more files/items errored along the way.</summary>
        [Description("Completed with errors")]
        CompletedWithErrors = 1,

        /// <summary>The run failed catastrophically (aborted before/within the per-item loop).</summary>
        [Description("Failed")]
        Failed = 2,

        /// <summary>
        /// The run completed with no hard errors, but one or more files were skipped as a non-fatal
        /// warning (e.g. locked / in use by another process).
        /// </summary>
        [Description("Completed with warnings")]
        CompletedWithWarnings = 3,
    }
}
