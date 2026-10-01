using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using DwgTranslator.Application;
using DwgTranslator.Contracts;

namespace DwgTranslator.Infrastructure.Windows;

public sealed class WindowsCredentialSecretStore : ISecretStore
{
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobBytes = 2560;

    public Task<Result<string>> GetAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult(Failure<string>("SECRET_STORE_UNAVAILABLE", "Windows Credential Manager is unavailable."));
        if (!NativeMethods.CredRead(Target(reference), CredentialType.Generic, 0, out var pointer))
            return Task.FromResult(Marshal.GetLastWin32Error() == ErrorNotFound
                ? Failure<string>("SECRET_NOT_FOUND", "The configured secret does not exist.")
                : Failure<string>("SECRET_READ_FAILED", "Windows Credential Manager could not read the secret."));
        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlobSize is 0 or > MaxCredentialBlobBytes || credential.CredentialBlobSize % 2 != 0 || credential.CredentialBlob == IntPtr.Zero)
                return Task.FromResult(Failure<string>("SECRET_DATA_INVALID", "The stored secret has an invalid protected representation."));
            var bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Task.FromResult(Results.Success(Encoding.Unicode.GetString(bytes)));
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { NativeMethods.CredFree(pointer); }
    }

    public Task<Result<bool>> SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult(Failure<bool>("SECRET_STORE_UNAVAILABLE", "Windows Credential Manager is unavailable."));
        if (string.IsNullOrEmpty(secret)) return Task.FromResult(Failure<bool>("SECRET_VALUE_INVALID", "A non-empty secret is required."));
        var byteCount = Encoding.Unicode.GetByteCount(secret);
        if (byteCount > MaxCredentialBlobBytes) return Task.FromResult(Failure<bool>("SECRET_VALUE_TOO_LARGE", "The secret exceeds Windows Credential Manager limits."));
        var blob = Marshal.StringToCoTaskMemUni(secret);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = Target(reference),
                CredentialBlobSize = byteCount,
                CredentialBlob = blob,
                Persist = CredentialPersistence.LocalMachine,
                UserName = Environment.UserName
            };
            return Task.FromResult(NativeMethods.CredWrite(ref credential, 0)
                ? Results.Success(true)
                : Failure<bool>("SECRET_WRITE_FAILED", "Windows Credential Manager could not store the secret."));
        }
        finally
        {
            ZeroUnmanaged(blob, byteCount);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public Task<Result<bool>> DeleteAsync(SecretReference reference, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows()) return Task.FromResult(Failure<bool>("SECRET_STORE_UNAVAILABLE", "Windows Credential Manager is unavailable."));
        if (NativeMethods.CredDelete(Target(reference), CredentialType.Generic, 0))
            return Task.FromResult(Results.Success(true));
        var nativeError = Marshal.GetLastWin32Error();
        if (nativeError == ErrorNotFound) return Task.FromResult(Results.Success(true));
        return Task.FromResult(Failure<bool>("SECRET_DELETE_FAILED", $"Windows Credential Manager could not delete the secret (Win32 {nativeError})."));
    }

    private static string Target(SecretReference reference) => "DwgTranslator/" + reference.Value["credential-manager:dwg-translator/".Length..];

    private static unsafe void ZeroUnmanaged(IntPtr pointer, int byteCount) => new Span<byte>(pointer.ToPointer(), byteCount).Clear();

    private static Result<T> Failure<T>(string code, string message) =>
        Results.Failure<T>(new ContractError(code, ErrorCategory.Security, message, false));

    private enum CredentialType : uint { Generic = 1 }
    private enum CredentialPersistence : uint { LocalMachine = 2 }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string UserName;
    }

#pragma warning disable SYSLIB1054 // CredWrite marshals a documented non-blittable CREDENTIALW structure.
    private static class NativeMethods
    {
        [DllImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, CredentialType type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref NativeCredential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, CredentialType type, uint flags);

        [DllImport("advapi32.dll")]
        internal static extern void CredFree(IntPtr buffer);
    }
#pragma warning restore SYSLIB1054
}
