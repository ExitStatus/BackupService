using BackupService.Connections.GoogleDrive;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace BackupService.UnitTests.Connections
{
    [TestFixture]
    public class GoogleOAuthFlowServiceTests
    {
        private const string RedirectUri = "http://localhost:5080/connections/google/callback";

        private GoogleOAuthFlowService _service = null!;

        [SetUp]
        public void SetUp() =>
            _service = new GoogleOAuthFlowService(
                NullLogger<GoogleOAuthFlowService>.Instance,
                new GoogleDriveAppCredentials("builtin-id.apps.googleusercontent.com", "builtin-secret"));

        [Test]
        public void Begin_BuildsConsentUrl_WithOfflineAccessForcedConsentAndState()
        {
            var begin = _service.Begin("my-client.apps.googleusercontent.com", "secret", RedirectUri);

            begin.State.Should().NotBeNullOrWhiteSpace();

            var query = ParseQuery(begin.AuthUrl);
            query["client_id"].Should().Be("my-client.apps.googleusercontent.com");
            query["redirect_uri"].Should().Be(RedirectUri);
            query["access_type"].Should().Be("offline");
            query["prompt"].Should().Be("consent");
            query["state"].Should().Be(begin.State);
            query["scope"].Should().Contain("drive");
        }

        [Test]
        public void BeginBuiltIn_UsesTheConfiguredBuiltInClient()
        {
            var begin = _service.BeginBuiltIn(RedirectUri);

            var query = ParseQuery(begin.AuthUrl);
            query["client_id"].Should().Be("builtin-id.apps.googleusercontent.com");
            query["access_type"].Should().Be("offline");
            query["state"].Should().Be(begin.State);
        }

        [Test]
        public void HasBuiltInClient_ReflectsConfiguration()
        {
            _service.HasBuiltInClient.Should().BeTrue();

            var unconfigured = new GoogleOAuthFlowService(
                NullLogger<GoogleOAuthFlowService>.Instance,
                new GoogleDriveAppCredentials(null, null));
            unconfigured.HasBuiltInClient.Should().BeFalse();
        }

        [Test]
        public async Task CompleteAsync_WithError_ResolvesWaitWithFailure()
        {
            var begin = _service.Begin("client", "secret", RedirectUri);

            await _service.CompleteAsync(begin.State, code: null, error: "access_denied");
            var result = await _service.WaitAsync(begin.State, TimeSpan.FromSeconds(5));

            result.Ok.Should().BeFalse();
            result.Error.Should().Contain("access_denied");
        }

        [Test]
        public async Task WaitAsync_WithUnknownState_ReturnsFailure()
        {
            var result = await _service.WaitAsync("no-such-state", TimeSpan.FromSeconds(1));

            result.Ok.Should().BeFalse();
            result.Error.Should().Contain("expired");
        }

        [Test]
        public async Task WaitAsync_WhenNothingArrives_TimesOut()
        {
            var begin = _service.Begin("client", "secret", RedirectUri);

            var result = await _service.WaitAsync(begin.State, TimeSpan.FromMilliseconds(50));

            result.Ok.Should().BeFalse();
            result.Error.Should().Contain("Timed out");
        }

        private static Dictionary<string, string> ParseQuery(string url)
        {
            var query = new Uri(url).Query.TrimStart('?');
            return query.Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : string.Empty));
        }
    }
}
