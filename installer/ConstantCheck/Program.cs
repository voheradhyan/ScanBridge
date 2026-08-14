// Checks every TWAIN constant in rs_twain.h and TwainTypes.cs against a real TWAIN library.
//
// Why this exists
// ---------------
// A wrong constant is the one class of mistake this project cannot catch by testing itself.
// The data source manager forwards DG/DAT/MSG untouched, so a data source and a probe that
// share a header agree with each other perfectly while disagreeing with every real
// application. Six wrong values in rs_twain.h survived 67 unit tests, a real-manager discovery
// gate and end-to-end scans in three transfer mechanisms; they were found only by reading a
// log from the user's machine, twice, days apart.
//
// So the numbers are checked against something outside this repository: the enums compiled
// into NTwain.dll, the interop layer NAPS2 itself calls through. Same reasoning as the
// dsmprobe gate - measure against a real implementation, never against ourselves.
//
// Exit codes: 0 pass or skipped, 1 mismatch, 2 could not run.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace ScanBridge.ConstantCheck;

internal static class Program
{
    /// Which NTwain enum answers for which prefix in our headers. A prefix absent here is not
    /// checked at all.
    private static readonly Dictionary<string, string> Families = new()
    {
        ["DG_"] = "DataGroups",        ["DAT_"] = "DataArgumentType", ["MSG_"] = "Message",
        ["TWRC_"] = "ReturnCode",      ["TWCC_"] = "ConditionCode",   ["TWTY_"] = "ItemType",
        ["TWON_"] = "ContainerType",   ["ICAP_"] = "CapabilityId",    ["CAP_"] = "CapabilityId",
        ["TWPT_"] = "PixelType",       ["TWSX_"] = "XferMech",        ["TWUN_"] = "Unit",
        ["TWCP_"] = "CompressionType", ["TWFF_"] = "FileFormat",      ["TWSS_"] = "SupportedSize",
        ["TWOR_"] = "OrientationType", ["TWBO_"] = "BitOrder",        ["TWPF_"] = "PixelFlavor",
        ["TWPC_"] = "PlanarChunky",    ["TWQC_"] = "QuerySupports",   ["TWLG_"] = "Language",
        ["TWCY_"] = "Country",
    };

    /// Names our headers spell differently from NTwain. Each is a genuine synonym - only the
    /// lookup name is translated, the number is still compared.
    private static readonly Dictionary<string, string> Aliases = new()
    {
        ["TWTY_STR32"] = "String32",   ["TWTY_STR64"] = "String64",
        ["TWTY_STR128"] = "String128", ["TWTY_STR255"] = "String255",
        ["TWON_ENUMERATION"] = "Enum", ["TWON_DONTCARE16"] = "DoNotCare",
        ["TWPT_BW"] = "BlackWhite",    ["TWPT_GRAY"] = "Gray",
        ["TWSS_A4LETTER"] = "A4",      ["TWSS_B5LETTER"] = "JisB5",
        ["TWSS_B4"] = "IsoB4",         ["TWSS_B6"] = "IsoB6", ["TWSS_B3"] = "IsoB3",
    };

    /// Constants with no counterpart in NTwain. Listed so that "not checked" is a decision
    /// recorded here rather than an accident of name matching.
    private static readonly HashSet<string> NotCovered = new()
    {
        "DAT_METRICS",                                    // TWAIN 2.3; NTwain does not carry it
        "DAT_CIECOLOR", "DAT_GRAYRESPONSE", "DAT_RGBRESPONSE", "DAT_JPEGCOMPRESSION",
        "DAT_PALETTE8", "DAT_FILTER", "DAT_ICCPROFILE",   // named for completeness, never dispatched
        "TWON_DONTCARE8", "TWON_DONTCARE32",
        "DF_DSM2", "DF_APP2", "DF_DS2",
    };

