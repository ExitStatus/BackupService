namespace BackupService.Hosting
{
    /// <summary>
    /// How the executable was asked to run, parsed from the command line.
    /// <list type="bullet">
    /// <item><see cref="Foreground"/> — no arguments: run the host attached to the terminal (console output to the
    /// terminal, Ctrl+C stops it). The internal <c>--worker</c> sentinel also runs the host, but redirects its console
    /// to the daily log files; <see cref="ApplicationMode.Parse"/> exposes that via its <c>isWorker</c> out-param.</item>
    /// <item><see cref="Background"/> — <c>-background</c>/<c>-bg</c>: relaunch a detached <c>--worker</c> child and exit.</item>
    /// <item><see cref="Stop"/> — <c>-stop</c>: signal a running background instance to shut down, then exit.</item>
    /// </list>
    /// </summary>
    public enum ApplicationMode
    {
        Foreground,
        Background,
        Stop,
    }

    /// <summary>
    /// Parses the process command line into an <see cref="ApplicationMode"/>. Pure (no side effects) so it can be
    /// unit-tested.
    /// </summary>
    public static class ApplicationModeParser
    {
        /// <summary>The internal flag the <c>-background</c> launcher passes to its detached child.</summary>
        public const string WorkerFlag = "--worker";

        /// <summary>
        /// Determines the run mode. <paramref name="isWorker"/> is set when the detached <c>--worker</c> child flag is
        /// present (a foreground-style host run that logs to files rather than the terminal). Matching is
        /// case-insensitive; unrecognised arguments fall through to <see cref="ApplicationMode.Foreground"/>.
        /// </summary>
        public static ApplicationMode Parse(string[] args, out bool isWorker)
        {
            isWorker = HasFlag(args, WorkerFlag);

            if (HasFlag(args, "-stop"))
            {
                return ApplicationMode.Stop;
            }

            if (HasFlag(args, "-background") || HasFlag(args, "-bg"))
            {
                return ApplicationMode.Background;
            }

            return ApplicationMode.Foreground;
        }

        private static bool HasFlag(string[] args, string flag) =>
            args.Any(a => string.Equals(a, flag, StringComparison.OrdinalIgnoreCase));
    }
}
