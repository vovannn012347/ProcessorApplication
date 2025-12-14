using System.Security.Claims;

namespace ProcessorApplication.Models.User;

/// <summary>
/// Implemented by modules to add domain-specific claims (user data) 
/// into the authentication cookie during sign-in/refresh.
/// </summary>
//public interface IUserClaimsProvider
//{
//    /// <summary>
//    /// Adds claims to the given ClaimsIdentity for the specified user.
//    /// </summary>
//    /// <param name="user">The user object retrieved from the database.</param>
//    /// <param name="identity">The identity object being constructed (modified directly).</param>
//    Task AddClaimsAsync(ApplicationUser user, ClaimsIdentity identity);
//}