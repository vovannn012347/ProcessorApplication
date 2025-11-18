namespace Common.Interfaces;
public interface ISettings
{
    Dictionary<string, string> GetSettingForDb();
    ISettings FromDbSettings(Dictionary<string, string> dict);
    bool IsSettingSensitive(string key);
    Dictionary<string, string> GetFlattenedSettings(string prefix);
}