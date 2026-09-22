using System.IO;
using System.Security.Cryptography;
using System.Text;
using PersonalDesktopHelper.Persistence;

namespace PersonalDesktopHelper.Copilot;

public sealed class ProtectedCopilotCredentialStore(string filePath) : ICopilotCredentialStore
{
    public string? Load()
    {
        byte[] encrypted;
        try
        {
            encrypted = File.ReadAllBytes(filePath);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            var token = Encoding.UTF8.GetString(plaintext);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidDataException("The saved Copilot credential is empty. Sign out and sign in again.");
            }

            return token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Save(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        var plaintext = Encoding.UTF8.GetBytes(token);
        try
        {
            AtomicFile.WriteAllBytes(filePath,
                ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void Clear() => File.Delete(filePath);
}
