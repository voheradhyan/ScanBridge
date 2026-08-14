using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace ScanBridge.Common;

/// <summary>
/// Stores the pre-shared keys that the native components authenticate with.
///
/// Keys are DPAPI-protected under the *current user*, which is what scopes them: only the
/// interactive user of a session can unprotect that session's key, so a different user on
/// the same RDP host cannot impersonate the endpoint even if they can name the pipe.
///
/// Layout, matching what rs_pipe.h reads on the native side:
///   HKCU\Software\ScanBridge                       Secret     (local PC: agent &lt;-&gt; DVC plugin)
///   HKCU\Software\ScanBridge\Session\&lt;sessionId&gt;   Secret     (server: session agent &lt;-&gt; .ds)
///                                                     ClientName (name shown in the scanner list)
/// </summary>
[SupportedOSPlatform("windows")]
public static class SecretStore
{
    private const string LocalKeyPath = @"Software\ScanBridge";
    private const string SecretValueName = "Secret";
    private const string ClientNameValueName = "ClientName";

    public const int KeyLength = 32;

    /// <summary>Key for the local-PC hop: tray agent to the plugin hosted in mstsc.exe.</summary>
    public static byte[] GetOrCreateLocalSecret() => GetOrCreate(LocalKeyPath);

    /// <summary>Key for the server hop: per-session agent to the virtual data source.</summary>
    public static byte[] GetOrCreateSessionSecret(uint sessionId)
        => GetOrCreate(SessionKeyPath(sessionId));

    public static string SessionKeyPath(uint sessionId) => $@"{LocalKeyPath}\Session\{sessionId}";

    private const string PairingValueName = "PairingKey";
    private const string PairingSeedValueName = "PairingSeed";

    /// <summary>
    /// Key shared between this PC and a Remote Desktop server for the direct connection.
    ///
    /// The virtual channel needs no such thing: the plugin runs inside the user's own Remote
    /// Desktop client, as that user, so it can simply read their key. A direct network
    /// connection has no such inheritance — the server is a different machine and a different
    /// account, and it cannot read this user's registry or unprotect their DPAPI blobs.
    ///
    /// So the two ends are paired once, by the person who owns both: this PC shows a code, the
    /// user pastes it into their session on the server, and both sides then hold the same key.
    /// It is deliberately not automatic. A direct connection that any machine on the network
    /// could open would be a way to read somebody's scanner, and the documents on it.
    /// </summary>
    /// <summary>
    /// Client side: the seed this PC keeps so it can show its pairing code again, created on
    /// first use. The key is derived from it; the seed itself never leaves this machine.
    /// </summary>
    public static byte[] GetOrCreatePairingSeed()
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(LocalKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Cannot open HKCU\\{LocalKeyPath}.");

        if (key.GetValue(PairingSeedValueName) is byte[] existing && existing.Length > 0)
        {
            try
            {
                byte[] seed = Dpapi.Unprotect(existing);
                if (seed.Length == PairingCode.SeedLength) return seed;
            }
            catch (CryptographicException)
            {
            }
        }

        byte[] fresh = PairingCode.NewSeed();
        key.SetValue(PairingSeedValueName, Dpapi.Protect(fresh), RegistryValueKind.Binary);
        return fresh;
    }

    /// <summary>Server side: the pairing key, or null when this session has not been paired.</summary>
    public static byte[]? TryGetPairingKey()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(LocalKeyPath);
        if (key?.GetValue(PairingValueName) is not byte[] stored || stored.Length == 0) return null;

        try
        {
            byte[] plaintext = Dpapi.Unprotect(stored);
            return plaintext.Length == KeyLength ? plaintext : null;
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    /// <summary>Stores a pairing key supplied as a code. Used on the server side.</summary>
    public static void SetPairingKey(byte[] key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeyLength)
            throw new ArgumentException($"A pairing key must be {KeyLength} bytes.", nameof(key));

        using RegistryKey subKey = Registry.CurrentUser.CreateSubKey(LocalKeyPath, writable: true)
            ?? throw new InvalidOperationException($"Cannot open HKCU\\{LocalKeyPath}.");

        subKey.SetValue(PairingValueName, Dpapi.Protect(key), RegistryValueKind.Binary);
    }

