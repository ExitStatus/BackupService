using BackupService.Logging;

namespace BackupService.UnitTests.Logging
{
    /// <summary>
    /// A <see cref="OperationLogFileStore"/> rooted at a fresh temp directory for a single test, cleaned up
    /// on <see cref="Dispose"/>. Operation-log detail lines are stored in files now (not the database), so
    /// tests that exercise logging construct one of these, pass <see cref="Store"/> to the logger/factory,
    /// and read the written lines back via <see cref="Store"/>.
    /// </summary>
    public sealed class TempLogStore : IDisposable
    {
        private readonly string _directory;

        public TempLogStore()
        {
            _directory = Path.Combine(Path.GetTempPath(), "bs-logtests-" + Guid.NewGuid().ToString("N"));
            Store = new OperationLogFileStore(_directory);
        }

        public OperationLogFileStore Store { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_directory))
                {
                    Directory.Delete(_directory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }
}
