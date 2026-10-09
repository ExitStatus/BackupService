using BackupService.Connections.Usb;

namespace BackupService.Scheduling.Usb
{
    /// <summary>
    /// Works out which portable (MTP) devices arrived and which were removed from successive scans of the device
    /// list. The list isn't reliable from one scan to the next: a device that is still plugged in can be missing for
    /// a moment (mid-reset, or busy with another program). Treating that as a removal would log a disconnect and then,
    /// on the next scan, a fresh arrival that runs the device's profiles all over again. So a device only counts as
    /// removed once it has been missing from <see cref="RemovalConfirmations"/> scans in a row. Only feed it scans
    /// that completed — a failed one says nothing about which devices are present. Not thread-safe; the caller
    /// serialises calls.
    /// </summary>
    internal sealed class MtpPresenceTracker
    {
        internal const int RemovalConfirmations = 2;

        // Serial -> how many scans in a row it has been missing from.
        private readonly Dictionary<string, int> _known = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Records devices already connected at startup, so they don't count as arrivals.</summary>
        public void Seed(IEnumerable<MtpDevice> devices)
        {
            foreach (var device in devices)
            {
                _known[device.Serial] = 0;
            }
        }

        /// <summary>Whether a device has gone missing but not yet for long enough to count as removed.</summary>
        public bool HasUnconfirmedRemovals => _known.Values.Any(missed => missed > 0);

        /// <summary>Takes one completed scan, returning the devices that arrived and the serials now removed.</summary>
        public (IReadOnlyList<MtpDevice> Arrived, IReadOnlyList<string> Removed) Update(IEnumerable<MtpDevice> present)
        {
            var presentBySerial = new Dictionary<string, MtpDevice>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in present)
            {
                presentBySerial.TryAdd(device.Serial, device);
            }

            var removed = new List<string>();
            foreach (var serial in _known.Keys.ToList())
            {
                if (presentBySerial.ContainsKey(serial))
                {
                    _known[serial] = 0;
                    continue;
                }

                var missed = _known[serial] + 1;
                if (missed >= RemovalConfirmations)
                {
                    _known.Remove(serial);
                    removed.Add(serial);
                }
                else
                {
                    _known[serial] = missed;
                }
            }

            var arrived = presentBySerial.Values.Where(d => !_known.ContainsKey(d.Serial)).ToList();
            foreach (var device in arrived)
            {
                _known[device.Serial] = 0;
            }

            return (arrived, removed);
        }
    }
}
