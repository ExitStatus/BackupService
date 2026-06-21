using System.ComponentModel.DataAnnotations;
using BackupService.Components.Controls;
using BackupService.Connections;
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

        private bool IsEdit => ConnectionId.HasValue;

        private string DialogTitle => IsEdit
            ? $"Edit {Input.Type.GetDescription()} Connection"
            : "Create Connection";

        private string IntroText => Input.Type switch
        {
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
        }

        private async Task SubmitAsync()
        {
            var saved = Input.Type switch
            {
                _ => await SubmitSmbAsync(),
            };

            if (!saved)
            {
                return;
            }

            await OnSaved.InvokeAsync();
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

        public sealed class InputModel
        {
            [Required]
            public string Name { get; set; } = string.Empty;

            public ConnectionType Type { get; set; } = ConnectionType.Smb;
        }
    }
}
