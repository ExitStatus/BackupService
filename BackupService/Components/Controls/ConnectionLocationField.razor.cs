using BackupService.Connections;
using BackupService.Connections.GoogleDrive;
using BackupService.Connections.Smb;
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

        private string LocationCaption => Grouped ? "Location" : $"{Label} location";

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

        /// <summary>An inline validation message to show under the folder box, or null.</summary>
        [Parameter]
        public string? Error { get; set; }

        private IReadOnlyList<ConnectionSummary> _connections = [];
        private List<int?> _options = [null];
        private bool _browsing;
        private SmbConnectionInfo? _smbInfo;
        private GoogleDriveConnectionInfo? _googleDriveInfo;

        protected override async Task OnInitializedAsync()
        {
            if (LocalOnly)
            {
                return; // no location dropdown — nothing to load
            }

            _connections = await ConnectionService.GetSummariesAsync();
            // The location options: null = this machine (local), then each configured connection.
            _options = new List<int?> { null };
            _options.AddRange(_connections.Select(c => (int?)c.Id));
        }

        private string LocationLabel(int? connectionId) =>
            connectionId is { } id
                ? _connections.FirstOrDefault(c => c.Id == id)?.Name ?? $"Connection {id}"
                : "This machine (local)";

        private async Task OnLocationChanged(int? connectionId)
        {
            await ConnectionIdChanged.InvokeAsync(connectionId);
            // Switching location changes what the path means, so clear it.
            await PathChanged.InvokeAsync(string.Empty);
        }

        private Task OnPathChanged(ChangeEventArgs e) =>
            PathChanged.InvokeAsync(e.Value?.ToString() ?? string.Empty);

        private async Task BrowseAsync()
        {
            _smbInfo = null;
            _googleDriveInfo = null;

            // Remote: resolve the connection by type (decrypting its secrets) so the right picker can list it.
            if (ConnectionId is { } id)
            {
                switch (await ConnectionResolver.GetTypeAsync(id))
                {
                    case ConnectionType.GoogleDrive:
                        _googleDriveInfo = await ConnectionResolver.GetGoogleDriveInfoAsync(id);
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

        private void CancelBrowse()
        {
            _browsing = false;
            _smbInfo = null;
            _googleDriveInfo = null;
        }
    }
}
