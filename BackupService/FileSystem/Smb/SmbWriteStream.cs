using SMBLibrary;
using SMBLibrary.Client;

namespace BackupService.FileSystem.Smb
{
    /// <summary>
    /// Forward-only write stream over an open SMB file handle, sending data in <c>maxWriteSize</c> chunks
    /// via <see cref="ISMBFileStore.WriteFile"/>. Closes the handle on dispose.
    /// </summary>
    internal sealed class SmbWriteStream(ISMBFileStore store, object handle, int maxWriteSize) : Stream
    {
        private long _position;
        private bool _disposed;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _position;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count)
        {
            var chunk = Math.Max(1, maxWriteSize);
            var written = 0;
            while (written < count)
            {
                var size = Math.Min(chunk, count - written);
                var slice = new byte[size];
                Array.Copy(buffer, offset + written, slice, 0, size);

                var status = store.WriteFile(out var bytesWritten, handle, _position, slice);
                if (status != NTStatus.STATUS_SUCCESS)
                {
                    throw new IOException($"SMB write failed ({status}).");
                }
                if (bytesWritten <= 0)
                {
                    throw new IOException("SMB write made no progress.");
                }

                _position += bytesWritten;
                written += bytesWritten;
            }
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                try { store.CloseFile(handle); } catch { /* best-effort */ }
            }
            base.Dispose(disposing);
        }
    }
}
