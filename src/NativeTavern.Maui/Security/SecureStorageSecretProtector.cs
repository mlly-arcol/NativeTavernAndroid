using NativeTavern.Security;

namespace NativeTavern.Maui.Security;

// MAUI SecureStorage is async-only, while the shared SettingsService calls
// ISecretProtector synchronously when it serializes ProviderSettings. The secret
// therefore lives in SecureStorage and is mirrored into memory: the value stored
// inside the SQLite settings blob is just a placeholder, and Unprotect ignores it
// in favour of the cached SecureStorage entry.
public sealed class SecureStorageSecretProtector : ISecretProtector
{
    private const string StorageKey = "NativeTavern.ProviderSettings.ApiKey";
    private readonly SemaphoreSlim gate = new(1, 1);
    private string? cached;

    public string Protect(string plaintext)
    {
        cached = plaintext ?? string.Empty;
        _ = SaveAsync(cached);
        return "secure-storage";
    }

    public string Unprotect(string protectedText) =>
        LoadAsync().GetAwaiter().GetResult() ?? string.Empty;

    private async Task<string> LoadAsync()
    {
        if (cached is not null) return cached;
        try { cached = await SecureStorage.GetAsync(StorageKey); }
        catch { cached = string.Empty; }
        return cached ?? string.Empty;
    }

    private async Task SaveAsync(string value)
    {
        await gate.WaitAsync();
        try
        {
            if (value.Length == 0) SecureStorage.Remove(StorageKey);
            else await SecureStorage.SetAsync(StorageKey, value);
        }
        catch
        {
            // Storage unavailable (rare device state); keep the in-memory secret.
        }
        finally { gate.Release(); }
    }
}
