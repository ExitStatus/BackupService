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
    /// <c>@bind-Path</c>. Reused by the OneWaySyncItem / InstantSync / ArchiveSync edit dialogs.
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
        /// while USB mass-storage stays offered. The target side of OneWaySync/ArchiveSync passes <c>AllowMtp="false"</c>.
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

        /// <summary>
        /// Confines Browse to this folder — relative to the connection's root, or a full path on this machine — and
        /// returns the pick relative to it. Used by a Path exclude, which names a location inside the source folder.
        /// </summary>
        [Parameter]
        public string? BrowseUnder { get; set; }

        /// <summary>
        /// Optional per-profile-edit-session memory of the last folder browsed (provided by ProfileDialog).
        /// When this field's folder is still blank, the picker opens at the remembered location instead of
        /// the root. Null outside a profile edit (e.g. the scheduled-task working-directory field).
        /// </summary>
        [CascadingParameter]
        private FolderBrowseMemory? BrowseMemory { get; set; }

        /// <summary>
        /// The path the picker should open at: this field's own folder if set, otherwise the last folder
        /// browsed for this connection in the current session (so the next item starts where the last ended).
        /// </summary>
        private string EffectivePath =>
            string.IsNullOrEmpty(Path) && BrowseUnder is null ? BrowseMemory?.Get(ConnectionId) ?? string.Empty : Path;

        private IReadOnlyList<ConnectionSummary> _connections = [];
        private List<int?> _options = [null];
        // The AllowUsb/AllowMtp the options were built for — the profile dialog changes them when the type changes.
        private (bool Usb, bool Mtp)? _optionsBuiltFor;
        private bool _browsing;
        private SmbConnectionInfo? _smbInfo;
        private GoogleDriveConnectionInfo? _googleDriveInfo;
        // A local-filesystem picker confined to this folder, with the pick stored relative to it (a mass-storage USB
        // connection's root, or BrowseUnder on this machine).
        private string? _localBrowseRoot;
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
            BuildOptions();
        }

        protected override void OnParametersSet()
        {
            // Rebuilt when the parent changes what's allowed (e.g. the profile type changed) — built once, the
            // dropdown kept offering USB to a type that can't use it.
            if (_optionsBuiltFor is { } built && built != (AllowUsb, AllowMtp))
            {
                BuildOptions();
            }
        }

        // The location options: null = this machine (local), then each configured connection. USB is hidden entirely
        // when !AllowUsb; read-only MTP is hidden when !AllowMtp (so a target offers mass-storage USB but not a camera).
        private void BuildOptions()
        {
            var selectable = _connections.Where(c =>
                (AllowUsb || c.Type != ConnectionType.Usb)
                && (AllowMtp || c.UsbKind != UsbDeviceKind.Mtp));
            _options = new List<int?> { null };
            _options.AddRange(selectable.Select(c => (int?)c.Id));
            _optionsBuiltFor = (AllowUsb, AllowMtp);
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

        /// <summary>Opens the picker — for a host that renders this field with neither the location nor the folder box.</summary>
        public Task OpenBrowserAsync() => BrowseAsync();

        private async Task BrowseAsync()
        {
            _smbInfo = null;
            _googleDriveInfo = null;
            _localBrowseRoot = null;
            _usbMtpSerial = null;
            _browseHint = null;

            try
            {
                await PrepareBrowseAsync();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // A saved password/token that can't be decrypted (e.g. the key ring was lost after a move) — a
                // supported state. Unhandled, it took down the whole page and every unsaved edit with it.
                _browseHint = "This connection's saved credentials can't be read. Edit the connection and enter them again.";
            }
            catch (Exception ex)
            {
                _browseHint = $"Couldn't open the connection: {ex.Message}";
            }
        }

        private async Task PrepareBrowseAsync()
        {
            // Remote: resolve the connection by type (decrypting its secrets) so the right picker can list it.
            if (ConnectionId is { } id)
            {
                switch (await ConnectionResolver.GetTypeAsync(id))
                {
                    case ConnectionType.GoogleDrive:
                        var drive = await ConnectionResolver.GetGoogleDriveInfoAsync(id);
                        // The confined picker returns paths relative to RootFolder, so moving the root down to
                        // BrowseUnder gives paths relative to that.
                        _googleDriveInfo = BrowseUnder is null ? drive : drive with { RootFolder = JoinRelative(drive.RootFolder, BrowseUnder) };
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
                            _usbMtpRoot = BrowseUnder is null ? usb.RootFolder ?? string.Empty : JoinRelative(usb.RootFolder, BrowseUnder);
                        }
                        else
                        {
                            var mountPath = UsbConnector.FindMountPath(usb);
                            if (mountPath is null)
                            {
                                _browseHint = "Plug the device in to browse it.";
                                return;
                            }

                            // A profile's folder is relative to the connection's root folder (the engine resolves
                            // mount + RootFolder + folder), so browse — and store the choice — relative to that root,
                            // as the SMB/Drive/MTP pickers do. Rooting at the drive instead applied the root twice.
                            var rootFolder = JoinRelative(usb.RootFolder, BrowseUnder);
                            var browseRoot = rootFolder.Length == 0 ? mountPath : System.IO.Path.Combine(mountPath, rootFolder);
                            if (!System.IO.Directory.Exists(browseRoot))
                            {
                                _browseHint = BrowseUnder is null
                                    ? $"The connection's root folder '{usb.RootFolder}' isn't on the device."
                                    : $"The folder '{BrowseUnder}' isn't on the device.";
                                return;
                            }
                            _localBrowseRoot = browseRoot;
                        }
                        break;
                    default:
                        var smb = await ConnectionResolver.GetSmbInfoAsync(id);
                        _smbInfo = BrowseUnder is null ? smb : smb with { RootFolder = JoinRelative(smb.RootFolder, BrowseUnder) };
                        break;
                }
            }
            else if (BrowseUnder is not null)
            {
                // This machine, confined to a full local path.
                if (!System.IO.Directory.Exists(BrowseUnder))
                {
                    _browseHint = $"The folder '{BrowseUnder}' doesn't exist.";
                    return;
                }
                _localBrowseRoot = BrowseUnder;
            }

            _browsing = true;
        }

        // Joins two connection-relative fragments with a single backslash.
        private static string JoinRelative(string? root, string? path)
        {
            var left = (root ?? string.Empty).Replace('/', '\\').Trim('\\');
            var right = (path ?? string.Empty).Replace('/', '\\').Trim('\\');
            if (left.Length == 0)
            {
                return right;
            }
            return right.Length == 0 ? left : $@"{left}\{right}";
        }

        private async Task OnSelected(string path)
        {
            // Remember this level so the next item's browse (this session) starts here — not a pick under
            // BrowseUnder, which is relative to a different folder.
            if (BrowseUnder is null)
            {
                BrowseMemory?.Set(ConnectionId, path);
            }
            await PathChanged.InvokeAsync(path);
            CancelBrowse();
        }

        // The rooted picker returns an absolute local path; store it relative to its root.
        private async Task OnRootedSelected(string absolutePath)
        {
            if (_localBrowseRoot is not null)
            {
                var relative = System.IO.Path.GetRelativePath(_localBrowseRoot, absolutePath);
                var stored = relative is "." or "" ? string.Empty : relative;
                if (BrowseUnder is null)
                {
                    BrowseMemory?.Set(ConnectionId, stored);
                }
                await PathChanged.InvokeAsync(stored);
            }

            CancelBrowse();
        }

        // The rooted picker takes an absolute local path; seed it from this field's folder or, when blank, the
        // remembered relative path for this connection (both relative to the picker's root).
        private string? RootedBrowseInitialPath => _localBrowseRoot is null
            ? null
            : string.IsNullOrEmpty(EffectivePath) ? _localBrowseRoot : System.IO.Path.Combine(_localBrowseRoot, EffectivePath);

        private void CancelBrowse()
        {
            _browsing = false;
            _smbInfo = null;
            _googleDriveInfo = null;
            _localBrowseRoot = null;
            _usbMtpSerial = null;
        }
    }
}
