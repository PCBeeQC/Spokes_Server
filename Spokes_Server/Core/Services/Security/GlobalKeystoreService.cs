using System.Collections.Concurrent;

namespace Spokes_Server.Core.Services.Security;

/// <summary>
/// A Singleton service that securely holds decrypted private keys in memory 
/// for the duration of the server process. This allows background services
/// (like NotificationRoutingService) to decrypt messages if the user has 
/// authenticated their vault at least once since the last server restart.
/// </summary>
/// <summary>
/// A global singleton storing decrypted private keys in RAM strictly for pushing 
/// background notifications.
/// 
/// DANGER / SECURITY NOTICE:
/// This service MUST NEVER be used by interactive UI components (Chat.razor, ChatSidebar.razor, etc).
/// Doing so introduces a critical vulnerability where an IDP-hijacked session can read an already 
/// unlocked victim's keystore simply by sharing the same EmployeeId.
/// 
/// For UI operations, ALWAYS use the ScopedKeystoreService which isolates vault unlocks
/// to the specific physical HTTP Session context holding the device cookie.
/// </summary>
public class GlobalKeystoreService
{
    // Dictionary mapping EmployeeId -> Plaintext Private Key XML
    private readonly ConcurrentDictionary<string, string> _unlockedPrivateKeys = new();

    /// <summary>
    /// Checks if the user's private key is currently unlocked in memory.
    /// </summary>
    public bool IsUserUnlocked(string employeeId)
    {
        if (string.IsNullOrEmpty(employeeId)) return false;
        return _unlockedPrivateKeys.ContainsKey(employeeId);
    }

    /// <summary>
    /// Retrieves the plaintext Private Key for an employee, if it has been unlocked.
    /// Returns null if locked.
    /// </summary>
    public string? GetPrivateKey(string employeeId)
    {
        if (string.IsNullOrEmpty(employeeId)) return null;
        _unlockedPrivateKeys.TryGetValue(employeeId, out var key);
        return key;
    }

    /// <summary>
    /// Unlocks and stores the user's private key in RAM.
    /// Returns true if successful, false if unlocking failed.
    /// </summary>
    public bool UnlockVault(string employeeId, string encryptedPrivateKey, string chatPassword, ICryptoService cryptoService)
    {
        try
        {
            // The password must match what was used to encrypt the private key
            var kek = cryptoService.DeriveKeyFromPassword(chatPassword, employeeId); // Using employeeId as salt

            var plainTextPrivateKey = cryptoService.DecryptAes(encryptedPrivateKey, kek);

            // Basic validation that it's an XML key
            if (plainTextPrivateKey.Contains("<RSAKeyValue>"))
            {
                _unlockedPrivateKeys[employeeId] = plainTextPrivateKey;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Forgets the user's private key from memory.
    /// </summary>
    public void LockVault(string employeeId)
    {
        _unlockedPrivateKeys.TryRemove(employeeId, out _);
    }
}
