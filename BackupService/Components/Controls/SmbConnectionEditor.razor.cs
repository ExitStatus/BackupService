using BackupService.Connections;
using BackupService.Connections.Smb;
using Microsoft.AspNetCore.Components;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for an SMB connection's fields, with a remote folder browser and a Test button.
    /// Mutates the shared <see cref="SmbEditModel"/> the hosting dialog reads on save.
    /// </summary>
    public partial class SmbConnectionEditor : ComponentBase
    {
        [Inject]
        private ISmbConnector SmbConnector { get; set; } = default!;

        [Inject]
        private IConnectionResolver ConnectionResolver { get; set; } = default!;

        [Parameter]
        public SmbEditModel Model { get; set; } = new();

        /// <summary>The id of the connection being edited (null when creating) — used to recover the
        /// stored password for Test/Browse when the password box is left blank.</summary>
        [Parameter]
        public int? ConnectionId { get; set; }

        private bool _busy;
        private bool _showBrowser;
        private SmbConnectionInfo? _browseInfo;
        private ConnectionTestResult? _testResult;

        private bool _hostError;
        private bool _shareError;
        private bool _usernameError;

        /// <summary>Validates the required fields; returns true when all are present.</summary>
        public bool Validate()
        {
            _hostError = string.IsNullOrWhiteSpace(Model.Host);
            _shareError = string.IsNullOrWhiteSpace(Model.Share);
            _usernameError = string.IsNullOrWhiteSpace(Model.Username);
            return !_hostError && !_shareError && !_usernameError;
        }

        private async Task TestAsync()
        {
            if (!Validate())
            {
                _testResult = ConnectionTestResult.Failure("Fill in host, share and username first.");
                return;
            }

            _busy = true;
            _testResult = null;
            try
            {
                var info = await BuildInfoAsync();
                _testResult = await SmbConnector.TestAsync(info);
            }
            catch (Exception ex)
            {
                _testResult = ConnectionTestResult.Failure($"Test failed: {ex.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task BrowseAsync()
        {
            if (!Validate())
            {
                _testResult = ConnectionTestResult.Failure("Fill in host, share and username first.");
                return;
            }

            _busy = true;
            try
            {
                _browseInfo = await BuildInfoAsync();
                _showBrowser = true;
            }
            finally
            {
                _busy = false;
            }
        }

        private void OnFolderSelected(string relativePath)
        {
            Model.RootFolder = relativePath;
            _showBrowser = false;
        }

        /// <summary>
        /// Builds runtime connection info from the current fields. When the password box is blank on an
        /// existing connection, the stored (decrypted) password is substituted.
        /// </summary>
        private async Task<SmbConnectionInfo> BuildInfoAsync()
        {
            var password = Model.Password ?? string.Empty;
            if (string.IsNullOrEmpty(password) && ConnectionId is { } id)
            {
                var stored = await ConnectionResolver.GetSmbInfoAsync(id);
                password = stored.Password;
            }

            return new SmbConnectionInfo(
                Model.Host.Trim(),
                Model.Port,
                Model.Share.Trim(),
                string.IsNullOrWhiteSpace(Model.Domain) ? null : Model.Domain.Trim(),
                Model.Username.Trim(),
                password,
                Model.RootFolder);
        }

        /// <summary>Editable SMB fields shared between this control and its hosting dialog.</summary>
        public sealed class SmbEditModel
        {
            public string Host { get; set; } = string.Empty;
            public int Port { get; set; } = 445;
            public string Share { get; set; } = string.Empty;
            public string? Domain { get; set; }
            public string Username { get; set; } = string.Empty;

            /// <summary>Plaintext as typed; null/empty on edit means "keep the stored password".</summary>
            public string? Password { get; set; }

            public string? RootFolder { get; set; }
        }
    }
}
