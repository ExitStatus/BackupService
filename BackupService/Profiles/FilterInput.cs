using BackupService.Enumerations;

namespace BackupService.Profiles
{
    /// <summary>
    /// One include/exclude filter rule supplied when creating or updating a folder pair or archive item.
    /// <see cref="Id"/> is 0 for a new rule, or the existing filter row's id when updating one.
    /// </summary>
    public sealed record FilterInput(int Id, FilterDirection Direction, FilterKind Kind, string Pattern);
}
