using BackupService.Enumerations;

namespace BackupService.Logging
{
    /// <summary>
    /// Creates <see cref="IOperationLogger"/> instances, each backed by a freshly-inserted
    /// <see cref="Database.OperationLog"/> record.
    /// </summary>
    public interface IOperationLogFactory
    {
        /// <summary>
        /// Inserts a new OperationLog record with the given name and severity (timestamped now)
        /// and returns a logger that appends detail lines to it.
        /// </summary>
        Task<IOperationLogger> CreateAsync(string name, OperationLogLevel level = OperationLogLevel.Info, CancellationToken cancellationToken = default);
    }
}
