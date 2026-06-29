using System.ComponentModel;

namespace BackupService.Enumerations
{
    /// <summary>
    /// The kind of remote resource a <see cref="BackupService.Database.Connection"/> reaches.
    /// Extensible — add a value here plus a settings table and a type-specific editor.
    /// </summary>
    public enum ConnectionType
    {
        [Description("SMB")]
        Smb = 0,

        [Description("Google Drive")]
        GoogleDrive = 1,

        [Description("USB")]
        Usb = 2,
    }
}
