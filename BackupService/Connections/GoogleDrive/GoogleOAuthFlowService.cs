using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Requests;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace BackupService.Connections.GoogleDrive
{
    /// <summary>
    /// Default <see cref="IGoogleOAuthFlowService"/> over the Google.Apis.Auth authorization-code flow.
    /// Pending attempts live in memory keyed by <c>state</c>, each carrying a
    /// <see cref="TaskCompletionSource{TResult}"/> the callback resolves.
    /// </summary>
    public sealed class GoogleOAuthFlowService(ILogger<GoogleOAuthFlowService> logger, GoogleDriveAppCredentials appCredentials) : IGoogleOAuthFlowService
    {
        internal const string ApplicationName = "BackupService";

        private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

        public bool HasBuiltInClient => appCredentials.IsConfigured;

        public GoogleOAuthBeginResult BeginBuiltIn(string redirectUri)
        {
            if (!appCredentials.IsConfigured)
            {
                throw new InvalidOperationException("No built-in Google OAuth client is configured.");
            }
            return Begin(appCredentials.ClientId!, appCredentials.ClientSecret!, redirectUri);
        }

        public GoogleOAuthBeginResult Begin(string clientId, string clientSecret, string redirectUri)
        {
            var state = Base64Url(RandomNumberGenerator.GetBytes(32));

            var flow = BuildFlow(clientId, clientSecret);
            var request = (GoogleAuthorizationCodeRequestUrl)flow.CreateAuthorizationCodeRequest(redirectUri);
            request.State = state;
            request.AccessType = "offline";  // request a refresh token
            request.Prompt = "consent";      // force the consent screen so a refresh token is always returned

            _pending[state] = new Pending(clientId, clientSecret, redirectUri, new TaskCompletionSource<GoogleOAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously));

            return new GoogleOAuthBeginResult(request.Build().ToString(), state);
        }

        public async Task<GoogleOAuthResult> WaitAsync(string state, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            if (!_pending.TryGetValue(state, out var pending))
            {
                return GoogleOAuthResult.Failure("The authorization attempt has expired. Please try again.");
            }

            var completed = await Task.WhenAny(pending.Completion.Task, Task.Delay(timeout, cancellationToken));
            _pending.TryRemove(state, out _);
            if (completed != pending.Completion.Task)
            {
                return GoogleOAuthResult.Failure("Timed out waiting for authorization. Please try again.");
            }

            return await pending.Completion.Task;
        }

        public async Task CompleteAsync(string state, string? code, string? error, CancellationToken cancellationToken = default)
        {
            // Peek (don't remove): the waiter removes the entry once it has consumed the result, so the
            // callback completing before WaitAsync runs is fine.
            if (!_pending.TryGetValue(state, out var pending))
            {
                return; // unknown/expired state — nothing to resolve
            }

            if (!string.IsNullOrEmpty(error))
            {
                pending.Completion.TrySetResult(GoogleOAuthResult.Failure($"Authorization was denied or failed: {error}."));
                return;
            }

            if (string.IsNullOrEmpty(code))
            {
                pending.Completion.TrySetResult(GoogleOAuthResult.Failure("No authorization code was returned."));
                return;
            }

            try
            {
                var flow = BuildFlow(pending.ClientId, pending.ClientSecret);
                var token = await flow.ExchangeCodeForTokenAsync("user", code, pending.RedirectUri, cancellationToken);

                if (string.IsNullOrEmpty(token.RefreshToken))
                {
                    pending.Completion.TrySetResult(GoogleOAuthResult.Failure(
                        "Google did not return a refresh token. Remove the app's access at myaccount.google.com and try again."));
                    return;
                }

                var email = await TryGetEmailAsync(flow, token, cancellationToken);
                pending.Completion.TrySetResult(GoogleOAuthResult.Success(token.RefreshToken, email));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Google OAuth code exchange failed.");
                pending.Completion.TrySetResult(GoogleOAuthResult.Failure($"Could not complete authorization: {ex.Message}"));
            }
        }

        // Reads the authorised account's email (best-effort; failure leaves it null).
        private async Task<string?> TryGetEmailAsync(GoogleAuthorizationCodeFlow flow, TokenResponse token, CancellationToken cancellationToken)
        {
            try
            {
                var credential = new UserCredential(flow, "user", token);
                using var drive = new DriveService(new BaseClientService.Initializer
                {
                    HttpClientInitializer = credential,
                    ApplicationName = ApplicationName,
                });
                var about = drive.About.Get();
                about.Fields = "user(emailAddress)";
                var response = await about.ExecuteAsync(cancellationToken);
                return response.User?.EmailAddress;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not read Google account email after authorization.");
                return null;
            }
        }

        private static GoogleAuthorizationCodeFlow BuildFlow(string clientId, string clientSecret) =>
            new(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = clientId, ClientSecret = clientSecret },
                Scopes = [DriveService.Scope.Drive],
            });

        private static string Base64Url(byte[] bytes) =>
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        private sealed record Pending(string ClientId, string ClientSecret, string RedirectUri, TaskCompletionSource<GoogleOAuthResult> Completion);
    }
}
