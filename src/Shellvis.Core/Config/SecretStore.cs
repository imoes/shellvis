using System.Security.Cryptography;
using System.Text;

namespace Shellvis.Core.Config;

/// <summary>
/// API keys, encrypted to the current Windows user.
///
/// Needed because a provider cannot be configured without somewhere to put its key, and
/// the two obvious places are both wrong. config.yaml is out by an existing rule of this
/// project: <c>${VAR}</c> references are deliberately kept literal on save so a key never
/// lands in a file people paste into tickets. An environment variable is fine when the
/// user already manages one, but "set OPENAI_API_KEY and restart your session" is not a
/// configuration dialog.
///
/// So: DPAPI with <see cref="DataProtectionScope.CurrentUser"/>, one file per secret under
/// <c>%USERPROFILE%\.shellvis\secrets</c>. The ciphertext is bound to this Windows account
/// on this machine -- copying the file elsewhere yields nothing, and another account on the
/// same machine cannot read it. That is a real boundary, not obfuscation.
///
/// What it is NOT: protection from code running as this user. Anything the user can run can
/// ask DPAPI to unprotect these files, this agent included. The threat it addresses is a
/// key sitting in plaintext in a file that gets copied, synced or shared, which is the way
/// keys actually leak.
/// </summary>
public static class SecretStore
{
    /// <summary>
    /// Extra entropy mixed into the encryption.
    ///
    /// Not a secret and not pretending to be: it scopes the ciphertext to Shellvis, so a
    /// blob from another application's DPAPI store cannot be dropped in here and decrypt.
    /// </summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Shellvis.SecretStore.v1");

    private static string Directory => Path.Combine(ShellvisPaths.Home, "secrets");

    /// <summary>
    /// Where a named secret lives. The name is restricted rather than escaped: these names
    /// come from config and from a text field, and a name that walked out of the directory
    /// would write an encrypted blob somewhere nobody would look for it.
    /// </summary>
    private static string? PathFor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-' && c != '.')
                return null;
        }

        return Path.Combine(Directory, name + ".bin");
    }

    /// <summary>Store a secret, replacing any previous value. An empty value deletes it.</summary>
    public static void Set(string name, string? value)
    {
        if (PathFor(name) is not { } file)
            throw new ArgumentException($"'{name}' is not a usable secret name.", nameof(name));

        if (string.IsNullOrEmpty(value))
        {
            Delete(name);
            return;
        }

        System.IO.Directory.CreateDirectory(Directory);

        byte[] protectedBytes = ProtectedData.Protect(
            Encoding.UTF8.GetBytes(value),
            Entropy,
            DataProtectionScope.CurrentUser);

        // Written to a temporary file and moved into place, so an interrupted write cannot
        // leave a truncated blob that fails to decrypt for ever after.
        string temporary = file + ".new";
        File.WriteAllBytes(temporary, protectedBytes);
        File.Move(temporary, file, overwrite: true);
    }

    /// <summary>Read a secret, or null if it is absent or cannot be decrypted here.</summary>
    public static string? Get(string name)
    {
        if (PathFor(name) is not { } file || !File.Exists(file))
            return null;

        try
        {
            byte[] plain = ProtectedData.Unprotect(
                File.ReadAllBytes(file),
                Entropy,
                DataProtectionScope.CurrentUser);

            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException)
        {
            // A blob written by another Windows account, or a damaged file. Treated as
            // absent rather than fatal: the caller falls back to the environment variable
            // and the user can set the key again.
            return null;
        }
    }

    public static bool Has(string name) => Get(name) is { Length: > 0 };

    public static void Delete(string name)
    {
        if (PathFor(name) is not { } file)
            return;

        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The conventional secret name for a provider's key.
    ///
    /// Derived rather than configurable, so a key stored for a provider is found again
    /// without a second setting to keep in step with the first.
    /// </summary>
    public static string NameForProvider(string providerId) =>
        "provider." + providerId.Trim().ToLowerInvariant();
}
