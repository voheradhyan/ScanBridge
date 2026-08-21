using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ScanBridge.Common;

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
    private const string Prefix = "ScanBridge.Payload.";

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
    /// <summary>
    /// The hash of a carried file, without writing it anywhere.
    ///
    /// Lets an installer answer "would replacing this file actually change anything" before it
    /// has modified a thing — which is the difference between refusing an upgrade cleanly and
    /// failing halfway through one.
    /// </summary>
    public static string? HashOf(string relativePath)
    {
        string? name = FindName(relativePath);
        if (name is null) return null;

        using Stream? source = OwningAssembly(name).GetManifestResourceStream(name);
        if (source is null) return null;

        return Convert.ToHexString(SHA256.HashData(source))[..12];
    }

    /// <summary>The hash of a file on disk, or null if it is not there or cannot be read.</summary>
    public static string? HashOfFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using FileStream file = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(file))[..12];
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static string Extract(string relativePath, string destination)
    {
        string? name = FindName(relativePath)
            ?? throw new FileNotFoundException(
                $"This build does not carry '{relativePath}'. It was assembled without the " +
                "native components; rebuild with installer\\Build-All.ps1.");

        using Stream source = OwningAssembly(name).GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource '{name}' could not be opened.");

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Read the resource once. Needed anyway to hash it, and it lets the identical-file case
        // below be answered without touching the destination at all.
        using var carried = new MemoryStream();
        source.CopyTo(carried);
        byte[] bytes = carried.ToArray();
        string hash = Convert.ToHexString(SHA256.HashData(bytes))[..12];

        // Already exactly this file? Then leave it alone.
        //
        // Not an optimisation. Two of these files are loaded into processes we do not control —
        // ScanBridge.DvcPlugin.dll lives inside mstsc.exe and ScanBridge.ds inside whatever
        // application is scanning — and Windows will not let a loaded image be replaced. So
        // reinstalling the same build while a Remote Desktop session was open failed with
        // "Access to the path is denied" partway through, leaving the install half-applied.
        // When the bytes match there is nothing to replace and no reason to fail.
        if (File.Exists(destination))
        {
            try
            {
                using FileStream existing = File.OpenRead(destination);
                if (Convert.ToHexString(SHA256.HashData(existing))[..12] == hash) return hash;
            }
            catch (IOException)
            {
                // Cannot even read it; fall through and let the write produce the real error.
            }
        }

        // Written through a temporary file in the same directory and moved into place, so a
        // half-written data source is never visible to a TWAIN manager scanning that folder.
        string staging = destination + ".new";
        using (FileStream target = File.Create(staging))
        {
            target.Write(bytes, 0, bytes.Length);
        }

        try
        {
            File.Move(staging, destination, overwrite: true);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            File.Delete(staging);

            // Name the process holding it. "Access to the path is denied" is true and useless;
            // what the person needs to know is that their Remote Desktop session has the add-in
            // loaded and they have to close it.
            string holders = FileLock.DescribeHolders(destination);
            throw new IOException(
                $"{Path.GetFileName(destination)} is in use and could not be replaced{holders}.",
                ex);
        }

        return hash;
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
