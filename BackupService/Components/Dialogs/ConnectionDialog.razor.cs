using System.ComponentModel.DataAnnotations;
using BackupService.Components.Controls;
using BackupService.Connections;
using BackupService.Connections.GoogleDrive;
using BackupService.Enumerations;
using BackupService.Extensions;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Dialogs
{
    /// <summary>
    /// Self-contained modal for creating or editing a connection. With no <see cref="ConnectionId"/>
    /// it creates; with one it loads that connection and saves changes. The connection type selects
    /// which editor is shown and is fixed once created (mirrors <see cref="ProfileDialog"/>).
    /// </summary>
    public partial class ConnectionDialog : ComponentBase
    {
        [Inject]
        private IConnectionService ConnectionService { get; set; } = default!;

        [Inject]
        private GoogleDriveAppCredentials GoogleDriveAppCredentials { get; set; } = default!;

        /// <summary>When set, the dialog edits this connection; otherwise it creates a new one.</summary>
        [Parameter]
        public int? ConnectionId { get; set; }

        [Parameter]
        public EventCallback OnCancel { get; set; }

        [Parameter]
        public EventCallback OnSaved { get; set; }

        private InputModel Input { get; set; } = new();
        private readonly SmbConnectionEditor.SmbEditModel _smb = new();
        private SmbConnectionEditor? _smbEditor;
        private readonly GoogleDriveConnectionEditor.GoogleDriveEditModel _googleDrive = new();
        private GoogleDriveConnectionEditor? _googleDriveEditor;
        private readonly UsbConnectionEditor.UsbEditModel _usb = new();
        private UsbConnectionEditor? _usbEditor;

        private bool IsEdit => ConnectionId.HasValue;

        private static readonly IReadOnlyList<TabBar.TabItem> _usbTabs =
        [
            new("details", "Details"),
            new("notifications", "Notifications"),
        ];
        private string _activeTab = "details";

        // Both USB tab panels stay in the DOM (so the editor's @ref/poll-timer persist); the inactive one is hidden.
        private string TabStyle(string key) => _activeTab == key ? string.Empty : "display:none";

        // Google Drive is only offered once the admin has configured the app's built-in OAuth client, so a
        // user never lands on a Client ID field; without it, only the locally-configurable types are shown.
        private IReadOnlyList<ConnectionType> AvailableTypes =>
            GoogleDriveAppCredentials.IsConfigured
                ? Enum.GetValues<ConnectionType>()
                : Enum.GetValues<ConnectionType>().Where(t => t != ConnectionType.GoogleDrive).ToArray();

        private string DialogTitle => IsEdit
            ? $"Edit {Input.Type.GetDescription()} Connection"
            : "Create Connection";

        private string IntroText => Input.Type switch
        {
            ConnectionType.GoogleDrive => "A connection to a Google Drive account (authorised via Google) so it can be used as a backup source or target.",
            ConnectionType.Usb => "A connection bound to a specific USB device. It's valid only while that device is plugged in; a profile that uses it as a source runs automatically when the device is connected.",
            _ => "A connection points at a remote resource (such as an SMB share) so it can be used as a backup source or target.",
        };

        protected override async Task OnInitializedAsync()
        {
            if (ConnectionId is not { } id)
            {
                return;
            }

            var connection = await ConnectionService.GetAsync(id);
            if (connection is null)
            {
                return;
            }

            Input.Name = connection.Name;
            Input.Type = connection.Type;

            if (connection.Smb is { } smb)
            {
                _smb.Host = smb.Host;
                _smb.Port = smb.Port;
                _smb.Share = smb.ShareName;
                _smb.Domain = smb.Domain;
                _smb.Username = smb.Username;
                _smb.Password = null; // never round-trip the stored secret; blank = keep
                _smb.RootFolder = smb.RootFolder;
            }

            if (connection.GoogleDrive is { } googleDrive)
            {
                _googleDrive.UseBuiltInClient = googleDrive.UsesBuiltInClient;
                _googleDrive.ClientId = googleDrive.ClientId;
                _googleDrive.ClientSecret = null; // never round-trip the stored secret; blank = keep
                _googleDrive.RefreshToken = null; // blank = keep the stored authorization
                _googleDrive.AccountEmail = googleDrive.AccountEmail;
                _googleDrive.RootFolder = googleDrive.RootFolder;
                _googleDrive.HasStoredAuth = !string.IsNullOrEmpty(googleDrive.RefreshTokenEncrypted);
            }

            if (connection.Usb is { } usb)
            {
                _usb.Kind = usb.Kind;
                _usb.HardwareSerial = usb.HardwareSerial;
                _usb.VolumeSerial = usb.VolumeSerial;
                _usb.MtpSerial = usb.MtpSerial;
                _usb.DeviceLabel = usb.DeviceLabel;
                _usb.RootFolder = usb.RootFolder;
                Input.NotificationsEnabled = usb.NotificationsEnabled;
                Input.NotifyOnConnect = usb.NotifyOnConnect;
                Input.NotifyOnDisconnect = usb.NotifyOnDisconnect;
            }
        }

        private async Task SubmitAsync()
        {
            var saved = Input.Type switch
            {
                ConnectionType.GoogleDrive => await SubmitGoogleDriveAsync(),
                ConnectionType.Usb => await SubmitUsbAsync(),
                _ => await SubmitSmbAsync(),
            };

            if (!saved)
            {
                return;
            }

            await OnSaved.InvokeAsync();
        }

        private async Task<bool> SubmitGoogleDriveAsync()
        {
            if (_googleDriveEditor is null || !_googleDriveEditor.Validate())
            {
                return false;
            }

            var googleDrive = new GoogleDriveConnectionInput(
                _googleDrive.UseBuiltInClient,
                _googleDrive.ClientId.Trim(),
                _googleDrive.ClientSecret,
                _googleDrive.RefreshToken,
                _googleDrive.AccountEmail,
                string.IsNullOrWhiteSpace(_googleDrive.RootFolder) ? null : _googleDrive.RootFolder);

            if (ConnectionId is { } id)
            {
                await ConnectionService.UpdateAsync(id, Input.Name, googleDrive);
            }
            else
            {
                await ConnectionService.CreateAsync(Input.Name, ConnectionType.GoogleDrive, googleDrive);
            }

            return true;
        }

        private async Task<bool> SubmitSmbAsync()
        {
            if (_smbEditor is null || !_smbEditor.Validate())
            {
                return false;
            }

            var smb = new SmbConnectionInput(
                _smb.Host.Trim(),
                _smb.Port,
                _smb.Share.Trim(),
                _smb.Domain,
                _smb.Username.Trim(),
                _smb.Password,
                _smb.RootFolder);

            if (ConnectionId is { } id)
            {
                await ConnectionService.UpdateAsync(id, Input.Name, smb);
            }
            else
            {
                await ConnectionService.CreateAsync(Input.Name, ConnectionType.Smb, smb);
            }

            return true;
        }

        private async Task<bool> SubmitUsbAsync()
        {
            if (_usbEditor is null || !_usbEditor.Validate())
            {
                return false;
            }

            var usb = new UsbConnectionInput(
                _usb.Kind,
                _usb.HardwareSerial,
                _usb.VolumeSerial,
                _usb.MtpSerial,
                _usb.DeviceLabel,
                string.IsNullOrWhiteSpace(_usb.RootFolder) ? null : _usb.RootFolder,
                Input.NotificationsEnabled,
                Input.NotifyOnConnect,
                Input.NotifyOnDisconnect);

            if (ConnectionId is { } id)
            {
                await ConnectionService.UpdateAsync(id, Input.Name, usb);
            }
            else
            {
                await ConnectionService.CreateAsync(Input.Name, ConnectionType.Usb, usb);
            }

            return true;
        }

        public sealed class InputModel
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            public ConnectionType Type { get; set; } = ConnectionType.Smb;

            // USB connect/disconnect desktop notifications (only used by the USB connection type).
            public bool NotificationsEnabled { get; set; } = true;

            public bool NotifyOnConnect { get; set; } = true;

            public bool NotifyOnDisconnect { get; set; } = true;
        }
    }
}
