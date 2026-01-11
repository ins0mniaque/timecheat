using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Timecheat;

internal interface ICredentialStore
{
    void Set(string service, string account, string secret);
    string? Get(string service, string account);
    void Delete(string service, string account);
}

internal static class CredentialStore
{
    public static ICredentialStore? Create()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return LinuxSecretToolStore.IsAvailable() ? new LinuxSecretToolStore() : null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return MacOSKeychainStore.IsAvailable() ? new MacOSKeychainStore() : null;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return WindowsCredentialStore.IsAvailable() ? new WindowsCredentialStore() : null;

        return null;
    }
}

internal sealed class LinuxSecretToolStore : ICredentialStore
{
    private static string? SecretToolPath => field ??=
        File.Exists("/usr/bin/secret-tool") ? "/usr/bin/secret-tool" :
        File.Exists("/bin/secret-tool") ? "/bin/secret-tool" : null;

    public static bool IsAvailable() => SecretToolPath is not null;

    public void Set(string service, string account, string secret)
    {
        Run(secret, "store", "--label", service, "service", service, "account", account);
    }

    public string? Get(string service, string account)
    {
        var output = RunCapture("lookup", "service", service, "account", account);
        return string.IsNullOrWhiteSpace(output) ? null : output.TrimEnd();
    }

    public void Delete(string service, string account)
    {
        Run(null, "clear", "service", service, "account", account);
    }

    private static void Run(string? input, params string[] args) => RunInternal(input, false, args);
    private static string RunCapture(params string[] args) => RunInternal(null, true, args)!;

    private static string? RunInternal(string? input, bool captureOutput, params string[] args)
    {
        var startInfo = new ProcessStartInfo(SecretToolPath!)
        {
            RedirectStandardOutput = captureOutput,
            RedirectStandardInput = input != null,
            RedirectStandardError = true
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;

        if (input != null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        process.WaitForExit();

        return captureOutput ? process.StandardOutput.ReadToEnd() : null;
    }
}

internal sealed class MacOSKeychainStore : ICredentialStore
{
    private const string SecurityToolPath = "/usr/bin/security";

    public static bool IsAvailable() => File.Exists(SecurityToolPath);

    public void Set(string service, string account, string secret)
    {
        Run("add-generic-password", "-U", "-s", service, "-a", account, "-w", secret);
    }

    public string? Get(string service, string account)
    {
        var output = RunCapture("find-generic-password", "-s", service, "-a", account, "-w");
        return string.IsNullOrWhiteSpace(output) ? null : output.TrimEnd();
    }

    public void Delete(string service, string account)
    {
        Run("delete-generic-password", "-s", service, "-a", account);
    }

    private static void Run(string command, params string[] args) => RunInternal(command, null, false, args);
    private static string RunCapture(string command, params string[] args) => RunInternal(command, null, true, args)!;

    private static string? RunInternal(string command, string? input, bool captureOutput, params string[] args)
    {
        var startInfo = new ProcessStartInfo(SecurityToolPath)
        {
            RedirectStandardOutput = captureOutput,
            RedirectStandardInput = input != null,
            RedirectStandardError = true
        };

        startInfo.ArgumentList.Add(command);
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)!;

        if (input != null)
        {
            process.StandardInput.Write(input);
            process.StandardInput.Close();
        }

        process.WaitForExit();

        return captureOutput ? process.StandardOutput.ReadToEnd() : null;
    }
}

internal sealed partial class WindowsCredentialStore : ICredentialStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    public static bool IsAvailable() => true;

    public void Set(string service, string account, string secret)
    {
        var servicePtr = Marshal.StringToHGlobalUni(service);
        var accountPtr = Marshal.StringToHGlobalUni(account);
        var secretBytes = Encoding.Unicode.GetBytes(secret);

        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = servicePtr,
            UserName = accountPtr,
            CredentialBlobSize = (uint)secretBytes.Length,
            CredentialBlob = Marshal.AllocHGlobal(secretBytes.Length),
            Persist = CRED_PERSIST_LOCAL_MACHINE
        };

        Marshal.Copy(secretBytes, 0, credential.CredentialBlob, secretBytes.Length);

        try
        {
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(credential.CredentialBlob);
            Marshal.FreeHGlobal(servicePtr);
            Marshal.FreeHGlobal(accountPtr);
        }
    }

    public string? Get(string service, string account)
    {
        if (!CredRead($"{service}:{account}", CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        using var handle = new CriticalCredentialHandle(credPtr);
        return handle.GetSecret();
    }

    public void Delete(string service, string account)
    {
        CredDelete($"{service}:{account}", CRED_TYPE_GENERIC, 0);
    }

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWrite(ref CREDENTIAL userCredential, uint flags);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredRead(string target, int type, int flags, out IntPtr credentialPtr);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDelete(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void CredFree(IntPtr cred);

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    private sealed class CriticalCredentialHandle(IntPtr handle) : IDisposable
    {
        public string GetSecret()
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(handle)!;
            return Marshal.PtrToStringUni(cred.CredentialBlob, (int)cred.CredentialBlobSize / 2)!;
        }

        public void Dispose() => CredFree(handle);
    }
}