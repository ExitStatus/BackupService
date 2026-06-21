namespace BackupService.FileSystem
{
    /// <summary>Byte-for-byte comparison of two forward-only streams (used for cross-filesystem content checks).</summary>
    public static class StreamCompare
    {
        private const int BufferSize = 64 * 1024;

        public static bool Equal(Stream a, Stream b)
        {
            var bufferA = new byte[BufferSize];
            var bufferB = new byte[BufferSize];

            while (true)
            {
                var readA = ReadBlock(a, bufferA);
                var readB = ReadBlock(b, bufferB);
                if (readA != readB)
                {
                    return false;
                }
                if (readA == 0)
                {
                    return true;
                }
                if (!bufferA.AsSpan(0, readA).SequenceEqual(bufferB.AsSpan(0, readB)))
                {
                    return false;
                }
            }
        }

        // Reads up to a full buffer, tolerating partial reads; returns the count read (0 at EOF).
        private static int ReadBlock(Stream stream, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }
                total += read;
            }
            return total;
        }
    }
}
