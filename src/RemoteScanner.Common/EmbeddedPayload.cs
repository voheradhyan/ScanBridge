using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace RemoteScanner.Common;

/// <summary>
/// Files carried inside the executable and written out when it installs itself.
///
/// Three things this product ships cannot be executables, however much simpler one file would
/// be. A TWAIN data source is a DLL the manager loads into the scanning application's process,
/// and it is only ever looked for in the system TWAIN folders. An RDP add-in is a DLL loaded
/// inside mstsc.exe. And a scanner driver's bitness decides the bitness of the process that
/// loads it, so the 32-bit host has to be its own program.
///
/// So they travel as resources and are laid down where Windows insists on finding them. What
/// the user handles is still one file.
/// </summary>
[SupportedOSPlatform("windows")]
public static class EmbeddedPayload
{
    /// <summary>Resource name prefix; the rest is the relative path with '/' as separator.</summary>
    private const string Prefix = "RemoteScanner.Payload.";

    public static bool Contains(string relativePath) => FindName(relativePath) is not null;

    /// <summary>Every payload file carried by this executable, as relative paths.</summary>
    public static IEnumerable<string> Names(Assembly? assembly = null)
        => (assembly ?? Assembly.GetEntryAssembly()!)
            .GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(n => n[Prefix.Length..]);

    /// <summary>
    /// Writes one carried file to <paramref name="destination"/>, creating directories as
    /// needed. Returns the SHA-256 of what was written, so an installer can report what it
    /// placed rather than merely that it tried.
    /// </summary>
    public static string Extract(string relativePath, string destination)
    {
        string? name = FindName(relativePath)
            ?? throw new FileNotFoundException(
                $"This build does not carry '{relativePath}'. It was assembled without the " +
                "native components; rebuild with installer\\Build-All.ps1.");

        using Stream source = OwningAssembly(name).GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource '{name}' could not be opened.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Written through a temporary file in the same directory and moved into place, so a
        // half-written data source is never visible to a TWAIN manager scanning that folder.
        string staging = destination + ".new";
        using (FileStream target = File.Create(staging))
        {
            source.CopyTo(target);
        }

        File.Move(staging, destination, overwrite: true);

        using FileStream written = File.OpenRead(destination);
        return Convert.ToHexString(SHA256.HashData(written))[..12];
    }

    private static string? FindName(string relativePath)
    {
        string suffix = Prefix + relativePath.Replace('\\', '/');
        return OwningAssembly(suffix)
            .GetManifestResourceNames()
            .FirstOrDefault(n => string.Equals(n, suffix, StringComparison.OrdinalIgnoreCase));
    }

    private static Assembly OwningAssembly(string _)
        => Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
}
