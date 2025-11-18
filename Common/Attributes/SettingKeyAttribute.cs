namespace Common.Attributes;

[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class SettingKeyAttribute : Attribute
{
    public string Key { get; }

    public SettingKeyAttribute(string key)
    {
        Key = key;
    }
}