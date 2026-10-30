using System.Runtime.InteropServices;
using System.Text;
using TradingHub.App.Services;

namespace TradingHub.Tests;

/// <summary>Las claves: el Administrador de credenciales de Windows y el archivo de prueba (nunca el Llavero real).</summary>
public class SecretStoreTests
{
    [Fact]
    public void Credential_HasTheWindowsLayout()
    {
        // CREDENTIALW de wincred.h: 80 bytes en 64 bits (x64 y ARM64) y 52 en 32 bits
        bool x64 = IntPtr.Size == 8;
        Assert.Equal(x64 ? 80 : 52, Marshal.SizeOf<WindowsCredentialStore.Credential>());
        Assert.Equal(x64 ? 24 : 16, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.LastWritten)));
        Assert.Equal(x64 ? 32 : 24, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.CredentialBlobSize)));
        Assert.Equal(x64 ? 40 : 28, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.CredentialBlob)));
        Assert.Equal(x64 ? 48 : 32, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.Persist)));
        Assert.Equal(x64 ? 72 : 48, (int)Marshal.OffsetOf<WindowsCredentialStore.Credential>(nameof(WindowsCredentialStore.Credential.UserName)));
    }

    [Fact]
    public void Credential_KeepsTheSecretAsUtf8AndUnderstandsOnesSavedByHand()
    {
        foreach (var secret in new[] { "a", "abc123", "contraseña ñ €", new string('x', WindowsCredentialStore.MaxBlobBytes) })
            Assert.Equal(secret, WindowsCredentialStore.Decode(WindowsCredentialStore.Encode(secret)));
        // cmdkey y el Administrador de credenciales guardan UTF-16
        Assert.Equal("hunter2", WindowsCredentialStore.Decode(Encoding.Unicode.GetBytes("hunter2")));
        Assert.Equal("Trading Hub/ab12cd34:secret", WindowsCredentialStore.TargetName("Trading Hub", "ab12cd34:secret"));
    }

    [Fact]
    public void FileSecretStore_SavesReadsAndDeletes()
    {
        var path = Path.Combine(Path.GetTempPath(), "tradinghub-secretos-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new FileSecretStore(path);
            Assert.Equal("", store.Get("x:secret"));
            store.Set("x:secret", "s3cr3t");
            store.Set("x:token", "tok");
            Assert.Equal("s3cr3t", new FileSecretStore(path).Get("x:secret"));
            store.Set("x:secret", null);
            Assert.Equal("", store.Get("x:secret"));
            Assert.Equal("tok", store.Get("x:token"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [WindowsFact]
    public void CredentialManager_AMissingCredentialIsEmpty()
    {
        // solo lee y borra algo que no existe: no cambia nada del Administrador de credenciales
        var store = new WindowsCredentialStore("Trading Hub (prueba)");
        var account = Guid.NewGuid().ToString("N") + ":secret";
        Assert.Equal("", store.Get(account));
        store.Set(account, null);
    }

    [WindowsOptInFact("TRADINGHUB_TEST_CREDMAN")]
    public void CredentialManager_SavesReadsAndDeletes()
    {
        var store = new WindowsCredentialStore("Trading Hub (prueba)");
        var account = Guid.NewGuid().ToString("N") + ":secret";
        try
        {
            store.Set(account, "contraseña ñ €");
            Assert.Equal("contraseña ñ €", store.Get(account));
            store.Set(account, "otra");
            Assert.Equal("otra", store.Get(account));
        }
        finally
        {
            store.Set(account, null);
        }

        Assert.Equal("", store.Get(account));
    }
}
