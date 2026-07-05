using System.Reflection;

namespace BackupService
{
    /// <summary>
    /// Exposes the application version, read once from the executable's baked
    /// <see cref="AssemblyInformationalVersionAttribute"/>. The version follows the scheme Major.Minor.Build and is
    /// baked at build time (see <c>BackupService.csproj</c> / <c>Properties/PublishProfiles/Install.pubxml</c>):
    /// a local dev build is <c>0.0.0-dev</c>, a local publish is <c>0.0.{buildnumber.txt}-published</c>, and a CI
    /// build is <c>{Major}.{Minor}.{build}</c> (Major/Minor from <c>appsettings.json</c>). Shown in the sidebar.
    /// </summary>
    public static class BuildInfo
    {
        private static readonly Lazy<string> LazyVersion = new(() =>
        {
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? typeof(BuildInfo).Assembly;
                var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                if (string.IsNullOrWhiteSpace(informational))
                {
                    return "0.0.0-dev";
                }

                // Strip any "+<source revision>" build metadata the SDK may append, leaving just the version.
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }
            catch
            {
                return "0.0.0-dev";
            }
        });

        /// <summary>The application version (e.g. <c>0.0.0-dev</c>, <c>0.0.42-published</c>, <c>1.0.42</c>).</summary>
        public static string Version => LazyVersion.Value;
    }
}
