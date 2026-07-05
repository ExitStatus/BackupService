using System.Globalization;
using System.Text;
using BackupService.Database;

namespace BackupService.Scheduling.TwoWaySync
{
    /// <summary>
    /// Default <see cref="ITwoWaySyncStateStore"/>. Stores each item's baseline as a small text file
    /// (<c>{itemId}.manifest</c>) under <c>{data dir}/two-way-sync</c>, one line per file:
    /// <c>{writeTimeUtcTicks}\t{size}\t{relativePath}</c>. The two numeric fields never contain a tab, so the
    /// path (which theoretically could, on Linux) is recovered by splitting into just three parts.
    /// </summary>
    public sealed class TwoWaySyncStateStore : ITwoWaySyncStateStore
    {
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

        public async Task<Dictionary<string, TwoWaySyncEntry>> LoadAsync(int itemId, CancellationToken cancellationToken = default)
        {
            var entries = new Dictionary<string, TwoWaySyncEntry>(StringComparer.OrdinalIgnoreCase);
            var path = PathFor(itemId);
            if (!File.Exists(path))
            {
                return entries;
            }

            foreach (var line in await File.ReadAllLinesAsync(path, cancellationToken))
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
                    continue; // skip a malformed/legacy line rather than failing the run
                }

                entries[parts[2]] = new TwoWaySyncEntry(ticks, size);
            }

            return entries;
        }

        public async Task SaveAsync(int itemId, IReadOnlyDictionary<string, TwoWaySyncEntry> entries, CancellationToken cancellationToken = default)
        {
            var builder = new StringBuilder(entries.Count * 40);
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
                // Best-effort — an orphaned manifest is harmless (it's keyed by an id that won't be reused).
            }
        }
    }
}
