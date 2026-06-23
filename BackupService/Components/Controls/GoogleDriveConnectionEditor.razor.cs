using BackupService.Connections;
using BackupService.Connections.GoogleDrive;
using BackupService.Connections.Smb;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace BackupService.Components.Controls
{
    /// <summary>
    /// Editor for a Google Drive connection. By default it uses the app's built-in OAuth client (one-click
    /// Authorize, no Client ID/secret); an Advanced toggle reveals fields for the user's own OAuth client.
    /// Hosts the in-app consent flow, a remote folder browser and a Test button. Mutates the shared
    /// <see cref="GoogleDriveEditModel"/> the hosting dialog reads on save.
    /// </summary>
    public partial class GoogleDriveConnectionEditor : ComponentBase
    {
        [Inject]
        private IGoogleDriveConnector Connector { get; set; } = default!;

        [Inject]
        private IGoogleOAuthFlowService OAuthFlow { get; set; } = default!;

        [Inject]
        private IConnectionResolver ConnectionResolver { get; set; } = default!;

        [Inject]
        private GoogleDriveAppCredentials AppCredentials { get; set; } = default!;

        [Inject]
        private NavigationManager Navigation { get; set; } = default!;

        [Inject]
        private IJSRuntime JS { get; set; } = default!;

        [Parameter]
        public GoogleDriveEditModel Model { get; set; } = new();

        /// <summary>The id of the connection being edited (null when creating) — used to recover the stored
        /// secret/refresh token for Test/Browse/Authorize when those boxes are left blank.</summary>
        [Parameter]
        public int? ConnectionId { get; set; }

        private bool _busy;
        private bool _authorizing;
        private bool _showBrowser;
        private string? _authUrl;
        private GoogleDriveConnectionInfo? _browseInfo;
        private ConnectionTestResult? _status;

        private bool _clientIdError;
        private bool _clientSecretError;
        private bool _authError;

        private bool HasBuiltInClient => AppCredentials.IsConfigured;

        private string RedirectUri => $"{Navigation.BaseUri.TrimEnd('/')}/connections/google/callback";

        private string AuthStatusText => Model.HasStoredAuth
            ? $"Authorized as {(string.IsNullOrWhiteSpace(Model.AccountEmail) ? "your account" : Model.AccountEmail)}"
            : "Not authorized";

        protected override void OnInitialized()
        {
            // With no built-in client the only option is a custom one.
            if (!HasBuiltInClient)
            {
                Model.UseBuiltInClient = false;
            }
        }

        private void OnToggleCustomClient(ChangeEventArgs e)
        {
            var useCustom = e.Value is true;
            Model.UseBuiltInClient = !useCustom;
        }

        /// <summary>Validates the required fields; returns true when all are present.</summary>
        public bool Validate()
        {
            if (Model.UseBuiltInClient)
            {
                _clientIdError = false;
                _clientSecretError = false;
            }
            else
            {
                _clientIdError = string.IsNullOrWhiteSpace(Model.ClientId);
                // On create the secret must be supplied; on edit a blank box keeps the stored one.
                _clientSecretError = string.IsNullOrWhiteSpace(Model.ClientSecret) && ConnectionId is null;
            }
            // A connection must have an authorization: a freshly-captured token, or a stored one when editing.
            _authError = string.IsNullOrWhiteSpace(Model.RefreshToken) && !(ConnectionId is not null && Model.HasStoredAuth);
            return !_clientIdError && !_clientSecretError && !_authError;
        }

        private async Task AuthorizeAsync()
        {
            GoogleOAuthBeginResult begin;
            if (Model.UseBuiltInClient)
            {
                if (!OAuthFlow.HasBuiltInClient)
                {
                    _status = ConnectionTestResult.Failure("No built-in Google client is configured.");
                    return;
                }
                begin = OAuthFlow.BeginBuiltIn(RedirectUri);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(Model.ClientId))
                {
                    _status = ConnectionTestResult.Failure("Enter the Client ID first.");
                    return;
                }
                var secret = Model.ClientSecret;
                if (string.IsNullOrEmpty(secret) && ConnectionId is { } id)
                {
                    secret = (await ConnectionResolver.GetGoogleDriveInfoAsync(id)).ClientSecret;
                }
                if (string.IsNullOrEmpty(secret))
                {
                    _status = ConnectionTestResult.Failure("Enter the Client secret first.");
                    return;
                }
                begin = OAuthFlow.Begin(Model.ClientId.Trim(), secret, RedirectUri);
            }

            _authUrl = begin.AuthUrl;
            _authorizing = true;
            _status = null;

            // Open the consent page in a new tab (a fallback link is shown if the popup is blocked).
            await JS.InvokeVoidAsync("open", begin.AuthUrl, "_blank");

            var result = await OAuthFlow.WaitAsync(begin.State, TimeSpan.FromMinutes(5));

            _authorizing = false;
            _authUrl = null;
            if (result.Ok)
            {
                Model.RefreshToken = result.RefreshToken;
                Model.AccountEmail = result.Email;
                Model.HasStoredAuth = true;
                _authError = false;
                _status = ConnectionTestResult.Success($"Authorized as {result.Email ?? "your account"}.");
            }
            else
            {
                _status = ConnectionTestResult.Failure(result.Error ?? "Authorization failed.");
            }
        }

        private async Task TestAsync()
        {
            _busy = true;
            _status = null;
            try
            {
                _status = await Connector.TestAsync(await BuildInfoAsync());
            }
            catch (Exception ex)
            {
                _status = ConnectionTestResult.Failure($"Test failed: {ex.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        private async Task BrowseAsync()
        {
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
        /// Builds runtime connection info from the current fields. The OAuth client id/secret come from the
        /// app's built-in client (built-in mode) or the typed/stored custom client; a blank secret/token on an
        /// existing connection falls back to the stored value.
        /// </summary>
        private async Task<GoogleDriveConnectionInfo> BuildInfoAsync()
        {
            string clientId;
            string secret;
            if (Model.UseBuiltInClient)
            {
                clientId = AppCredentials.ClientId ?? string.Empty;
                secret = AppCredentials.ClientSecret ?? string.Empty;
            }
            else
            {
                clientId = Model.ClientId.Trim();
                secret = Model.ClientSecret ?? string.Empty;
                if (string.IsNullOrEmpty(secret) && ConnectionId is { } sid)
                {
                    secret = (await ConnectionResolver.GetGoogleDriveInfoAsync(sid)).ClientSecret;
                }
            }

            var token = Model.RefreshToken;
            if (string.IsNullOrEmpty(token) && ConnectionId is { } tid)
            {
                token = (await ConnectionResolver.GetGoogleDriveInfoAsync(tid)).RefreshToken;
            }

            return new GoogleDriveConnectionInfo(
                clientId,
                secret,
                token ?? string.Empty,
                Model.AccountEmail,
                string.IsNullOrWhiteSpace(Model.RootFolder) ? null : Model.RootFolder);
        }

        /// <summary>Editable Google Drive fields shared between this control and its hosting dialog.</summary>
        public sealed class GoogleDriveEditModel
        {
            /// <summary>True to use the app's built-in OAuth client; false to use the custom client below.</summary>
            public bool UseBuiltInClient { get; set; } = true;

            public string ClientId { get; set; } = string.Empty;

            /// <summary>Plaintext as typed; null/empty on edit means "keep the stored secret".</summary>
            public string? ClientSecret { get; set; }

            /// <summary>The refresh token captured this session; null/empty on edit means "keep the stored one".</summary>
            public string? RefreshToken { get; set; }

            public string? AccountEmail { get; set; }

            public string? RootFolder { get; set; }

            /// <summary>True when the connection already has a stored authorization (set on load, or after a fresh consent).</summary>
            public bool HasStoredAuth { get; set; }
        }
    }
}
