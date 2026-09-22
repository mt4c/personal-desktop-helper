using System.Security.Cryptography;
using System.Text;
using PersonalDesktopHelper.Copilot;

namespace PersonalDesktopHelper.Tests;

public sealed class ProtectedCredentialStoreTests
{
    [Fact]
    public void CredentialRoundTripsEncryptedAndSignOutRemovesIt()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "copilot-auth.dat");
        var store = new ProtectedCopilotCredentialStore(path);
        Assert.Null(store.Load());
        store.Save("test-only-fake-oauth");

        Assert.Equal("test-only-fake-oauth", store.Load());
        Assert.DoesNotContain("test-only-fake-oauth", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.Empty(Directory.GetFiles(directory.Path, "*.tmp"));
        store.Clear();
        Assert.False(File.Exists(path));
        Assert.Null(store.Load());
    }

    [Fact]
    public void CorruptCredentialIsNotSilentlyReplaced()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "copilot-auth.dat");
        File.WriteAllText(path, "invalid encrypted data");
        var store = new ProtectedCopilotCredentialStore(path);

        Assert.Throws<CryptographicException>(() => store.Load());
        Assert.Equal("invalid encrypted data", File.ReadAllText(path));
    }
}
