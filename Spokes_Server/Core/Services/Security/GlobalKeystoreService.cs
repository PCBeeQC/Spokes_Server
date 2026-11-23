using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Spokes_Server.Core.Services.Security;

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

    // Per-process secret used to compute constant-time HMAC-SHA256 password verifiers so
    // session-authenticated ScopedKeystoreService instances holding a valid vault cookie
    // do not re-run 100,000 PBKDF2-SHA256 iterations on every Blazor circuit creation.
    private readonly byte[] _verifierSecret = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, (byte[] VerifierHash, string PlainPrivateKey)> _verifiedEntries = new();

    private byte[] ComputeVerifierHash(string employeeId, string encryptedPrivateKey, string vaultPassword)
    {
        var inputBytes = Encoding.UTF8.GetBytes($"{employeeId}\0{encryptedPrivateKey}\0{vaultPassword}");
        return HMACSHA256.HashData(_verifierSecret, inputBytes);
    }

    /// <summary>
    /// Verifies in constant time whether the provided password and encrypted private key match
    /// an already-unlocked vault entry in memory, returning the plaintext private key without
    /// re-running 100,000 PBKDF2 iterations.
    /// </summary>
    internal bool TryGetVerifiedPrivateKey(
        string employeeId,
        string encryptedPrivateKey,
        string vaultPassword,
        out string? plainTextPrivateKey)
    {
        plainTextPrivateKey = null;
        if (string.IsNullOrEmpty(employeeId) ||
            string.IsNullOrEmpty(encryptedPrivateKey) ||
            string.IsNullOrEmpty(vaultPassword))
        {
            return false;
        }

        if (!_verifiedEntries.TryGetValue(employeeId, out var entry) ||
            !_unlockedPrivateKeys.ContainsKey(employeeId))
        {
            return false;
        }

        var candidateHash = ComputeVerifierHash(employeeId, encryptedPrivateKey, vaultPassword);
        if (!CryptographicOperations.FixedTimeEquals(entry.VerifierHash, candidateHash))
        {
            return false;
        }

        plainTextPrivateKey = entry.PlainPrivateKey;
        return true;
    }

    internal void StoreVerifiedPrivateKey(
        string employeeId,
        string encryptedPrivateKey,
        string vaultPassword,
        string plainTextPrivateKey)
    {
        if (string.IsNullOrEmpty(employeeId) ||
            string.IsNullOrEmpty(encryptedPrivateKey) ||
            string.IsNullOrEmpty(vaultPassword) ||
            string.IsNullOrEmpty(plainTextPrivateKey) ||
            !plainTextPrivateKey.Contains("<RSAKeyValue>", System.StringComparison.Ordinal))
        {
            return;
        }

        var verifierHash = ComputeVerifierHash(employeeId, encryptedPrivateKey, vaultPassword);
        _unlockedPrivateKeys[employeeId] = plainTextPrivateKey;
        _verifiedEntries[employeeId] = (verifierHash, plainTextPrivateKey);
    }

    /// <summary>
    /// Checks if the user's private key is currently unlocked in memory.
    /// </summary>
    public bool IsUserUnlocked(string employeeId)
        => !string.IsNullOrEmpty(employeeId) && _unlockedPrivateKeys.ContainsKey(employeeId);

    /// <summary>
    /// Retrieves the plaintext Private Key for an employee, if it has been unlocked.
    /// Returns null if locked.
    /// </summary>
    public string? GetPrivateKey(string employeeId)
    {
        if (string.IsNullOrEmpty(employeeId)) return null;
        return _unlockedPrivateKeys.TryGetValue(employeeId, out var key) ? key : null;
    }

    /// <summary>
    /// Unlocks and stores the user's private key in RAM.
    /// Returns true if successful, false if unlocking failed.
    /// </summary>
    public bool UnlockVault(string employeeId, string encryptedPrivateKey, string vaultPassword, ICryptoService cryptoService)
    {
        try
        {
            if (TryGetVerifiedPrivateKey(employeeId, encryptedPrivateKey, vaultPassword, out _))
            {
                return true;
            }

            // The password must match what was used to encrypt the private key
            var kek = cryptoService.DeriveKeyFromPassword(vaultPassword, employeeId); // Using employeeId as salt

            var plainTextPrivateKey = cryptoService.DecryptAes(encryptedPrivateKey, kek);

            // Basic validation that it's an XML key
            if (plainTextPrivateKey.Contains("<RSAKeyValue>"))
            {
                StoreVerifiedPrivateKey(employeeId, encryptedPrivateKey, vaultPassword, plainTextPrivateKey);
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
        if (!string.IsNullOrEmpty(employeeId))
        {
            _unlockedPrivateKeys.TryRemove(employeeId, out _);
            _verifiedEntries.TryRemove(employeeId, out _);
        }
    }
}
