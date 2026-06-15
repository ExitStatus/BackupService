namespace BackupService.Extensions
{
    /// <summary>
    /// Supplies explanatory help text for an enum value, read via
    /// <see cref="EnumExtensions.GetHelpText"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class HelpTextAttribute(string text) : Attribute
    {
        public string Text { get; } = text;
    }
}
