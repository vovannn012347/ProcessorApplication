
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Common.Interfaces.Menu;

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

using ProcessorApplication.Database.Models;
using ProcessorApplication.Infrastructure;
using ProcessorApplication.Services.HashStamps;

using static System.Formats.Asn1.AsnWriter;
using static Org.BouncyCastle.Crypto.Engines.SM2Engine;

namespace ProcessorApplication.Services.User;

public class ProcessorApplicationUserManager : UserManager<ApplicationUser>
{
    private readonly IServiceProvider _services;
    private readonly IHashStampService _hashProvider;
    private readonly IEmailService _emailService;
    // private readonly IStringLocalizer<SharedResource> _localizer;

    public ProcessorApplicationUserManager(
        IUserStore<ApplicationUser> store,
        IOptions<IdentityOptions> optionsAccessor,
        IPasswordHasher<ApplicationUser> passwordHasher,
        IEnumerable<IUserValidator<ApplicationUser>> userValidators,
        IEnumerable<IPasswordValidator<ApplicationUser>> passwordValidators,
        ILookupNormalizer keyNormalizer,
        IdentityErrorDescriber errors,
        IServiceProvider services,
        ILogger<ProcessorApplicationUserManager> logger,
        IHashStampService hashProvider,
        IEmailService emailService//,
                                  //IStringLocalizer<SharedResource> localizer
        ) : base(store, optionsAccessor, passwordHasher, userValidators, passwordValidators, keyNormalizer, errors, services, logger)
    {
        _services = services;
        _hashProvider = hashProvider;
        _emailService = emailService;
        //_localizer = localizer;
    }
    private enum CryptoAction { Encrypt, Decrypt }

    /// <summary>
    /// Selects properties marked with [ProtectedPersonalData], respecting inheritance rules.
    /// </summary>
    public static List<PropertyInfo> GetSelectedProperties(Type objectType)
    {
        var selectedProperties = new List<PropertyInfo>();

        // Get all properties on the type
        PropertyInfo[] allProps = objectType.GetProperties(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy
        );

        foreach (PropertyInfo prop in allProps)
        {
            // process only Strings for encryption in this context
            if (prop.PropertyType != typeof(string)) continue;

            // Determine the Type where this specific property definition exists
            Type declaringType = prop.DeclaringType;

            // If the property was defined in the Derived Class (ApplicationUser)
            if (declaringType == objectType)
            {
                // Rule 1: We only select if the Derived class explicitly puts the attribute here.
                // inherit: false checks ONLY the derived class for the tag.
                if (Attribute.IsDefined(prop, typeof(ProtectedPersonalDataAttribute), inherit: false))
                {
                    selectedProperties.Add(prop);
                }
            }
            // If the property was inherited from a Base Class (IdentityUser)
            else
            {
                // Rule 2: Non-overridden properties should persist their selection behavior.
                // inherit: true checks the chain up to the base class.
                if (Attribute.IsDefined(prop, typeof(ProtectedPersonalDataAttribute), inherit: true))
                {
                    selectedProperties.Add(prop);
                }
            }
        }

        return selectedProperties;
    }

    private void ProcessUserData(ApplicationUser user, CryptoAction action)
    {
        // 1. Key Check
        if (string.IsNullOrEmpty(user.EncryptionHash))
        {
            // Without the key, we cannot touch the protected data.
            // In a production app, you might throw or log depending on severity.
            throw new InvalidOperationException($"Cannot {action} user data: EncryptionKey is missing from memory.");
        }

        if (action == CryptoAction.Encrypt && user.IsEncrypted)
        {
            return;
        }

        if (action == CryptoAction.Decrypt && !user.IsEncrypted)
        {
            return;
        }

        // 2. Key Derivation (PHSK + ServerHash)
        var key = GetUserKey(user);

        var keyBytes = SecurityHelperUtil.MakeValidHashKey(key);

        // 3. Property Selection
        var protectedProps = GetSelectedProperties(user.GetType());

        // 4. Processing Loop
        foreach (var prop in protectedProps)
        {
            var currentValue = (string)prop.GetValue(user);

            // Skip nulls or empty strings
            if (string.IsNullOrEmpty(currentValue)) continue;

            string newValue = currentValue;

            try
            {
                if (action == CryptoAction.Encrypt)
                {
                    // Encrypt raw text
                    newValue = AesEncryptionHelper.Encrypt(currentValue, keyBytes);
                }
                else // Decrypt
                {
                    // Decrypt ciphertext
                    newValue = AesEncryptionHelper.Decrypt(currentValue, keyBytes);
                }

                // Update the object in memory
                prop.SetValue(user, newValue);
            }
            catch (Exception ex)
            {
                // If decryption fails (bad key, data corruption), we log but do not crash.
                // The field remains as-is.
                Logger.LogWarning($"Crypto failure on property {prop.Name} for user {user.Id}: {ex.Message}");
            }
        }

        user.IsEncrypted = (action == CryptoAction.Encrypt);
    }

