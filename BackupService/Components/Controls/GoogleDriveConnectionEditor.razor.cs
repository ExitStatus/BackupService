using System.Security.Cryptography;
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
    public partial class GoogleDriveConnectionEditor : ComponentBase, IDisposable
    {
        private const string UnreadableSecrets =
            "The connection's saved credentials can't be read. Authorize again (and re-enter a custom client's secret).";

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
        private CancellationTokenSource? _authCts;
        private bool _showBrowser;
        private string? _authUrl;
        private GoogleDriveConnectionInfo? _browseInfo;
        private ConnectionTestResult? _status;

        private bool _clientIdError;
        private bool _clientSecretError;
        private bool _authError;

        private bool HasBuiltInClient => AppCredentials.IsConfigured;

        private string RedirectUri => $"{Navigation.BaseUri.TrimEnd('/')}/connections/google/callback";

        private string AuthStatusText =>
            Model.IsAuthorized
                ? $"Authorized as {(string.IsNullOrWhiteSpace(Model.AccountEmail) ? "your account" : Model.AccountEmail)}"
                : Model.StoredAuthClient is not null || Model.CapturedAuthClient is not null
                    ? "Not authorized for this OAuth client — authorize again"
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
                // A blank box keeps the stored secret — but only for the custom client that secret belongs to.
                _clientSecretError = string.IsNullOrWhiteSpace(Model.ClientSecret) && !Model.CanKeepStoredSecret;
            }
            // An authorization is tied to the OAuth client that issued it, so it must be for the client chosen now.
            _authError = !Model.IsAuthorized;
            return !_clientIdError && !_clientSecretError && !_authError;
        }

        private async Task AuthorizeAsync()
        {
            // While waiting, the button cancels the attempt (a second attempt would race the first for the result).
            if (_authorizing)
            {
                _authCts?.Cancel();
                return;
            }

            var client = Model.CurrentClient;
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
                if (string.IsNullOrEmpty(secret) && Model.CanKeepStoredSecret && ConnectionId is { } id)
                {
                    try
                    {
                        secret = (await ConnectionResolver.GetGoogleDriveInfoAsync(id)).ClientSecret;
                    }
                    catch (CryptographicException)
                    {
                        _status = ConnectionTestResult.Failure(UnreadableSecrets);
                        return;
                    }
                }
                if (string.IsNullOrEmpty(secret))
                {
                    _status = ConnectionTestResult.Failure("Enter the Client secret first.");
                    return;
                }
                begin = OAuthFlow.Begin(client.ClientId, secret, RedirectUri);
            }

            _authUrl = begin.AuthUrl;
            _authorizing = true;
            _status = null;
            _authCts = new CancellationTokenSource();

            try
            {
                // Open the consent page in a new tab (a fallback link is shown if the popup is blocked).
                await JS.InvokeVoidAsync("open", begin.AuthUrl, "_blank");
            }
            catch (Exception)
            {
                // Popup blocked or the browser went away — the fallback link covers the first; the wait below still
                // runs either way, so the pending attempt (which holds the client secret) is always cleared.
            }

            var result = await OAuthFlow.WaitAsync(begin.State, TimeSpan.FromMinutes(5), _authCts.Token);
            var cancelled = _authCts.IsCancellationRequested;
            _authCts.Dispose();
            _authCts = null;

            _authorizing = false;
            _authUrl = null;
            if (cancelled)
            {
                _status = ConnectionTestResult.Failure("Authorization cancelled.");
            }
            else if (result.Ok)
            {
                Model.RefreshToken = result.RefreshToken;
                Model.CapturedAuthClient = client;
                Model.AccountEmail = result.Email;
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
            catch (CryptographicException)
            {
                _status = ConnectionTestResult.Failure(UnreadableSecrets);
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
            catch (CryptographicException)
            {
                // Unhandled, this took down the page along with every unsaved edit.
                _status = ConnectionTestResult.Failure(UnreadableSecrets);
            }
            catch (Exception ex)
            {
                _status = ConnectionTestResult.Failure($"Couldn't open Google Drive: {ex.Message}");
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
        /// existing connection falls back to the stored value — only when it belongs to the client chosen now.
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
                clientId = Model.CurrentClient.ClientId;
                secret = Model.ClientSecret ?? string.Empty;
                if (string.IsNullOrEmpty(secret) && Model.CanKeepStoredSecret && ConnectionId is { } sid)
                {
                    secret = (await ConnectionResolver.GetGoogleDriveInfoAsync(sid)).ClientSecret;
                }
            }

            var token = Model.HasCapturedToken ? Model.RefreshToken : null;
            if (token is null && Model.StoredAuthClient == Model.CurrentClient && ConnectionId is { } tid)
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

        public void Dispose()
        {
            // Closing the dialog mid-authorization ends the wait (and drops the pending attempt holding the secret).
            _authCts?.Cancel();
        }

        /// <summary>Editable Google Drive fields shared between this control and its hosting dialog.</summary>
        public sealed class GoogleDriveEditModel
        {
            /// <summary>True to use the app's built-in OAuth client; false to use the custom client below.</summary>
            public bool UseBuiltInClient { get; set; } = true;

            public string ClientId { get; set; } = string.Empty;

            /// <summary>Plaintext as typed; null/empty on edit means "keep the stored secret" (same client only).</summary>
            public string? ClientSecret { get; set; }

            /// <summary>The refresh token captured this session (for <see cref="CapturedAuthClient"/>).</summary>
            public string? RefreshToken { get; set; }

            public string? AccountEmail { get; set; }

            public string? RootFolder { get; set; }

            /// <summary>The OAuth client the stored authorization was issued to; null when nothing is stored.</summary>
            public OAuthClient? StoredAuthClient { get; set; }

            /// <summary>The OAuth client <see cref="RefreshToken"/> was issued to.</summary>
            public OAuthClient? CapturedAuthClient { get; set; }

            /// <summary>The custom client whose secret is stored; null when the stored connection has none.</summary>
            public string? StoredCustomClientId { get; set; }

            public OAuthClient CurrentClient => OAuthClient.For(UseBuiltInClient, ClientId);

            /// <summary>A token captured this session for the client chosen now — the only one worth saving.</summary>
            public bool HasCapturedToken => !string.IsNullOrEmpty(RefreshToken) && CapturedAuthClient == CurrentClient;

            /// <summary>
            /// Whether there's an authorization for the client chosen now. A refresh token only works with the OAuth
            /// client that issued it, so one captured or stored for another client doesn't count.
            /// </summary>
            public bool IsAuthorized => HasCapturedToken || StoredAuthClient == CurrentClient;

            /// <summary>Whether a blank secret box can keep the stored secret: only for the same custom client.</summary>
            public bool CanKeepStoredSecret => StoredCustomClientId is not null && StoredCustomClientId == CurrentClient.ClientId;
        }

        /// <summary>Which OAuth client an authorization belongs to: the built-in one, or a custom one by client id.</summary>
        public readonly record struct OAuthClient(bool BuiltIn, string ClientId)
        {
            public static OAuthClient For(bool builtIn, string? clientId) =>
                new(builtIn, builtIn ? string.Empty : (clientId ?? string.Empty).Trim());
        }
    }
}
