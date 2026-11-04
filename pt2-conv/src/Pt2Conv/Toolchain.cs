using System.Text.RegularExpressions;

namespace Pt2Conv;

/// <summary>
/// Detects the installed TriStation toolchain so a conversion can state which library version its
/// captured identity data corresponds to. The builder does not call TriStation; this is only a
/// compatibility probe (see docs: captured 5.8 library data must be refreshed for other versions).
/// </summary>
public static class Toolchain
{
    /// <summary>TriStation major.minor this build's captured library data came from.</summary>
    public const string CapturedVersion = "5.8";

    public static IReadOnlyList<string> InstalledVersions()
    {
        var roots = new List<string>();
        string? env = Environment.GetEnvironmentVariable("TS58PROGRAMSDIR");
        if (!string.IsNullOrEmpty(env)) roots.Add(env);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Triconex"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Triconex"));

        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;
            foreach (string dir in SafeDirs(root))
            {
                var m = Regex.Match(Path.GetFileName(dir), @"^TriStation 1131 (\d+\.\d+)");
                if (m.Success) versions.Add(m.Groups[1].Value);
            }
        }
        return versions.OrderBy(v => v, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Compatibility note for the report, or null when the host matches the captured data.</summary>
    public static string? CompatibilityNote()
    {
        var installed = InstalledVersions();
        if (installed.Count == 0)
            return "TriStation was not detected on this machine; using the captured " + CapturedVersion
                   + " library data (conversion still works, but library identity cannot be re-verified).";
        if (!installed.Contains(CapturedVersion))
            return "Installed TriStation " + string.Join("/", installed) + " differs from the captured "
                   + CapturedVersion + " library data; refresh the capture before publishing.";
        var newer = installed.Where(v => string.Compare(v, CapturedVersion, StringComparison.Ordinal) > 0).ToList();
        if (newer.Count > 0)
            return "A newer TriStation is installed (" + string.Join("/", newer) + "); this build still uses the "
                   + CapturedVersion + " library identity data.";
        return null;
    }

    private static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    /// <summary>Checks the installed TriStation 5.8 library files against the fingerprint captured
    /// with this build. A changed library means the captured element identity data (DogTags, event
    /// ids, pin tables) may be stale, so the project must be re-captured before publishing.</summary>
    public static List<string> VerifyLibraries()
    {
        var notes = new List<string>();
        string? data = FindDataDir();
        if (data == null)
        {
            notes.Add("TriStation 5.8 data directory not found; library fingerprint not checked.");
            return notes;
        }
        var host = HostLibraryFingerprint();
        foreach (var kv in host)
        {
            string path = Path.Combine(data, kv.Key);
            if (!File.Exists(path))
            {
                notes.Add($"Installed library '{kv.Key}' is missing; captured identity data may not apply.");
                continue;
            }
            string hash;
            try { hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(); }
            catch { notes.Add($"Could not read installed library '{kv.Key}'."); continue; }
            if (!hash.Equals(kv.Value, StringComparison.OrdinalIgnoreCase))
                notes.Add($"Installed library '{kv.Key}' differs from the captured 5.8 fingerprint; "
                          + "re-capture the library data before publishing.");
        }
        if (notes.Count == 0)
            notes.Add("Installed TriStation 5.8 libraries match the captured fingerprints.");
        return notes;
    }

    private static string? FindDataDir()
    {
        var roots = new List<string>();
        string? env = Environment.GetEnvironmentVariable("TS58PROGRAMSDIR");
        if (!string.IsNullOrEmpty(env)) roots.Add(Directory.GetParent(env)?.FullName ?? env);
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Triconex", "TriStation 1131 5.8.0"));
        foreach (var root in roots)
        {
            string candidate = Path.Combine(root ?? "", "Data");
            if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, "STDLIB.LT2X"))) return candidate;
        }
        return null;
    }

    private static Dictionary<string, string> HostLibraryFingerprint()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var stream = typeof(Toolchain).Assembly
                .GetManifestResourceStream("Pt2Conv.Resources.library_host.json");
            if (stream == null) return map;
            using var reader = new StreamReader(stream);
            using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            foreach (var f in doc.RootElement.GetProperty("files").EnumerateObject())
                map[f.Name] = f.Value.GetProperty("sha256").GetString() ?? "";
        }
        catch { }
        return map;
    }
}
