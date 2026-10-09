using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Default <see cref="IOperationLogFileStore"/>. Writes each log to <c>{directory}\{id}.log</c>. Appends
    /// are serialised per log id (a per-id <see cref="SemaphoreSlim"/>) so concurrent writers to the same log
    /// don't interleave. A far cheaper unit of work than the old per-line database insert (a file append vs a
    /// SQLite transaction), which is the point of the file-based design.
    ///
    /// <para>Line format: <c>[Level][yyyy-MM-dd HH:mm:ss] message</c>, where the timestamp is local wall-clock
    /// (matching the terminal display and the daily rolling log convention). A message containing newlines is
    /// written verbatim, so its extra physical lines have no prefix; on read, a physical line that does not
    /// start with a recognised prefix is treated as a continuation of the previous line's message.</para>
    /// </summary>
    public sealed partial class OperationLogFileStore : IOperationLogFileStore
    {
        private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss";

        /// <summary>The name of the log-file folder under the data directory.</summary>
        public const string DirectoryName = "operation-logs";

        private readonly string _directory;
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

        public OperationLogFileStore(string directory)
        {
            _directory = directory;
            Directory.CreateDirectory(_directory);
        }

        /// <summary>The directory the log files live in.</summary>
        public string RootDirectory => _directory;

        public string FileNameFor(int operationLogId) => $"{operationLogId}.log";

        private string PathFor(int operationLogId) => Path.Combine(_directory, FileNameFor(operationLogId));

        public async Task AppendAsync(int operationLogId, OperationLogLevel level, IReadOnlyList<string> messages, CancellationToken cancellationToken = default)
        {
            if (messages.Count == 0)
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var builder = new StringBuilder();
            foreach (var message in messages)
            {
                builder.Append(FormatLine(level, now, message)).Append('\n');
            }

            await AppendRawAsync(operationLogId, builder.ToString(), cancellationToken);
        }

        public async Task WriteAllAsync(int operationLogId, IReadOnlyList<OperationLogLine> lines, CancellationToken cancellationToken = default)
        {
            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                builder.Append(FormatLine(line.Level, line.Timestamp, line.Message)).Append('\n');
            }

            var semaphore = _locks.GetOrAdd(operationLogId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await WriteSharedAsync(PathFor(operationLogId), FileMode.Create, builder.ToString(), cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        public async Task<IReadOnlyList<OperationLogLine>> ReadAsync(int operationLogId, CancellationToken cancellationToken = default)
        {
            var path = PathFor(operationLogId);
            if (!File.Exists(path))
            {
                return [];
            }

            string[] physicalLines;
            try
            {
                physicalLines = await ReadAllLinesSharedAsync(path, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return [];
            }

            return Parse(physicalLines);
        }

        public async Task<IReadOnlyList<OperationLogLine>> ReadTailAsync(int operationLogId, int maxLines, CancellationToken cancellationToken = default)
            => (await ReadWindowAsync(operationLogId, skip: null, take: maxLines, cancellationToken)).Lines;

        public async Task<OperationLogWindow> ReadWindowAsync(int operationLogId, int? skip, int take, CancellationToken cancellationToken = default)
        {
            var path = PathFor(operationLogId);
            if (take <= 0 || !File.Exists(path))
            {
                return OperationLogWindow.Empty;
            }

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LogFileShare, bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream);

                // One streaming pass gathers the window AND the whole-file facts (total + per-level
                // counts); at most `take` lines are ever held. A null skip keeps the tail (a bounded
                // queue); a set skip keeps [skip, skip+take).
                var tail = skip is null ? new Queue<OperationLogLine>() : null;
                var range = skip is null ? null : new List<OperationLogLine>(take);
                int total = 0, warnings = 0, errors = 0;

                await foreach (var line in ParseStreamAsync(reader, cancellationToken))
                {
                    if (line.Level == OperationLogLevel.Warning)
                    {
                        warnings++;
                    }
                    else if (line.Level == OperationLogLevel.Error)
                    {
                        errors++;
                    }

                    if (tail is not null)
                    {
                        tail.Enqueue(line);
                        if (tail.Count > take)
                        {
                            tail.Dequeue();
                        }
                    }
                    else if (total >= skip!.Value && range!.Count < take)
                    {
                        range.Add(line);
                    }

                    total++;
                }

                IReadOnlyList<OperationLogLine> lines = tail is not null ? [.. tail] : range!;
                var startIndex = skip is null ? total - lines.Count : Math.Min(skip.Value, total);
                return new OperationLogWindow(lines, startIndex, total, warnings, errors);
            }
            catch (FileNotFoundException)
            {
                return OperationLogWindow.Empty;
            }
        }

        public async Task<OperationLogSearch> SearchAsync(
            int operationLogId, string? text, IReadOnlyCollection<OperationLogLevel>? levels, int maxMatches, CancellationToken cancellationToken = default)
        {
            var path = PathFor(operationLogId);
            if (maxMatches <= 0 || !File.Exists(path))
            {
                return OperationLogSearch.Empty;
            }

            var trimmed = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            var levelFilter = levels is { Count: > 0 } ? levels : null;

            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LogFileShare, bufferSize: 4096, useAsync: true);
                using var reader = new StreamReader(stream);

                // Streaming grep: count every match but keep only the first `maxMatches` lines in memory.
                var matches = new List<OperationLogLine>();
                var totalMatches = 0;

                await foreach (var line in ParseStreamAsync(reader, cancellationToken))
                {
                    if (trimmed is not null && !line.Message.Contains(trimmed, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (levelFilter is not null && !levelFilter.Contains(line.Level))
                    {
                        continue;
                    }

                    totalMatches++;
                    if (matches.Count < maxMatches)
                    {
                        matches.Add(line);
                    }
                }

                return new OperationLogSearch(matches, totalMatches);
            }
            catch (FileNotFoundException)
            {
                return OperationLogSearch.Empty;
            }
        }

        public async Task<bool> ContainsAsync(int operationLogId, string text, CancellationToken cancellationToken = default)
        {
            var path = PathFor(operationLogId);
            if (!File.Exists(path) || string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                foreach (var line in await ReadAllLinesSharedAsync(path, cancellationToken))
                {
                    if (line.Contains(text, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (FileNotFoundException)
            {
                // Raced with a delete — treat as no match.
            }

            return false;
        }

        public void Delete(int operationLogId)
        {
            try
            {
                File.Delete(PathFor(operationLogId));
            }
            catch (IOException)
            {
                // Best-effort — a locked/absent file is not fatal.
            }
            catch (UnauthorizedAccessException)
            {
            }
            finally
            {
                _locks.TryRemove(operationLogId, out _);
            }
        }

        public void DeleteAll()
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        private async Task AppendRawAsync(int operationLogId, string content, CancellationToken cancellationToken)
        {
            var semaphore = _locks.GetOrAdd(operationLogId, _ => new SemaphoreSlim(1, 1));
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await WriteSharedAsync(PathFor(operationLogId), FileMode.Append, content, cancellationToken);
            }
            finally
            {
                semaphore.Release();
            }
        }

        // Read/write share the file with FileShare.ReadWrite|Delete so a live tail (the View Progress dialog
        // or the Logs terminal) reading a log doesn't collide with the running backup appending to it — the
        // default File.Read*/Append* helpers open with FileShare.Read, which throws "being used by another
        // process" when one side holds the file while the other opens it. Writes are still serialised per log
        // id by the caller's semaphore; the permissive share only lets a concurrent reader coexist.
        private const FileShare LogFileShare = FileShare.ReadWrite | FileShare.Delete;

        private static async Task WriteSharedAsync(string path, FileMode mode, string content, CancellationToken cancellationToken)
        {
            // UTF-8 without a BOM, matching the old File.AppendAllText/WriteAllText behaviour.
            var bytes = Encoding.UTF8.GetBytes(content);
            await using var stream = new FileStream(path, mode, FileAccess.Write, LogFileShare, bufferSize: 4096, useAsync: true);
            await stream.WriteAsync(bytes, cancellationToken);
        }

        private static async Task<string[]> ReadAllLinesSharedAsync(string path, CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, LogFileShare, bufferSize: 4096, useAsync: true);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                lines.Add(line);
            }
            return lines.ToArray();
        }

        /// <summary>Builds one prefixed record. The message is written verbatim (embedded newlines included).</summary>
        private static string FormatLine(OperationLogLevel level, DateTimeOffset timestamp, string message) =>
            $"[{level}][{timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture)}] {message}";

        /// <summary>
        /// Reconstructs lines from a file's physical lines: a line matching the <c>[Level][timestamp]</c>
        /// prefix starts a new record; any other physical line is appended (with a newline) to the current
        /// record's message, restoring messages that originally contained embedded newlines.
        /// </summary>
        private static List<OperationLogLine> Parse(IReadOnlyList<string> physicalLines)
        {
            var result = new List<OperationLogLine>();

            OperationLogLevel level = OperationLogLevel.Info;
            DateTimeOffset timestamp = default;
            StringBuilder? message = null;

            void Flush()
            {
                if (message is not null)
                {
                    result.Add(new OperationLogLine(level, timestamp, message.ToString()));
                    message = null;
                }
            }

            foreach (var physical in physicalLines)
            {
                var match = PrefixRegex().Match(physical);
                if (match.Success)
                {
                    Flush();
                    level = Enum.Parse<OperationLogLevel>(match.Groups["level"].Value);
                    timestamp = ParseTimestamp(match.Groups["time"].Value);
                    message = new StringBuilder(match.Groups["msg"].Value);
                }
                else if (message is not null)
                {
                    // Continuation of the current record's (multi-line) message.
                    message.Append('\n').Append(physical);
                }
                else
                {
                    // A leading line with no recognised prefix (shouldn't normally happen) — keep it as
                    // an Info line rather than dropping it.
                    level = OperationLogLevel.Info;
                    timestamp = default;
                    message = new StringBuilder(physical);
                }
            }

            Flush();
            return result;
        }

        /// <summary>
        /// Streams a log file's physical lines, reconstructing and yielding logical lines exactly as
        /// <see cref="Parse"/> does — one at a time, so callers (tail, window, search) can process a huge
        /// log without ever materialising it fully in memory.
        /// </summary>
        private static async IAsyncEnumerable<OperationLogLine> ParseStreamAsync(
            StreamReader reader, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            OperationLogLevel level = OperationLogLevel.Info;
            DateTimeOffset timestamp = default;
            StringBuilder? message = null;

            string? physical;
            while ((physical = await reader.ReadLineAsync(cancellationToken)) is not null)
            {
                var match = PrefixRegex().Match(physical);
                if (match.Success)
                {
                    if (message is not null)
                    {
                        yield return new OperationLogLine(level, timestamp, message.ToString());
                    }
                    level = Enum.Parse<OperationLogLevel>(match.Groups["level"].Value);
                    timestamp = ParseTimestamp(match.Groups["time"].Value);
                    message = new StringBuilder(match.Groups["msg"].Value);
                }
                else if (message is not null)
                {
                    // Continuation of the current record's (multi-line) message.
                    message.Append('\n').Append(physical);
                }
                else
                {
                    // A leading line with no recognised prefix — keep it as an Info line rather than dropping it.
                    level = OperationLogLevel.Info;
                    timestamp = default;
                    message = new StringBuilder(physical);
                }
            }

            if (message is not null)
            {
                yield return new OperationLogLine(level, timestamp, message.ToString());
            }
        }

        private static DateTimeOffset ParseTimestamp(string value) =>
            DateTime.TryParseExact(value, TimestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
                ? new DateTimeOffset(local, TimeZoneInfo.Local.GetUtcOffset(local))
                : default;

        [GeneratedRegex(@"^\[(?<level>Info|Warning|Error|Debug)\]\[(?<time>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2})\] (?<msg>.*)$")]
        private static partial Regex PrefixRegex();
    }
}
