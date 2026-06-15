using System.ComponentModel;
using System.Reflection;

namespace BackupService.Extensions
{
    /// <summary>
    /// Helpers for working with enum values.
    /// </summary>
    public static class EnumExtensions
    {
        /// <summary>
        /// Returns the text of the <see cref="DescriptionAttribute"/> applied to the enum
        /// value, falling back to the value's name when no description is present.
        /// </summary>
        public static string GetDescription(this Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var attribute = field?.GetCustomAttribute<DescriptionAttribute>();
            return attribute?.Description ?? value.ToString();
        }

        /// <summary>
        /// Returns the text of the <see cref="HelpTextAttribute"/> applied to the enum value,
        /// or an empty string when none is present.
        /// </summary>
        public static string GetHelpText(this Enum value)
        {
            var field = value.GetType().GetField(value.ToString());
            var attribute = field?.GetCustomAttribute<HelpTextAttribute>();
            return attribute?.Text ?? string.Empty;
        }
    }
}
