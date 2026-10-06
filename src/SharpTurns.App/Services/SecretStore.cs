using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using SharpTurns.App.Services.Dictation;

namespace SharpTurns.App.Services;

/// <summary>The OS's credential store, which keeps secrets such as the Deepgram key out of the database.</summary>
internal interface ISecretStore
{
    /// <summary>The store's name for the Config tab.</summary>
    string Description { get; }

    Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default);

    /// <summary>A null value removes the secret.</summary>
    Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default);
}

internal static class SecretStoreFactory
{
    /// <summary>Null where there's no credential store: Linux without secret-tool, or another OS.</summary>
    public static ISecretStore? CreateDefault(string dataDirectory)
    {
        if (OperatingSystem.IsMacOS()) return new MacOsKeychainSecretStore();
        if (OperatingSystem.IsWindows()) return new WindowsDataProtectionSecretStore(Path.Combine(dataDirectory, "secrets"));
        if (OperatingSystem.IsLinux() && RecorderProcess.ResolveExecutablePath("secret-tool", ["/usr/bin/secret-tool"]) is { } path)
            return new LinuxSecretToolSecretStore(path);
        return null;
    }
}

/// <summary>
/// Generic passwords in the login keychain, under the service "SharpTurns". The legacy SecKeychain calls are deprecated
/// but still supported, and need no CoreFoundation dictionaries. The keychain trusts the build that saved the item, so
/// a rebuilt app may be asked to allow access.
/// </summary>
[SupportedOSPlatform("macos")]
internal sealed class MacOsKeychainSecretStore : ISecretStore
{
    private const string SecurityFramework = "/System/Library/Frameworks/Security.framework/Security";
    private const int Success = 0;
    private const int ItemNotFound = -25300;
    private static readonly byte[] Service = Encoding.UTF8.GetBytes("SharpTurns");

    public string Description => "macOS Keychain";

    // Keychain calls can wait on an access prompt, so they run off the UI thread.
    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var status = Find(name, out var length, out var data, out var item);
        if (status == ItemNotFound) return null;
        ThrowIfError(status);
        try { return Marshal.PtrToStringUTF8(data, checked((int)length)); }
        finally { Free(data, item); }
    }, cancellationToken);

    public Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var status = Find(name, out _, out var data, out var item);
        if (status != ItemNotFound) ThrowIfError(status);
        try
        {
            if (value is null)
            {
                if (item != IntPtr.Zero) ThrowIfError(SecKeychainItemDelete(item));
                return;
            }
            var password = Encoding.UTF8.GetBytes(value);
            if (item != IntPtr.Zero)
            {
                ThrowIfError(SecKeychainItemModifyAttributesAndData(item, IntPtr.Zero, (uint)password.Length, password));
                return;
            }
            var account = Encoding.UTF8.GetBytes(name);
            ThrowIfError(SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
                (uint)password.Length, password, out var added));
            if (added != IntPtr.Zero) CFRelease(added);
        }
        finally { Free(data, item); }
    }, cancellationToken);

    private static int Find(string name, out uint length, out IntPtr data, out IntPtr item)
    {
        var account = Encoding.UTF8.GetBytes(name);
        return SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)account.Length, account,
            out length, out data, out item);
    }

    private static void Free(IntPtr data, IntPtr item)
    {
        if (data != IntPtr.Zero) _ = SecKeychainItemFreeContent(IntPtr.Zero, data);
        if (item != IntPtr.Zero) CFRelease(item);
    }

    private static void ThrowIfError(int status)
    {
        if (status != Success) throw new InvalidOperationException($"The macOS Keychain refused the request (OSStatus {status}).");
    }

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, uint passwordLength, byte[] passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainFindGenericPassword(IntPtr keychainOrArray, uint serviceNameLength, byte[] serviceName,
        uint accountNameLength, byte[] accountName, out uint passwordLength, out IntPtr passwordData, out IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemModifyAttributesAndData(IntPtr itemRef, IntPtr attrList, uint length, byte[] data);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemDelete(IntPtr itemRef);

    [DllImport(SecurityFramework)]
    private static extern int SecKeychainItemFreeContent(IntPtr attrList, IntPtr data);

    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")]
    private static extern void CFRelease(IntPtr value);
}

/// <summary>
/// Each secret is a file in the given folder, encrypted with DPAPI so only the current Windows account can read it.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsDataProtectionSecretStore(string directory) : ISecretStore
{
    private const int CryptProtectUiForbidden = 0x1;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SharpTurns secret v1");

    public string Description => "Windows data protection (DPAPI)";

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        return Encoding.UTF8.GetString(Transform(await File.ReadAllBytesAsync(path, cancellationToken), protect: false));
    }

    public async Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default)
    {
        var path = PathFor(name);
        if (value is null)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, Transform(Encoding.UTF8.GetBytes(value), protect: true), cancellationToken);
        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(string name) => Path.Combine(directory, name + ".dpapi");

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputPointer = Marshal.AllocHGlobal(input.Length);
        var entropyPointer = Marshal.AllocHGlobal(Entropy.Length);
        var output = default(DataBlob);
        try
        {
            Marshal.Copy(input, 0, inputPointer, input.Length);
            Marshal.Copy(Entropy, 0, entropyPointer, Entropy.Length);
            var inputBlob = new DataBlob { Length = input.Length, Data = inputPointer };
            var entropyBlob = new DataBlob { Length = Entropy.Length, Data = entropyPointer };
            var succeeded = protect
                ? CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden,
                    out output);
            if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error());
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(inputPointer);
            Marshal.FreeHGlobal(entropyPointer);
            if (output.Data != IntPtr.Zero) _ = LocalFree(output.Data);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Length;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? dataDescription, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr dataDescription, ref DataBlob optionalEntropy,
        IntPtr reserved, IntPtr promptStruct, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

/// <summary>
/// The Secret Service (GNOME Keyring or KWallet) through libsecret's secret-tool, with the attributes
/// application=sharpturns and name=the secret's name. It fails when no Secret Service is running.
/// </summary>
internal sealed class LinuxSecretToolSecretStore(string secretToolPath) : ISecretStore
{
    public string Description => "Secret Service keyring (secret-tool)";

    public async Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default)
    {
        var (exitCode, output, error) = await RunAsync(null, cancellationToken, "lookup", "application", "sharpturns", "name", name);
        // lookup fails without a message when there's no such secret.
        if (exitCode != 0 && error.Length == 0) return null;
        ThrowIfFailed(exitCode, error);
        return output;
    }

    // store reads the secret from stdin, which keeps it off the command line.
    public async Task WriteAsync(string name, string? value, CancellationToken cancellationToken = default)
    {
        var (exitCode, _, error) = value is null
            ? await RunAsync(null, cancellationToken, "clear", "application", "sharpturns", "name", name)
            : await RunAsync(value, cancellationToken, "store", "--label=SharpTurns " + name, "application", "sharpturns", "name", name);
        if (value is null && error.Length == 0) return;
        ThrowIfFailed(exitCode, error);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunAsync(string? input, CancellationToken cancellationToken,
        params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(secretToolPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Couldn't start secret-tool.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        if (input is not null) await process.StandardInput.WriteAsync(input);
        process.StandardInput.Close();
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output, (await error).Trim());
    }

    private static void ThrowIfFailed(int exitCode, string error)
    {
        if (exitCode != 0)
            throw new InvalidOperationException($"secret-tool failed (exit code {exitCode}){(error.Length > 0 ? ": " + error : ".")}");
    }
}
