namespace BackupService.Connections.GoogleDrive
{
    /// <summary>The auth URL to open plus the opaque state that ties the consent back to this attempt.</summary>
    public sealed record GoogleOAuthBeginResult(string AuthUrl, string State);

    /// <summary>The outcome of an in-app OAuth consent: a refresh token (+ account email) or an error.</summary>
    public sealed record GoogleOAuthResult(bool Ok, string? RefreshToken, string? Email, string? Error)
    {
        public static GoogleOAuthResult Success(string refreshToken, string? email) => new(true, refreshToken, email, null);

        public static GoogleOAuthResult Failure(string error) => new(false, null, null, error);
    }

    /// <summary>
    /// Drives the in-app Google OAuth 2.0 consent flow. Bridges the editor circuit (which opens the consent
    /// page and awaits the result) and the OAuth callback HTTP request (which exchanges the authorization
    /// code), keyed by an unguessable <c>state</c>. The captured refresh token is held only in memory here;
    /// it is encrypted when the connection is saved.
    /// </summary>
    public interface IGoogleOAuthFlowService
    {
        /// <summary>
        /// Builds the Google consent URL (offline access, forced consent so a refresh token is always issued)
        /// and registers a pending attempt. Open <see cref="GoogleOAuthBeginResult.AuthUrl"/> in the browser,
        /// then await <see cref="WaitAsync"/> with the returned state.
        /// </summary>
        GoogleOAuthBeginResult Begin(string clientId, string clientSecret, string redirectUri);

        /// <summary>
        /// Like <see cref="Begin"/> but uses the app's built-in OAuth client (so the editor never handles the
        /// built-in secret). Throws if no built-in client is configured.
        /// </summary>
        GoogleOAuthBeginResult BeginBuiltIn(string redirectUri);

        /// <summary>Whether a built-in OAuth client is configured (drives the one-click vs Advanced editor UX).</summary>
        bool HasBuiltInClient { get; }

        /// <summary>Awaits the consent result for <paramref name="state"/>, failing if it doesn't arrive within <paramref name="timeout"/>.</summary>
        Task<GoogleOAuthResult> WaitAsync(string state, TimeSpan timeout, CancellationToken cancellationToken = default);

        /// <summary>
        /// Completes a pending attempt from the OAuth callback: exchanges <paramref name="code"/> for tokens
        /// (or records <paramref name="error"/>), resolving the awaiting <see cref="WaitAsync"/>. A no-op for
        /// an unknown/expired state.
        /// </summary>
        Task CompleteAsync(string state, string? code, string? error, CancellationToken cancellationToken = default);
    }
}
