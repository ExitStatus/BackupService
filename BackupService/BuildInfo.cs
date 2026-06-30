namespace BackupService
{
    /// <summary>
    /// Exposes the deployed build number, read once from <c>buildnumber.txt</c> next to the executable. The number is
    /// bumped by the publish profile on every deploy (see <c>Properties/PublishProfiles/Install.pubxml</c>) and shown
    /// at the bottom of the sidebar, so a running instance can be confirmed to match the build that was just published.
    /// </summary>
    public static class BuildInfo
    {
        private static readonly Lazy<string> LazyNumber = new(() =>
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, "buildnumber.txt");
                return File.Exists(path) ? File.ReadAllText(path).Trim() : "?";
            }
            catch
            {
                return "?";
            }
        });

        public static string Number => LazyNumber.Value;
    }
}
