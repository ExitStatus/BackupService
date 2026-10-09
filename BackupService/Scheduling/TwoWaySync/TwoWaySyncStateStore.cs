using System.Globalization;
using System.Text;
using BackupService.Database;

namespace BackupService.Scheduling.TwoWaySync
{
    /// <summary>
    /// Default <see cref="ITwoWaySyncStateStore"/>. Stores each item's baseline as a small text file
    /// (<c>{itemId}.manifest</c>) under <c>{data dir}/two-way-sync</c>. The first line is the endpoint stamp,
    /// <c>#endpoints\t{endpointKey}</c>; then one line per file: <c>{writeTimeUtcTicks}\t{size}\t{relativePath}</c>.
    /// The two numeric fields never contain a tab, so the path (which theoretically could, on Linux) is recovered by
    /// splitting into just three parts. A manifest without the stamp (written by an older version) is trusted once
    /// and stamped on the next save.
    /// </summary>
    public sealed class TwoWaySyncStateStore : ITwoWaySyncStateStore
    {
        private const string EndpointHeader = "#endpoints\t";

        private readonly string _root;

        public TwoWaySyncStateStore()
            : this(Path.Combine(BackupDatabaseLocation.GetDataDirectory(), "two-way-sync"))
        {
        }

        // Test seam: a caller-supplied root directory.
        public TwoWaySyncStateStore(string root)
        {
            _root = root;
            Directory.CreateDirectory(_root);
        }

        private string PathFor(int itemId) => Path.Combine(_root, $"{itemId}.manifest");

        public async Task<TwoWaySyncBaseline> LoadAsync(int itemId, string endpointKey, CancellationToken cancellationToken = default)
        {
            var entries = new Dictionary<string, TwoWaySyncEntry>(StringComparer.OrdinalIgnoreCase);
            var path = PathFor(itemId);
            if (!File.Exists(path))
            {
                return new TwoWaySyncBaseline(entries, WasReset: false);
            }

            var lines = await File.ReadAllLinesAsync(path, cancellationToken);
            var stamped = lines.Length > 0 && lines[0].StartsWith(EndpointHeader, StringComparison.Ordinal);
            if (stamped && !string.Equals(lines[0][EndpointHeader.Length..], endpointKey, StringComparison.Ordinal))
            {
                // Built for another folder pair — its state says nothing about this one.
                return new TwoWaySyncBaseline(entries, WasReset: true);
            }

            // An unstamped manifest predates the stamp. It's trusted once (deliberately: discarding it would bring
            // back every file deleted since the last sync) and gets stamped when this run saves.
            foreach (var line in stamped ? lines.Skip(1) : lines)
            {
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var parts = line.Split('\t', 3);
                if (parts.Length != 3
                    || !long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                    || !long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var size))
                {
                    continue; // skip a malformed line rather than failing the run
                }

                entries[parts[2]] = new TwoWaySyncEntry(ticks, size);
            }

            return new TwoWaySyncBaseline(entries, WasReset: false);
        }

        public async Task SaveAsync(int itemId, string endpointKey, IReadOnlyDictionary<string, TwoWaySyncEntry> entries, CancellationToken cancellationToken = default)
        {
            var builder = new StringBuilder((entries.Count + 1) * 40);
            builder.Append(EndpointHeader).Append(endpointKey).Append('\n');
            foreach (var (relativePath, entry) in entries)
            {
                builder.Append(entry.WriteTimeUtcTicks.ToString(CultureInfo.InvariantCulture))
                    .Append('\t')
                    .Append(entry.Size.ToString(CultureInfo.InvariantCulture))
                    .Append('\t')
                    .Append(relativePath)
                    .Append('\n');
            }

            // Atomic replace: write a temp file then move it over the target so a crash can't leave a
            // half-written baseline (which would make the next run mis-classify every file).
            var finalPath = PathFor(itemId);
            var tempPath = finalPath + ".tmp";
            await File.WriteAllTextAsync(tempPath, builder.ToString(), cancellationToken);
            File.Move(tempPath, finalPath, overwrite: true);
        }

        public void Delete(int itemId)
        {
            try
            {
                var path = PathFor(itemId);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort — an orphaned manifest is harmless: it's stamped with its folder pair, so even an
                // item that later reuses the id (e.g. after a database restore) won't trust it.
            }
        }
    }
}
