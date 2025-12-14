namespace ProcessorApplication.Utils;

public static class ViewExtensions
{
    /// <summary>
    /// Truncates a string to a specified maximum length and appends an ellipsis if truncation occurs.
    /// Handles null or empty input safely.
    /// </summary>
    /// <param name="value">The input string (e.g., user.Name).</param>
    /// <param name="maxLength">The maximum desired length before ellipsis.</param>
    /// <returns>The truncated or original string.</returns>
    public static string Truncate(this string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        if (value.Length <= maxLength)
        {
            return value;
        }

        // Truncate the string and append the ellipsis
        return value.Substring(0, maxLength) + "...";
    }
}
