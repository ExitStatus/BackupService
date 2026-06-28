using System.Globalization;
using System.Text;

namespace BackupService.Hosting
{
    /// <summary>
    /// A <see cref="TextWriter"/> that captures console output to a daily-rolling log file under a logs directory —
    /// <c>backupservice-{yyyy-MM-dd}.log</c>, one file per local calendar day. Used by the detached <c>--worker</c>
    /// process: <c>Console.SetOut</c>/<c>Console.SetError</c> are pointed at it, so all <c>ILogger</c>/console output
    /// (the default console logger writes to <c>Console.Out</c>) lands in the file rather than a terminal.
    ///
    /// Writes are lock-guarded and auto-flushed (a line survives a crash). When the local date ticks over the writer
    /// rolls to a new file and purges files older than the retention window; the same purge runs once at construction.
    /// </summary>
    public sealed class DailyRollingLogWriter : TextWriter
    {
        private const string FilePrefix = "backupservice-";
        private const string FileExtension = ".log";
        private const string DateFormat = "yyyy-MM-dd";

        private readonly string _logsDirectory;
        private readonly int _retentionDays;
        private readonly object _gate = new();

        private StreamWriter? _writer;
        private DateOnly _currentDay;

        private DailyRollingLogWriter(string logsDirectory, int retentionDays)
        {
            _logsDirectory = logsDirectory;
            _retentionDays = retentionDays;
        }

        /// <summary>
        /// Creates the logs directory if missing, purges expired files, opens today's file and returns the writer.
        /// </summary>
        public static DailyRollingLogWriter Start(string logsDirectory, int retentionDays)
        {
            Directory.CreateDirectory(logsDirectory);
            var writer = new DailyRollingLogWriter(logsDirectory, retentionDays);
            writer.PurgeExpired();
            writer.Roll(DateOnly.FromDateTime(DateTime.Now));
            return writer;
        }

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (_gate)
            {
                EnsureCurrentDay();
                _writer!.Write(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                EnsureCurrentDay();
                _writer!.Write(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_gate)
            {
                EnsureCurrentDay();
                _writer!.WriteLine(value);
            }
        }

        public override void Flush()
        {
            lock (_gate)
            {
                _writer?.Flush();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    _writer?.Flush();
                    _writer?.Dispose();
                    _writer = null;
                }
            }

            base.Dispose(disposing);
        }

        // Rolls to a new day's file when the calendar date has advanced (and purges expired files on the roll).
        private void EnsureCurrentDay()
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            if (_writer is null || today != _currentDay)
            {
                Roll(today);
                PurgeExpired();
            }
        }

        private void Roll(DateOnly day)
        {
            _writer?.Flush();
            _writer?.Dispose();

            var path = Path.Combine(_logsDirectory, $"{FilePrefix}{day.ToString(DateFormat, CultureInfo.InvariantCulture)}{FileExtension}");
            _writer = new StreamWriter(path, append: true, Encoding.UTF8) { AutoFlush = true };
            _currentDay = day;
        }

        private void PurgeExpired()
        {
            try
            {
                var files = Directory.EnumerateFiles(_logsDirectory, $"{FilePrefix}*{FileExtension}");
                foreach (var path in SelectExpiredLogFiles(files, DateOnly.FromDateTime(DateTime.Now), _retentionDays))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch
                    {
                        // Best effort — a file held open (e.g. today's) is simply skipped.
                    }
                }
            }
            catch
            {
                // Never let log housekeeping break the run.
            }
        }

        /// <summary>
        /// Pure selection of which daily log files have aged out: those whose encoded date is more than
        /// <paramref name="retentionDays"/> days before <paramref name="today"/>. Files whose names don't match the
        /// <c>backupservice-{yyyy-MM-dd}.log</c> pattern are ignored (never selected for deletion). Extracted for unit
        /// testing without touching the filesystem.
        /// </summary>
        public static IEnumerable<string> SelectExpiredLogFiles(IEnumerable<string> files, DateOnly today, int retentionDays)
        {
            var cutoff = today.AddDays(-retentionDays);
            foreach (var path in files)
            {
                var name = Path.GetFileName(path);
                if (!name.StartsWith(FilePrefix, StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(FileExtension, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var datePart = name.Substring(FilePrefix.Length, name.Length - FilePrefix.Length - FileExtension.Length);
                if (DateOnly.TryParseExact(datePart, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var fileDate)
                    && fileDate < cutoff)
                {
                    yield return path;
                }
            }
        }
    }
}
