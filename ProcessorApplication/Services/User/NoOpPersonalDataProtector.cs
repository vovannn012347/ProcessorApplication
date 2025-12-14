using Microsoft.AspNetCore.Identity;

namespace ProcessorApplication.Services.User;

/// <summary>
/// A pass-through protector. It satisfies the Identity framework's requirement
/// for an IPersonalDataProtector but does not apply any encryption.
/// This allows custom ServerUserDEKprotectorService to handle encryption exclusively.
/// </summary>
public class NoOpPersonalDataProtector : IPersonalDataProtector
{
    public string Protect(string data)
    {
        // Do nothing. Return data exactly as is.
        return data;
    }

    public string Unprotect(string data)
    {
        // Do nothing. Return data exactly as is.
        return data;
    }
}