    private static readonly char[] Base32Alphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890".ToCharArray();

    public async Task<string> GenerateUniqueUsername(string name, string surname, int length = 32)
    {
        if (length < 4) throw new ArgumentException("Length must be >= 4");

        var userName = "";
        ApplicationUser user = null;
        do
        {
            // Combine inputs
            string input = $"{name}{surname}{Guid.NewGuid()}";

            // Hash with SHA256
            using var sha = SHA256.Create();
            byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));

            // Convert bytes to alphanumeric via Base32-like encoding
            StringBuilder sb = new StringBuilder();

            int bits = 0;
            int value = 0;

            foreach (byte b in hashBytes)
            {
                value = (value << 8) | b;
                bits += 8;

                while (bits >= 5)
                {
                    bits -= 5;
                    int index = (value >> bits) & 0b11111;
                    sb.Append(Base32Alphabet[index]);
                }
            }

            // If still bits left, pad
            if (bits > 0)
            {
                int index = (value << (5 - bits)) & 0b11111;
                sb.Append(Base32Alphabet[index]);
            }

            // Trim to desired length
            userName = sb.ToString();

            if (length <= userName.Length)
            {
                userName = userName.Substring(0, length);
            }

            user = await FindByNameAsync(userName);

        } while (user != null);

        return userName;
    }
    public void InitializeUserSecurity(ApplicationUser user, string plainPassword, int length = 32)
    {
        var serverHash = _hashProvider.GetLatestHashAsync().Result;
        user.CreateDateTime = DateTime.UtcNow;
        if (string.IsNullOrEmpty(user.EncryptionHash))
        {
            user.EncryptionHash = SecurityHelperUtil.GeneratePHSK(length);
        }

        user.PersonalHashKeyLockedByPassword = SecurityHelperUtil.EncryptData(user.EncryptionHash, plainPassword);
        user.ServerEncryptedHashKey = SecurityHelperUtil.EncryptData(user.EncryptionHash, serverHash.MasterKey);
        user.UserIdLockedByPHSK = SecurityHelperUtil.EncryptData(user.UserName, user.EncryptionHash);

        var derivedKey = SecurityHelperUtil.DeriveKey(user.EncryptionHash, serverHash.MasterKey);

        var defaultSettings = new UserSecuritySettings();
        user.EncryptedSecuritySettings = SecurityHelperUtil.EncryptData(JsonSerializer.Serialize(defaultSettings), user.EncryptionHash);

        // PHSK lock and ID lock happen AFTER userManager.CreateAsync
    }


    public override async Task<ApplicationUser> FindByNameAsync(string userName)
    {
        ThrowIfDisposed();
        if (userName == null)
        {
            throw new ArgumentNullException(nameof(userName));
        }
        userName = NormalizeName(userName);

        //we do have randomized user identifier, no need to scribble on username
        var user = await Store.FindByNameAsync(userName, CancellationToken);
        return user;
    }

    private string GetUserKey(ApplicationUser user)
    {
        var serverHash = _hashProvider.GetHashByTimeAsync(user.CreateDateTime).Result;
        if (string.IsNullOrEmpty(user.EncryptionHash))
        {
            user.EncryptionHash = SecurityHelperUtil.DecryptData(user.ServerEncryptedHashKey, serverHash.MasterKey);
        }
        return SecurityHelperUtil.DeriveKey(user.EncryptionHash, serverHash.MasterKey);
    }

    private string UnprotectData(ApplicationUser user, string data)
    {
        var key = GetUserKey(user);
        return SecurityHelperUtil.DecryptData(data, key);
    }

    private string ProtectData(ApplicationUser user, string data)
    {
        var key = GetUserKey(user);
        return SecurityHelperUtil.EncryptData(data, key);
    }

    public override async Task UpdateNormalizedUserNameAsync(ApplicationUser user)
    {
        var normalizedName = NormalizeName(await GetUserNameAsync(user));
        await Store.SetNormalizedUserNameAsync(user, normalizedName, CancellationToken);
    }

    private IUserEmailStore<ApplicationUser> GetEmailStore(bool throwOnFail = true)
    {
        var cast = Store as IUserEmailStore<ApplicationUser>;
        if (throwOnFail && cast == null)
        {
            throw new NotSupportedException("Database not supported as email store");
        }
        return cast;
    }

    public override async Task UpdateNormalizedEmailAsync(ApplicationUser user)
    {
        var store = GetEmailStore();
        if (store == null) return;

        string emailToNormalize = user.Email;
        string NormalizedEmail = string.Empty;

        if (user.IsEncrypted)
        {
            try
            {
                var key = GetUserKey(user);
                var keyBytes = SecurityHelperUtil.MakeValidHashKey(key);
                emailToNormalize = AesEncryptionHelper.Decrypt(user.Email, keyBytes);
                var normalized = NormalizeEmail(emailToNormalize);
                NormalizedEmail = AesEncryptionHelper.Encrypt(normalized, keyBytes);
            }
            catch {
                return;
            }
        }
        else
        {
            NormalizedEmail = NormalizeEmail(emailToNormalize);
        }

        await store.SetNormalizedEmailAsync(user, NormalizedEmail, CancellationToken);
    }


    /// <summary>
    /// Gets the user, if any, associated with the normalized value of the specified email address.
    /// WARNING: email search will not use decrypted emails
    /// </summary>
    /// <param name="email">The email address to return the user for.</param>
    /// <returns>
    /// The task object containing the results of the asynchronous lookup operation, the user, if any, associated with a normalized value of the specified email address.
    /// </returns>
    public override async Task<ApplicationUser> FindByEmailAsync(string email)
    {
        ThrowIfDisposed();
        var store = GetEmailStore();
        if (email == null)
        {
            throw new ArgumentNullException(nameof(email));
        }

        email = NormalizeEmail(email);

        var user = await store.FindByEmailAsync(email, CancellationToken);
        return user;
    }

    /// <summary>
    /// Creates the specified <paramref name="user"/> in the backing store with given password,
    /// as an asynchronous operation.
    /// Requires user with encryption key properly set if user encrypted.
    /// </summary>
    /// <param name="user">The user to create.</param>
    /// <param name="password">The password for the user to hash and store.</param>
    /// <returns>
    /// The <see cref="Task"/> that represents the asynchronous operation, containing the <see cref="IdentityResult"/>
    /// of the operation.
    /// </returns>
    public override async Task<IdentityResult> CreateAsync(ApplicationUser user, string password)
    {
        if (user.IsEncrypted)
        {
            if (string.IsNullOrEmpty(user.EncryptionHash))
            {
                return IdentityResult.Failed(new IdentityError { Description = "Cannot update user: Encryption key missing for validation." });
            }
            // Decrypt user and set IsEncrypted = false internally
            ProcessUserData(user, CryptoAction.Decrypt);
        }

        InitializeUserSecurity(user, password);

        //validate first, then encrypt
        var validResult = await ValidateUserAsync(user);
        if (!validResult.Succeeded) return validResult;

        // encrypt
        //ProcessUserData(user, CryptoAction.Encrypt);
        //user.IsEncrypted = true;

        await UpdateNormalizedUserNameAsync(user);
        await UpdateNormalizedEmailAsync(user);

        ProcessUserData(user, CryptoAction.Encrypt);

        // Password hashing is handled by base.CreateAsync usually, so we must be careful.
        // To be safe, we let base run, but we acknowledge it validates ciphertext.
        return await base.CreateAsync(user, password);
    }

    protected override async Task<IdentityResult> UpdateUserAsync(ApplicationUser user)
    {
        bool wasEncryptedInitially = user.IsEncrypted;

        if (user.IsEncrypted)
        {
            if (string.IsNullOrEmpty(user.EncryptionHash))
            {
                return IdentityResult.Failed(new IdentityError { Description = "Cannot update user: Encryption key missing for validation." });
            }
            // Decrypt and set IsEncrypted = false internally
            ProcessUserData(user, CryptoAction.Decrypt);
        }

        var result = await ValidateUserAsync(user);
        if (!result.Succeeded)
        {
            return result;
        }

        // Validation passed. Now we lock it up again.
        //ProcessUserData(user, CryptoAction.Encrypt);
        //user.IsEncrypted = true;

        await UpdateNormalizedUserNameAsync(user);
        await UpdateNormalizedEmailAsync(user);

        ProcessUserData(user, CryptoAction.Encrypt);

        return await Store.UpdateAsync(user, CancellationToken);
    }

    public bool UnlockKeyWithPassword(ApplicationUser user, string password)
    {
        try
        {
            // Decrypt the password-locked key container
            user.EncryptionHash = SecurityHelperUtil.DecryptData(user.PersonalHashKeyLockedByPassword, password);
            return !string.IsNullOrEmpty(user.EncryptionHash);
        }
        catch
        {
            return false;
        }
    }

    public bool DecryptUserDataByPassword(ApplicationUser user, string password)
    {
        if (!UnlockKeyWithPassword(user, password)) return false;

        if (user.IsEncrypted)
        {
            ProcessUserData(user, CryptoAction.Decrypt);
            user.IsEncrypted = false;
        }
        return true;
    }

    private bool UnlockKeyWithServerMaster(ApplicationUser user)
    {
        try
        {
            // 1. Get the Server Hash used at user creation (Time-based)
            var serverHash = _hashProvider.GetHashByTimeAsync(user.CreateDateTime).Result;

            // 2. Decrypt the server-locked key container
            user.EncryptionHash = SecurityHelperUtil.DecryptData(user.ServerEncryptedHashKey, serverHash.MasterKey);

            return !string.IsNullOrEmpty(user.EncryptionHash);
        }
        catch
        {
            return false;
        }
    }

    public bool DecryptUserDataByServer(ApplicationUser user)
    {
        if (!UnlockKeyWithServerMaster(user))
        {
            Logger.LogError("Audit Decryption Failed: Master Key mismatch or Server Hash rotation.");
            return false;
        }

        // Optimistic Decryption
        // We MUST decrypt the object in memory first to read the SecurityPreferences and Email.
        // At this point, the data is cleartext in memory, but NOT yet returned to the caller.
        if (user.IsEncrypted)
        {
            ProcessUserData(user, CryptoAction.Decrypt);
            user.IsEncrypted = false;
        }

        bool userWantsNotification = user.SecurityPreferences.NotifyOnDataAccess;
        string userEmail = user.Email;
        bool hasEmail = !string.IsNullOrEmpty(userEmail);

        if (userWantsNotification && hasEmail)
        {
            // Check Configuration ONLY NOW
            if (!_emailService.IsHealthy)
            {
                Logger.LogWarning("Audit Access BLOCKED: User {UserId} requires notification, but Email Service is not configured.", user.Id);

                // Re-encrypt the data before returning so the caller gets nothing readable.
                ProcessUserData(user, CryptoAction.Encrypt);
                user.IsEncrypted = true;

                return false;
            }
            var adminName = 
                //_contextAccessor.HttpContext?.User?.Identity?.Name ??
                "System";
            string subject = 
                //_localizer["SecurityAlertTitle"];
                "Security Alert: Profile Access";

            // Key: "AuditMessageBody" -> Value: "Your profile data was accessed by an Administrator ({0}) for audit purposes."
            // We pass 'adminName' as the argument {0}
            string body = //_localizer["AuditMessageBody", adminName];
            $"Your profile data was accessed by an Administrator for audit purposes.";
            //$"Your profile data was accessed by an Administrator ({_contextAccessor.HttpContext?.User?.Identity?.Name ?? "System"}) for audit purposes."

            // Attempt Send
            _emailService.NotifyUserDataAccess(
                userEmail,
                subject,
                body
            );

            //we do not check the result - it is not out shenanigans by now
            //// C. Check Send Result
            //if (!emailSent)
            //{
            //    Logger.LogCritical("Audit Access BLOCKED: Failed to send required notification to {Email}", userEmail);

            //    // *** CRITICAL ROLLBACK ***
            //    ProcessUserData(user, CryptoAction.Encrypt);
            //    user.IsEncrypted = true;

            //    return false;
            //}
        }

        // Either notification was sent successfully, OR the user didn't require/have one.
        // Return true with the User object still in Decrypted state.
        return true;
    }

    public bool DecryptUserDataDirect(ApplicationUser user, string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            Logger.LogError("Direct Decryption Failed: Key is null.");
            return false;
        }

        user.EncryptionHash = key;

        if (user.IsEncrypted)
        {
            ProcessUserData(user, CryptoAction.Decrypt);
            user.IsEncrypted = false;
        }

        return true;
    }

    public bool LoadKeyForAdminAction(ApplicationUser user)
    {
        GetUserKey(user);
        return !string.IsNullOrEmpty(user.EncryptionHash);
    }

    public void ReLockPHSKAsync(ApplicationUser user, string phsk, string password)
    {
        user.PersonalHashKeyLockedByPassword = SecurityHelperUtil.EncryptData(phsk, password);
    }
}