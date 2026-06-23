using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Drive.v3;
using Google.Apis.Services;

namespace BackupService.Connections.GoogleDrive
{
    /// <summary>
    /// Builds an authenticated <see cref="DriveService"/> from a connection's decrypted credentials. The
    /// stored refresh token is used to mint short-lived access tokens (the client refreshes them
    /// automatically). Shared by the connector (Test/Browse) and the Drive filesystem (the sync engine).
    /// </summary>
    internal static class GoogleDriveServiceFactory
    {
        internal const string ApplicationName = "BackupService";

        public static DriveService Create(GoogleDriveConnectionInfo info)
        {
            var flow = new GoogleAuthorizationCodeFlow(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = info.ClientId, ClientSecret = info.ClientSecret },
                Scopes = [DriveService.Scope.Drive],
            });

            // A token response carrying only the refresh token; the credential refreshes the access token
            // on first use and as it expires.
            var token = new TokenResponse { RefreshToken = info.RefreshToken };
            var credential = new UserCredential(flow, "user", token);

            return new DriveService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName = ApplicationName,
            });
        }
    }
}
