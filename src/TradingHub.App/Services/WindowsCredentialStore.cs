using System.Runtime.InteropServices;
using System.Text;
using TradingHub.Common.Logging;

namespace TradingHub.App.Services;

/// <summary>
/// Los secretos en el Administrador de credenciales de Windows: una credencial genérica «Trading Hub/&lt;cuenta&gt;» por
/// secreto, solo para este usuario en esta PC (no viaja con un perfil móvil). Es el equivalente del llavero de macOS.
/// </summary>
internal sealed class WindowsCredentialStore(string service) : ISecretStore
{
    private const uint CredTypeGeneric = 1;          // CRED_TYPE_GENERIC
    private const uint CredPersistLocalMachine = 2;  // CRED_PERSIST_LOCAL_MACHINE
    private const int ErrorNotFound = 1168;          // ERROR_NOT_FOUND

    /// <summary>Lo más grande que Windows guarda en una credencial (CRED_MAX_CREDENTIAL_BLOB_SIZE, en bytes).</summary>
    internal const int MaxBlobBytes = 5 * 512;

    /// <summary>El nombre de la credencial (lo que se ve en el Administrador de credenciales).</summary>
    internal static string TargetName(string service, string account) => $"{service}/{account}";

    public string Get(string account)
    {
        if (!CredRead(TargetName(service, account), CredTypeGeneric, 0, out var ptr))
        {
            int error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound) this.LogWarn($"No se pudo leer una clave del Administrador de credenciales (error {error}).");
            return "";
        }

        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlob == IntPtr.Zero || cred.CredentialBlobSize == 0) return "";
            var blob = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, blob, 0, blob.Length);
            string secret = Decode(blob);
            Array.Clear(blob);
            return secret;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    public void Set(string account, string? secret)
    {
        string target = TargetName(service, account);
        if (string.IsNullOrEmpty(secret))
        {
            if (!CredDelete(target, CredTypeGeneric, 0) && Marshal.GetLastWin32Error() is var error and not ErrorNotFound)
                this.LogWarn($"No se pudo borrar una clave del Administrador de credenciales (error {error}).");
            return;
        }

        var blob = Encode(secret);
        if (blob.Length > MaxBlobBytes)
        {
            Array.Clear(blob);
            this.LogWarn($"No se guardó la clave: Windows acepta hasta {MaxBlobBytes} bytes por credencial.");
            return;
        }

        IntPtr targetPtr = IntPtr.Zero, userPtr = IntPtr.Zero, blobPtr = IntPtr.Zero;
        try
        {
            targetPtr = Marshal.StringToHGlobalUni(target);
            userPtr = Marshal.StringToHGlobalUni(account);
            blobPtr = Marshal.AllocHGlobal(Math.Max(1, blob.Length));
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = targetPtr,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPtr,
                Persist = CredPersistLocalMachine,
                UserName = userPtr,
            };
            if (!CredWrite(ref cred, 0))
                this.LogWarn($"No se pudo guardar la clave en el Administrador de credenciales (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            if (blobPtr != IntPtr.Zero)
            {
                Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length); // no deja la clave en memoria suelta
                Marshal.FreeHGlobal(blobPtr);
            }

            if (targetPtr != IntPtr.Zero) Marshal.FreeHGlobal(targetPtr);
            if (userPtr != IntPtr.Zero) Marshal.FreeHGlobal(userPtr);
            Array.Clear(blob);
        }
    }

    /// <summary>La clave tal como se guarda: UTF-8 (las claves y tokens son ASCII: entran hasta 2560 caracteres).</summary>
    internal static byte[] Encode(string secret) => Encoding.UTF8.GetBytes(secret);

    /// <summary>
    /// Lee lo guardado. Trading Hub guarda UTF-8; una credencial cargada a mano (cmdkey o el Administrador de credenciales)
    /// viene en UTF-16, con un byte en cero después de cada letra ASCII, y también se entiende.
    /// </summary>
    internal static string Decode(byte[] blob)
    {
        bool utf16 = blob.Length >= 2 && blob.Length % 2 == 0;
        for (int i = 1; utf16 && i < blob.Length; i += 2) utf16 = blob[i] == 0 && blob[i - 1] != 0;
        return utf16 ? Encoding.Unicode.GetString(blob) : Encoding.UTF8.GetString(blob);
    }

    // ===================================================================== advapi32

    /// <summary>CREDENTIALW (wincred.h). Todo puntero o número: el mismo diseño en x64 y ARM64 (80 bytes) y en x86 (52).</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Credential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
