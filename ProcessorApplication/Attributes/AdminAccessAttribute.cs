using Microsoft.AspNetCore.Authorization;

namespace ProcessorApplication.Attributes;

/// <summary>
/// Admin pages: the user must be signed in, have the role (default "Admin"; e.g. [AdminAccess("NetworkAdmin")]),
/// and the request must come from this computer or from an address/network listed in
/// Settings -> Security -> "Admin access from other computers" (SecuritySettings.AdminNetworks).
/// Use this on every admin controller instead of a bare [Authorize(Roles = ...)], which allows any network.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class AdminAccessAttribute : AuthorizeAttribute
{
    public const string PolicyName = "AdminNetworkPolicy";

    public AdminAccessAttribute() : this("Admin") { }

    public AdminAccessAttribute(string roles)
    {
        Policy = PolicyName;
        Roles = roles;
    }
}
