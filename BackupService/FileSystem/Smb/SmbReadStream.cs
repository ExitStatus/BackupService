using SMBLibrary;
using SMBLibrary.Client;

namespace BackupService.FileSystem.Smb
{
    /// <summary>
    /// Forward-only read stream over an open SMB file handle, fetching the file in <c>maxReadSize</c>
    /// chunks via <see cref="ISMBFileStore.ReadFile"/>. Closes the handle on dispose.
    /// </summary>
    internal sealed class SmbReadStream(ISMBFileStore store, object handle, int maxReadSize) : Stream
    {
        private long _position;
        private byte[] _buffer = [];
        private int _bufferPos;
        private bool _eof;
        private bool _disposed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] outputBuffer, int offset, int count)
        {
            if (_bufferPos >= _buffer.Length)
            {
                if (_eof || !FillBuffer())
                {
                    return 0;
                }
            }

            var available = _buffer.Length - _bufferPos;
            var toCopy = Math.Min(available, count);
            Array.Copy(_buffer, _bufferPos, outputBuffer, offset, toCopy);
            _bufferPos += toCopy;
            _position += toCopy;
            return toCopy;
        }

        private bool FillBuffer()
        {
            var status = store.ReadFile(out var data, handle, _position, Math.Max(1, maxReadSize));
            if (status == NTStatus.STATUS_END_OF_FILE || data is null || data.Length == 0)
            {
                _eof = true;
                _buffer = [];
                _bufferPos = 0;
                return false;
            }
            if (status != NTStatus.STATUS_SUCCESS)
            {
                throw new IOException($"SMB read failed ({status}).");
            }

            _buffer = data;
            _bufferPos = 0;
            return true;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

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
