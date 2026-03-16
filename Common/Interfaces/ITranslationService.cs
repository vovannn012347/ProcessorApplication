namespace Common.Interfaces;

public interface ITranslationService
{
    // Fetches a string for the current UI culture
    string GetString(string key);

    // Fetches a string for a specific locale (e.g., "uk-UA")
    string GetString(string key, string locale);

    // Returns all available keys for a specific locale (useful for JS-side translation)
    Dictionary<string, string> GetAllStrings(string locale);
}