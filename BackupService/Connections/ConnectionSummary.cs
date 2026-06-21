using BackupService.Enumerations;

namespace BackupService.Connections
{
    /// <summary>
    /// A minimal connection projection (id + name + type) for pickers (e.g. the folder-pair
    /// source/target location dropdowns).
    /// </summary>
    public sealed record ConnectionSummary(int Id, string Name, ConnectionType Type);
}