    private static int Main(string[] args)
    {
        string repo = args.Length > 0
            ? args[0]
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

        string? ntwain = args.Length > 1 ? args[1] : FindNTwain();
        if (ntwain is null || !File.Exists(ntwain))
        {
            Console.WriteLine("    ! NTwain.dll not found (NAPS2 not installed); constants NOT verified.");
            return 0;
        }

        Dictionary<string, long> reference;
        try
        {
            reference = ReadEnums(ntwain);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    ! could not read {ntwain}: {ex.Message}");
            return 2;
        }

        if (reference.Count == 0)
        {
            Console.WriteLine($"    ! no constants found in {ntwain}");
            return 2;
        }

        var sources = new[]
        {
            Path.Combine(repo, "native", "include", "rs_twain.h"),
            Path.Combine(repo, "src", "ScanBridge.Scanner", "Twain", "TwainTypes.cs"),
        };

        var wrong = new List<string>();
        int checkedCount = 0, skipped = 0;

        foreach (string file in sources)
        {
            if (!File.Exists(file)) { Console.WriteLine($"    ! missing {file}"); return 2; }

            bool isHeader = file.EndsWith(".h", StringComparison.OrdinalIgnoreCase);
            var pattern = isHeader
                ? new Regex(@"^\s*#define\s+([A-Z0-9_]+)\s+(0x[0-9a-fA-F]+|\d+)L?\s*(/\*.*)?$")
                : new Regex(@"const\s+\w+\s+([A-Z0-9_]+)\s*=\s*(0x[0-9a-fA-F]+|\d+)\s*;");

            foreach (string line in File.ReadLines(file))
            {
                var match = pattern.Match(line);
                if (!match.Success) continue;

                string name = match.Groups[1].Value;
                string raw = match.Groups[2].Value;
                if (NotCovered.Contains(name)) { skipped++; continue; }

                long value = raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? Convert.ToInt64(raw[2..], 16)
                    : long.Parse(raw);

                string? prefix = Families.Keys
                    .Where(p => name.StartsWith(p, StringComparison.Ordinal))
                    .OrderByDescending(p => p.Length)
                    .FirstOrDefault();
                if (prefix is null) { skipped++; continue; }

                string family = Families[prefix];
                var lookups = new List<string>();
                if (Aliases.TryGetValue(name, out string? alias)) lookups.Add(Normalise(alias));
                lookups.Add(Normalise(name[prefix.Length..]));
                lookups.Add(Normalise(name));

                long? found = null;
                foreach (string lookup in lookups)
                {
                    if (reference.TryGetValue($"{family}|{lookup}", out long v)) { found = v; break; }
                }

                if (found is null) { skipped++; continue; }

                checkedCount++;
                if (found != value)
                {
                    wrong.Add($"{name,-24} in {Path.GetFileName(file)}: " +
                              $"ours 0x{value:X4}, real 0x{found:X4}");
                }
            }
        }

        Console.WriteLine($"    {checkedCount} constant(s) verified against " +
                          $"{Path.GetFileName(ntwain)}, {skipped} not covered");

        if (wrong.Count == 0) return 0;

        Console.WriteLine();
        foreach (string w in wrong) Console.WriteLine($"    WRONG: {w}");
        Console.WriteLine();
        Console.WriteLine($"    {wrong.Count} TWAIN constant(s) disagree with a real TWAIN library.");
        Console.WriteLine("    Nothing else in this repository can catch this: the manager forwards");
        Console.WriteLine("    DG/DAT/MSG untouched, so our own tools reproduce the mistake and pass.");
        return 1;
    }

    private static string Normalise(string name)
    {
        Span<char> buffer = stackalloc char[name.Length];
        int length = 0;
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c)) buffer[length++] = char.ToUpperInvariant(c);
        }
        return new string(buffer[..length]);
    }

    private static string? FindNTwain()
    {
        string[] roots =
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
        };

        return roots.Select(root => Path.Combine(root, "NAPS2", "lib", "NTwain.dll"))
                    .FirstOrDefault(File.Exists);
    }

    /// Reads enum constants out of the assembly's metadata. MetadataLoadContext is used rather
    /// than a normal load because this needs the numbers only, and must never run code from a
    /// third party's DLL as a side effect of building.
    private static Dictionary<string, long> ReadEnums(string assemblyPath)
    {
        var paths = new List<string>(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"))
        {
            assemblyPath,
        };

        using var context = new MetadataLoadContext(new PathAssemblyResolver(paths));
        Assembly assembly = context.LoadFromAssemblyPath(assemblyPath);

        var values = new Dictionary<string, long>();
        foreach (Type type in assembly.GetTypes())
        {
            if (!type.IsEnum) continue;

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                object? raw;
                try { raw = field.GetRawConstantValue(); } catch { continue; }
                if (raw is null) continue;

                long value;
                try { value = Convert.ToInt64(raw); } catch { continue; }

                string key = $"{type.Name}|{Normalise(field.Name)}";
                if (!values.ContainsKey(key)) values[key] = value;
            }
        }
        return values;
    }
}
