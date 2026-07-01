using BackupService.Connections;
using BackupService.Connections.GoogleDrive;
using BackupService.Connections.Smb;
using BackupService.Connections.Usb;
using BackupService.Enumerations;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// One source/target "location" field: a dropdown choosing <em>this machine (local)</em> or a
    /// configured connection, a folder textbox, and a Browse button that opens the local picker or the
    /// remote SMB picker as appropriate. Two-way bindable via <c>@bind-ConnectionId</c> and
    /// <c>@bind-Path</c>. Reused by the FolderPair / InstantSync / ArchiveSync edit dialogs.
    /// </summary>
    public partial class ConnectionLocationField : ComponentBase
    {
        [Inject]
        private IConnectionService ConnectionService { get; set; } = default!;

        [Inject]
        private IConnectionResolver ConnectionResolver { get; set; } = default!;

        [Inject]
        private IUsbConnector UsbConnector { get; set; } = default!;

        /// <summary>Field label prefix, e.g. "Source" or "Target".</summary>
        [Parameter]
        public string Label { get; set; } = "Folder";

        /// <summary>
        /// When true, render <see cref="Label"/> as a section heading with separate "Location" and
        /// "Folder" sub-labels (e.g. "Source" / Location / Folder) instead of the inline
        /// "{Label} location" / "{Label} folder" captions.
        /// </summary>
        [Parameter]
        public bool Grouped { get; set; }

        private string LocationCaption => Grouped ? "Location" : (ShowFolder ? $"{Label} location" : Label);

        private string FolderCaption => Grouped ? "Folder" : $"{Label} folder";

        private string FolderPlaceholder => ConnectionId is null ? string.Empty : "(connection root)";

        [Parameter]
        public int? ConnectionId { get; set; }

        [Parameter]
        public EventCallback<int?> ConnectionIdChanged { get; set; }

        [Parameter]
        public string Path { get; set; } = string.Empty;

        [Parameter]
        public EventCallback<string> PathChanged { get; set; }

        /// <summary>Offer a New-folder button in the local picker (target side only).</summary>
        [Parameter]
        public bool AllowCreateFolder { get; set; }

        /// <summary>
        /// When true, this side can only be a local folder — no location dropdown is shown and Browse uses
        /// the local picker. Used for the InstantSync source (a remote source can't be watched live).
        /// </summary>
        [Parameter]
        public bool LocalOnly { get; set; }

        /// <summary>
        /// When false, USB connections are not offered in the location dropdown at all. (Used for the target side of
        /// the watcher-driven types, which can't target USB.)
        /// </summary>
        [Parameter]
        public bool AllowUsb { get; set; } = true;

        /// <summary>
        /// When false, USB <b>MTP</b> connections are excluded (they're read-only — valid as a source, not a target),
        /// while USB mass-storage stays offered. The target side of FolderPair/ArchiveSync passes <c>AllowMtp="false"</c>.
        /// </summary>
        [Parameter]
        public bool AllowMtp { get; set; } = true;

        /// <summary>
        /// When false, only the location dropdown is rendered (no folder textbox / Browse). Used for the
        /// profile-level connection pickers, where the connection is chosen once and the folder lives per row.
        /// </summary>
        [Parameter]
        public bool ShowFolder { get; set; } = true;

        /// <summary>
        /// When false, the location dropdown is hidden and the (fixed) <see cref="ConnectionId"/> passed in by the
        /// parent is used for Browse. Used by the per-row folder editors against the profile-level connection.
        /// </summary>
        [Parameter]
        public bool ShowLocation { get; set; } = true;

        /// <summary>An inline validation message to show under the folder box, or null.</summary>
        [Parameter]
        public string? Error { get; set; }

        private IReadOnlyList<ConnectionSummary> _connections = [];
        private List<int?> _options = [null];
        private bool _browsing;
        private SmbConnectionInfo? _smbInfo;
        private GoogleDriveConnectionInfo? _googleDriveInfo;
        private string? _usbMountPath;
        private string? _usbMtpSerial;
        private string _usbMtpRoot = string.Empty;
        private string? _browseHint;

        protected override async Task OnInitializedAsync()
        {
            if (LocalOnly || !ShowLocation)
            {
                return; // no location dropdown — nothing to load (Browse resolves a fixed connection by id)
            }

            _connections = await ConnectionService.GetSummariesAsync();
            // The location options: null = this machine (local), then each configured connection. USB is hidden
            // entirely when !AllowUsb; read-only MTP is hidden when !AllowMtp (so a target offers mass-storage USB
            // but not a camera).
            var selectable = _connections.Where(c =>
                (AllowUsb || c.Type != ConnectionType.Usb)
                && (AllowMtp || c.UsbKind != UsbDeviceKind.Mtp));
            _options = new List<int?> { null };
            _options.AddRange(selectable.Select(c => (int?)c.Id));
        }

        private string LocationLabel(int? connectionId) =>
            connectionId is { } id
                ? _connections.FirstOrDefault(c => c.Id == id)?.Name ?? $"Connection {id}"
                : "This machine (local)";

        private async Task OnLocationChanged(int? connectionId)
        {
            await ConnectionIdChanged.InvokeAsync(connectionId);
            // Switching location changes what the path means, so clear it (only when this field owns a folder).
            if (ShowFolder)
            {
                await PathChanged.InvokeAsync(string.Empty);
            }
        }

        private Task OnPathChanged(ChangeEventArgs e) =>
            PathChanged.InvokeAsync(e.Value?.ToString() ?? string.Empty);

        private async Task BrowseAsync()
        {
            _smbInfo = null;
            _googleDriveInfo = null;
            _usbMountPath = null;
            _usbMtpSerial = null;
            _browseHint = null;

            // Remote: resolve the connection by type (decrypting its secrets) so the right picker can list it.
            if (ConnectionId is { } id)
            {
                switch (await ConnectionResolver.GetTypeAsync(id))
                {
                    case ConnectionType.GoogleDrive:
                        _googleDriveInfo = await ConnectionResolver.GetGoogleDriveInfoAsync(id);
                        break;
                    case ConnectionType.Usb:
                        // A USB connection is only browsable while its device is connected.
                        var usb = await ConnectionResolver.GetUsbInfoAsync(id);
                        if (usb.Kind == UsbDeviceKind.Mtp)
                        {
                            if (!(await UsbConnector.TestAsync(usb)).Ok)
                            {
                                _browseHint = "Plug the device in to browse it.";
                                return;
                            }
                            _usbMtpSerial = usb.MtpSerial;
                            _usbMtpRoot = usb.RootFolder ?? string.Empty;
                        }
                        else
                        {
                            _usbMountPath = UsbConnector.FindMountPath(usb);
                            if (_usbMountPath is null)
                            {
                                _browseHint = "Plug the device in to browse it.";
                                return;
                            }
                        }
                        break;
                    default:
                        _smbInfo = await ConnectionResolver.GetSmbInfoAsync(id);
                        break;
                }
            }

            _browsing = true;
        }

        private async Task OnSelected(string path)
        {
            await PathChanged.InvokeAsync(path);
            CancelBrowse();
        }

        // The USB picker returns an absolute path on the current drive; store it relative to the device root.
        private async Task OnUsbSelected(string absolutePath)
        {
            if (_usbMountPath is not null)
            {
                var relative = System.IO.Path.GetRelativePath(_usbMountPath, absolutePath);
                await PathChanged.InvokeAsync(relative is "." or "" ? string.Empty : relative);
            }

            CancelBrowse();
        }

        private string? UsbBrowseInitialPath => _usbMountPath is null
            ? null
            : string.IsNullOrEmpty(Path) ? _usbMountPath : System.IO.Path.Combine(_usbMountPath, Path);

        private void CancelBrowse()
        {
            _browsing = false;
            _smbInfo = null;
            _googleDriveInfo = null;
            _usbMountPath = null;
            _usbMtpSerial = null;
        }
    }
}