    /// <summary>
    /// A short, non-reversible tag for a secret, safe to write to a log.
    ///
    /// Exists because "Shared secret mismatch." is otherwise unattributable: it says two peers
    /// disagree without saying which one is out of date, and the only way to tell has been to
    /// restart things until it stops. With both ends logging this at startup, the answer is a
    /// glance at two log lines. It is a truncated hash of a 32-byte random key, so it discloses
    /// nothing usable.
    /// </summary>
    public static string Fingerprint(byte[] secret)
    {
        ArgumentNullException.ThrowIfNull(secret);
        return Convert.ToHexString(SHA256.HashData(secret))[..8];
    }

    /// <summary>
    /// Publishes the client machine name so the data source can title itself
    /// "ScanBridge (DESKTOP-XXXX)" before it has connected to anything.
    /// </summary>
    public static void SetSessionClientName(uint sessionId, string clientName)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(SessionKeyPath(sessionId), writable: true)
            ?? throw new InvalidOperationException("Cannot create the session registry key.");
        key.SetValue(ClientNameValueName, clientName, RegistryValueKind.String);
    }

    /// <summary>Removes a session's key material. Called when the session ends.</summary>
    public static void ClearSession(uint sessionId)
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(SessionKeyPath(sessionId), throwOnMissingSubKey: false);
        }
        catch (UnauthorizedAccessException)
        {
            // Losing the race with session teardown is not worth failing a shutdown path over.
        }
    }

    private static byte[] GetOrCreate(string keyPath, string valueName = SecretValueName)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true)
            ?? throw new InvalidOperationException($"Cannot open HKCU\\{keyPath}.");

        if (key.GetValue(valueName) is byte[] existing && existing.Length > 0)
        {
            try
            {
                byte[] plaintext = Dpapi.Unprotect(existing);
                if (plaintext.Length == KeyLength) return plaintext;
                CryptographicOperations.ZeroMemory(plaintext);
            }
            catch (CryptographicException)
            {
                // Written by a different user, or the profile was reset. Replacing it is
                // correct: a key we cannot decrypt is a key no peer of ours can be using.
            }
        }

        byte[] secret = RandomNumberGenerator.GetBytes(KeyLength);
        key.SetValue(valueName, Dpapi.Protect(secret), RegistryValueKind.Binary);
        return secret;
    }
}

/// <summary>
/// Thin DPAPI wrapper. P/Invoked rather than taken from the
/// System.Security.Cryptography.ProtectedData package so the components that run as a
/// service carry no extra assemblies, and so the blob format provably matches the
/// CryptUnprotectData call the native side makes with no entropy argument.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Dpapi
{
    public static byte[] Protect(ReadOnlySpan<byte> plaintext)
        => Transform(plaintext, protect: true);

    public static byte[] Unprotect(ReadOnlySpan<byte> ciphertext)
        => Transform(ciphertext, protect: false);

    private static unsafe byte[] Transform(ReadOnlySpan<byte> input, bool protect)
    {
        if (input.IsEmpty) return Array.Empty<byte>();

        IntPtr buffer = Marshal.AllocHGlobal(input.Length);
        try
        {
            input.CopyTo(new Span<byte>(buffer.ToPointer(), input.Length));

            var inBlob = new Win32.DATA_BLOB { cbData = input.Length, pbData = buffer };

            bool ok = protect
                ? Win32.CryptProtectData(ref inBlob, "ScanBridge", IntPtr.Zero, IntPtr.Zero,
                                         IntPtr.Zero, Win32.CRYPTPROTECT_UI_FORBIDDEN, out Win32.DATA_BLOB outBlob)
                : Win32.CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                           IntPtr.Zero, Win32.CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

            if (!ok)
            {
                throw new CryptographicException(
                    protect ? "CryptProtectData failed." : "CryptUnprotectData failed.",
                    new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()));
            }

            try
            {
                var result = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                return result;
            }
            finally
            {
                // The plaintext side of this blob must not linger in the process heap.
                new Span<byte>(outBlob.pbData.ToPointer(), outBlob.cbData).Clear();
                Win32.LocalFree(outBlob.pbData);
            }
        }
        finally
        {
            new Span<byte>(buffer.ToPointer(), input.Length).Clear();
            Marshal.FreeHGlobal(buffer);
        }
    }
}
