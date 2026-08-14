using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace ScanBridge.Scanner.Twain;

/// <summary>
/// Binds the TWAIN Data Source Manager and exposes one typed call per pData shape.
///
/// DSM_Entry is a single C entry point whose last argument is a different structure for
/// almost every operation. There is no way to express that in one P/Invoke signature, so
/// each shape gets its own delegate. Blitting the wrong one silently corrupts the driver's
/// view of the call, hence the deliberate verbosity.
///
/// The DSM is resolved at runtime rather than through DllImport so we can prefer the copy
/// shipped beside the application over whatever a scanner vendor left in System32.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class TwainDsm : IDisposable
{
    private readonly IntPtr _module;

    private TwainDsm(IntPtr module, string path)
    {
        _module = module;
        Path = path;

        IntPtr entry = NativeLibrary.GetExport(module, "DSM_Entry");

        Parent = Marshal.GetDelegateForFunctionPointer<DsmParent>(entry);
        Identity = Marshal.GetDelegateForFunctionPointer<DsmIdentity>(entry);
        IdentityPair = Marshal.GetDelegateForFunctionPointer<DsmIdentityPair>(entry);
        Capability = Marshal.GetDelegateForFunctionPointer<DsmCapability>(entry);
        UserInterface = Marshal.GetDelegateForFunctionPointer<DsmUserInterface>(entry);
        Event = Marshal.GetDelegateForFunctionPointer<DsmEvent>(entry);
        PendingXfers = Marshal.GetDelegateForFunctionPointer<DsmPendingXfers>(entry);
        ImageInfo = Marshal.GetDelegateForFunctionPointer<DsmImageInfo>(entry);
        NativeXfer = Marshal.GetDelegateForFunctionPointer<DsmNativeXfer>(entry);
        Status = Marshal.GetDelegateForFunctionPointer<DsmStatus>(entry);
        SetupMemXfer = Marshal.GetDelegateForFunctionPointer<DsmSetupMemXfer>(entry);
        MemXfer = Marshal.GetDelegateForFunctionPointer<DsmMemXfer>(entry);
        EntryPoint = Marshal.GetDelegateForFunctionPointer<DsmEntryPoint>(entry);
    }

    public string Path { get; }

    /// <summary>
    /// Loads the DSM, or returns null with a reason when TWAIN is unavailable on this PC.
    ///
    /// Search order:
    ///   1. TWAINDSM.dll next to our own binaries — the version we ship and have tested.
    ///   2. TWAINDSM.dll on the default search path (installed by many scanner vendors).
    ///   3. twain_32.dll — the DSM Windows itself ships. 32-bit only, so it is reachable
    ///      only from the x86 build of ScanHost; that is exactly why ScanHost is built twice.
    /// </summary>
    public static TwainDsm? TryLoad(out string diagnostic)
    {
        string here = AppContext.BaseDirectory;
        var candidates = new List<string> { System.IO.Path.Combine(here, "TWAINDSM.dll"), "TWAINDSM.dll" };

        if (!Environment.Is64BitProcess) candidates.Add("twain_32.dll");

        var failures = new List<string>();

        foreach (string candidate in candidates)
        {
            try
            {
                if (!NativeLibrary.TryLoad(candidate, out IntPtr module))
                {
                    failures.Add($"{candidate}: not found");
                    continue;
                }

                try
                {
                    var dsm = new TwainDsm(module, candidate);
                    diagnostic = $"Loaded TWAIN DSM '{candidate}'.";
                    return dsm;
                }
                catch (EntryPointNotFoundException)
                {
                    NativeLibrary.Free(module);
                    failures.Add($"{candidate}: no DSM_Entry export");
                }
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        diagnostic = Environment.Is64BitProcess
            ? "No 64-bit TWAIN DSM found (" + string.Join("; ", failures) +
              "). 64-bit TWAIN needs TWAINDSM.dll; 32-bit scanner drivers are reached through the x86 host instead."
            : "No 32-bit TWAIN DSM found (" + string.Join("; ", failures) + ").";
        return null;
    }

    public void Dispose()
    {
        if (_module != IntPtr.Zero) NativeLibrary.Free(_module);
    }

    // Each of these is the same function pointer viewed through a different final argument.
    // StdCall is correct on x86 and collapses to the single native convention on x64.

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmParent(ref TW_IDENTITY origin, IntPtr dest, uint dg, ushort dat, ushort msg, ref IntPtr hwnd);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmIdentity(ref TW_IDENTITY origin, IntPtr dest, uint dg, ushort dat, ushort msg, ref TW_IDENTITY data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmIdentityPair(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, IntPtr data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmCapability(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_CAPABILITY data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmUserInterface(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_USERINTERFACE data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmEvent(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_EVENT data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmPendingXfers(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_PENDINGXFERS data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmImageInfo(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_IMAGEINFO data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmNativeXfer(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmStatus(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_STATUS data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmSetupMemXfer(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_SETUPMEMXFER data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmMemXfer(ref TW_IDENTITY origin, ref TW_IDENTITY dest, uint dg, ushort dat, ushort msg, ref TW_IMAGEMEMXFER data);

    [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public delegate ushort DsmEntryPoint(ref TW_IDENTITY origin, IntPtr dest, uint dg, ushort dat, ushort msg, ref TW_ENTRYPOINT data);

    public DsmParent Parent { get; }
    public DsmIdentity Identity { get; }
    public DsmIdentityPair IdentityPair { get; }
    public DsmCapability Capability { get; }
    public DsmUserInterface UserInterface { get; }
    public DsmEvent Event { get; }
    public DsmPendingXfers PendingXfers { get; }
    public DsmImageInfo ImageInfo { get; }
    public DsmNativeXfer NativeXfer { get; }
    public DsmStatus Status { get; }
    public DsmSetupMemXfer SetupMemXfer { get; }
    public DsmMemXfer MemXfer { get; }
    public DsmEntryPoint EntryPoint { get; }
}
