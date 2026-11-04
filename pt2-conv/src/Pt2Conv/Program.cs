using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pt2Conv.Cfb;

namespace Pt2Conv;

public static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 0;
            }
            return args[0].ToLowerInvariant() switch
            {
                "extract" => Extract(args),
                "rebuild" => Rebuild(args),
                "verify" => Verify(args),
                "diff" => Diff(args),
                "selftest" => SelfTest(),
                "crc" => Crc(args),
                "buildxml" => BuildXml.Run(args),
                "fromsimatic" => SimaticToPlcOpen.Run(args),
                "frompt2" => FromPt2.Run(args),
                "tojson" => FromPt2.ToJson(args),
                "topdf" => ToPdf.Run(args),
                "toreport" => ToReport.Run(args),
                "genfbd" => FbdGenerator.Run(args),
                "captureboard" => HwConfig.Capture(args),
                "capturehw" => HwCapture.Run(args),
                "genst" => StGenerator.Run(args),
                "gendoc" => DocGenerator.Run(args),
                "analyze" => Analyze.Run(args),
                "metacrc" => Metacrc.Run(args),
                "rbtree" => RbTreeCheck.Run(args),
                "dir" => DirDump.Run(args),
                "dirdiff" => DirDump.RunDiff(args),
                "catalog" => Catalog.Run(args),
                "addpou" => AddPou.Run(args),
                "tags" => Tags.Run(args),
                "tagmap" => TagMap.Run(args),
                "tagpairs" => TagMap.RunPairs(args),
                "gentags" => TagGen.Run(args),
                "tagrecon" => TagRecon.Run(args),
                "tagrecon-dump" => TagRecon.RunDump(args),
                "tagmatrix" => TagMatrix.Run(args),
                "findtemplate" => FindTemplate.Run(args),
                "base" => BaseCmd(args),
                "check" => Capabilities.Run(args),
                "stlist" => StList.Run(args),
                "help" or "-h" or "--help" => PrintUsageSafe(),
                _ => PrintUnknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            if (Environment.GetEnvironmentVariable("PT2_VERBOSE") == "1")
                File.WriteAllText(@"C:\Users\Moatasem\AppData\Local\Temp\opencode\pt2_stack.txt", ex.ToString());
            return 2;
        }
    }

    private static int PrintUsageSafe() { PrintUsage(); return 0; }
    private static int PrintUnknown(string c) { Console.Error.WriteLine($"Unknown command '{c}'"); PrintUsage(); return 1; }

    /// <summary>Writes the embedded generic base project (.pt2x) or extracts it to a directory.</summary>
    private static int BaseCmd(string[] args)
    {
        if (args.Length < 2 || args[1].Equals("--extract", StringComparison.OrdinalIgnoreCase))
        {
            string dir = args.Length >= 3 ? args[2] : Path.Combine(Path.GetTempPath(), "pt2conv_base");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            EmbeddedBase.ExtractTo(dir);
            Console.WriteLine($"Extracted embedded base to {dir}");
            return 0;
        }
        string outPath = args[1];
        if (File.Exists(outPath)) { Console.Error.WriteLine("ERROR: output file already exists: " + outPath); return 3; }
        File.WriteAllBytes(outPath, EmbeddedBase.LoadPt2xBytes());
        Console.WriteLine($"Wrote embedded base to {outPath}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
"""
pt2conv — OLE/CFB (TriStation .pt2/.pt2x) container tool and PLCopen-XML build tool.

Commands:
  extract <file.pt2x> <outdir>          Extract every stream to <outdir> (hierarchy preserved)
                                       and write _manifest.json.
  rebuild <outdir> <out.pt2x>          Build a fresh OLE/CFB from the extracted tree.
                                       Recomputes every *_crc stream automatically.
  verify <file.pt2x>                   Validate structure + every *_crc SHA-256 pair.
  diff   <a.pt2x> <b.pt2x>             Stream-level diff between two container files.
  crc    <file.pt2x>                   Print raw container summary (STEP-1 style).
  selftest                             Extract->rebuild->re-extract round-trip test.
  buildxml <xml> <out.pt2x> [<refExtractDir>] [<pouFilter>] [--tags] [--insert] [--export]
                                       Build a project container from PLCopen XML. With no
                                       <refExtractDir> it uses the embedded blank project
                                       (library + hardware scaffold, no POUs), synthesises
                                       the catalog entries TriStation's importer creates,
                                       and regenerates every POU + Tagnames.csv.
  fromsimatic <dir|file> <out.xml>     Translate SimaticML block exports (FBD) + tag table into
                                       the TriStation PLCopen XML that buildxml consumes.
  frompt2 <project.pt2x> <out.xml>     Decompile a TriStation .pt2/.pt2x container (.FBD/.ST
                                       POU streams + Tagnames.csv) into TriStation PLCopen XML.
                                       In-process: needs no TriStation installation or licence.
  tojson <project.pt2x> <out.json>     Decompile a TriStation .pt2/.pt2x container to a JSON model
                                       (POUs, vars, FBD sheets/wires, tags) for the web viewer.
  topdf  <project.pt2x> <out.pdf>      Render the decompiled FBD sheets (frame, title block,
                                       blocks, wires, comments) to a PDF. In-process, no licence.
  toreport <project.pt2x> <out.pdf>    Render the TriStation report suite (POU Variables, Instance
                                       Variable Connections, Program Variable Listing, Tagnames
                                       Complete Listing, POU Cross-Reference, Shared Libraries,
                                       Project Implementation) from the embedded .trdp templates.
  genfbd <xml> <pouName> <out.FBD>     Generate one FBD container stream from a PLCopen-XML POU.
  genst  <xml> <pouName> <out.ST>      Generate one ST container stream (CDATA + CRLF) from a PLCopen-XML POU.
  gendoc <xml> <pouName> <out.DOC>      Generate one CTS2Doc container stream from a PLCopen-XML POU.
  dir    <file.pt2x>                   Dump CFB directory/catalog entries (ids, links, colors, allocation).
  dirdiff <a.pt2x> <b.pt2x>            Structural directory/catalog diff between two containers.
  catalog <file.pt2x> [libID]          Parse Current/Project.xml POU catalog; test EMOCRC32 hypotheses.
  addpou <tmpl> <out> <srcPOU> <new>   [PHASE-1 PROBE] clone a POU folder+entry under a new name/DogTag.
  findtemplate <xml> <searchDir>        Find a .pt2x whose POU catalog matches the XML.
  base <out.pt2x> | base --extract [dir]  Emit/extract the embedded generic base project.
  check <xml>                           Pre-flight: report what the builder cannot convert.
""");
    }

    // ---------------------------------------------------------------- extract

    private static int Extract(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: extract <file> <outdir>"); return 1; }
        var cf = CompoundFile.Open(args[1]);
        var dir = args[2];
        if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any())
        {
            Console.Error.WriteLine("ERROR: output directory is not empty: " + dir);
            return 1;
        }
        Directory.CreateDirectory(dir);

        var manifest = new List<StreamRecord>();
        foreach (var s in cf.ListStreams())
        {
            string full = Path.Combine(dir, s.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, s.Data);
            manifest.Add(new StreamRecord(s.Path, s.Data.Length, Sha256Hex(s.Data)));
        }

        Console.WriteLine($"Extracted {manifest.Count} streams into {dir}");
        Console.WriteLine($"  total bytes: {manifest.Sum(m => (long)m.size):N0}");
        Console.WriteLine($"  FAT sectors: {cf.FatSectorCount}, dir sectors: {cf.DirSectorCount}, " +
                          $"miniFAT sectors: {cf.MiniFatSectorCount}, entries: {cf.Entries.Count}");
        var doc = new {
            source = Path.GetFullPath(args[1]),
            version = 1,
            streams = manifest,
        };
        File.WriteAllText(Path.Combine(dir, "_manifest.json"),
            JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // ---------------------------------------------------------------- rebuild

    private static int Rebuild(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: rebuild <outdir> <out.pt2x>"); return 1; }
        var dir = args[1];
        var streams = ReadTree(dir);
        RewriteCrcs(streams);
        RewriteGlobalCrc(streams);
        CompoundFileWriter.Write(args[2], streams);
        Console.WriteLine($"Wrote {args[2]} ({streams.Count} streams)");
        return 0;
    }

    /// <summary>Walks an extracted tree, returns (path-relative-to-root, bytes) preserving manifest order.</summary>
    public static List<(string Path, byte[] Data)> ReadTree(string dir)
    {
        var list = new List<(string, byte[])>();
        var manifest = Path.Combine(dir, "_manifest.json");
        if (File.Exists(manifest))
        {
            var doc = JsonSerializer.Deserialize<ManifestDoc>(
                File.ReadAllText(manifest), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var r in doc!.streams)
            {
                string full = Path.Combine(dir, r.path.Replace('/', Path.DirectorySeparatorChar));
                list.Add((r.path, File.ReadAllBytes(full)));
            }
            return list;
        }
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            if (Path.GetFileName(f) == "_manifest.json") continue;
            string rel = Path.GetRelativePath(dir, f).Replace('\\', '/');
            list.Add((rel, File.ReadAllBytes(f)));
        }
        return list;
    }

    /// <summary>Recomputes sibling *_crc streams so TriStation's integrity check passes.</summary>
    public static void RewriteCrcs(IList<(string Path, byte[] Data)> streams)
    {
        var byName = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, d) in streams)
            byName[p] = d;
        for (int i = 0; i < streams.Count; i++)
        {
            var (p, d) = streams[i];
            if (!p.EndsWith("_crc", StringComparison.OrdinalIgnoreCase)) continue;
            string sibling = p[..^4];
            // Unknown semantics (e.g. orphan crc without a sibling): preserve original bytes.
            if (!byName.TryGetValue(sibling, out var b)) continue;
            streams[i] = (p, Encoding.ASCII.GetBytes(Sha256Hex(b)));
        }
    }

    /// <summary>
    /// Recomputes the top-level "meta_crc" stream. Reverse-engineered from
    /// CTS2GlobalCRCManager::GenerateGlobalCRC + UTL_GenSHA256:
    ///   meta_crc = SHA256( concat( lowercase-sha256-hex(data-stream) ) )
    /// over every stream that is neither a sibling *_crc nor "meta_crc", ordered by
    /// path using a case-insensitive comparison, with no separator.
    /// </summary>
    public static void RewriteGlobalCrc(IList<(string Path, byte[] Data)> streams)
    {
        var entries = new List<(string Path, string Sha)>();
        foreach (var (p, d) in streams)
        {
            if (p.EndsWith("_crc", StringComparison.OrdinalIgnoreCase)) continue;
            if (Path.GetFileName(p).Equals("meta_crc", StringComparison.OrdinalIgnoreCase)) continue;
            entries.Add((p, Sha256Hex(d)));
        }
        entries.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder(entries.Count * 64);
        foreach (var e in entries) sb.Append(e.Sha);
        byte[] bytes = Encoding.ASCII.GetBytes(Sha256Hex(Encoding.ASCII.GetBytes(sb.ToString())));

        int idx = -1;
        for (int i = 0; i < streams.Count; i++)
            if (Path.GetFileName(streams[i].Path).Equals("meta_crc", StringComparison.OrdinalIgnoreCase)) { idx = i; break; }
        if (idx >= 0) streams[idx] = ("meta_crc", bytes);
        else streams.Add(("meta_crc", bytes));
    }

    // ---------------------------------------------------------------- verify

    private static int Verify(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: verify <file>"); return 1; }
        var cf = CompoundFile.Open(args[1]);
        var streams = cf.ListStreams();
        Console.WriteLine($"entries: {cf.Entries.Count}");
        Console.WriteLine($"FAT sectors: {cf.FatSectorCount}, dir: {cf.DirSectorCount}, miniFAT: {cf.MiniFatSectorCount}");
        Console.WriteLine($"streams: {streams.Count}, total bytes: {streams.Sum(s => (long)s.Data.Length):N0}");

        var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var crcs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in streams)
        {
            if (s.Path.EndsWith("_crc", StringComparison.OrdinalIgnoreCase)) crcs[s.Path[..^4]] = s.Data;
            else data[s.Path] = s.Data;
        }
        int ok = 0, missing = 0, mismatch = 0;
        foreach (var kv in data)
        {
            if (!crcs.TryGetValue(kv.Key, out var stored))
            {
                missing++;
                continue;
            }
            string want = Encoding.ASCII.GetString(stored).Trim().ToLowerInvariant();
            string got = Sha256Hex(kv.Value);
            if (want == got) ok++;
            else
            {
                mismatch++;
                Console.WriteLine($"  CRC MISMATCH: {kv.Key}  stored={want[..16]}… got={got[..16]}…");
            }
        }
        int orphan = crcs.Count(kv => !data.ContainsKey(kv.Key));
        Console.WriteLine($"CRC verified OK: {ok}   mismatches: {mismatch}   data-without-crc: {missing}   orphan-crc: {orphan}");
        int ole = CfbOpenCheck.Check(Path.GetFullPath(args[1]));
        Console.WriteLine($"ole32 container open: 0x{ole:X8} ({(ole == 0 ? "VALID" : "INVALID")})");
        return (mismatch > 0 || ole != 0) ? 2 : 0;
    }

    // ---------------------------------------------------------------- diff

    private static int Diff(string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("usage: diff <a> <b>"); return 1; }
        var a = CompoundFile.Open(args[1]).ListStreams();
        var b = CompoundFile.Open(args[2]).ListStreams();
        var am = a.ToDictionary(x => x.Path, x => (long)x.Data.Length);
        var bm = b.ToDictionary(x => x.Path, x => (long)x.Data.Length);
        var ha = a.ToDictionary(x => x.Path, x => Sha256Hex(x.Data));
        var hb = b.ToDictionary(x => x.Path, x => Sha256Hex(x.Data));

        int added = 0, removed = 0, changed = 0, same = 0;
        foreach (var k in bm.Keys.Except(am.Keys)) { added++; Console.WriteLine($"  + {k} ({bm[k]} B)"); }
        foreach (var k in am.Keys.Except(bm.Keys)) { removed++; Console.WriteLine($"  - {k} ({am[k]} B)"); }
        foreach (var k in am.Keys.Intersect(bm.Keys))
        {
            if (ha[k] == hb[k]) { same++; }
            else
            {
                changed++;
                Console.WriteLine($"  ~ {k}  {am[k]}->{bm[k]} B");
            }
        }
        Console.WriteLine($"same={same} added={added} removed={removed} changed={changed}");
        return 0;
    }

    // ---------------------------------------------------------------- crc (STEP-1 summary)

    private static int Crc(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("usage: crc <file>"); return 1; }
        var cf = CompoundFile.Open(args[1]);
        Console.WriteLine($"signature OK; CFB {cf.MajorVersion}.{cf.MinorVersion}; sector={cf.SectorSize} mini={cf.MiniSectorSize}; " +
                          $"cutoff={cf.MiniStreamCutoff}");
        Console.WriteLine($"FAT sectors={cf.FatSectorCount} firstDir={cf.FirstDirSector} dirSectors={cf.DirSectorCount} " +
                          $"miniFAT={cf.MiniFatSectorCount} firstMiniFAT={cf.FirstMiniFAT} size={cf.TotalFileSize:N0}");
        var streams = cf.ListStreams();
        Console.WriteLine($"streams: {streams.Count}, total bytes: {streams.Sum(s => (long)s.Data.Length):N0}");
        return 0;
    }

    // ---------------------------------------------------------------- selftest

    private static int SelfTest()
    {
        const string src = @"C:\XforceMsw\pt2-study\runA_COPY.PT2X";
        const string work = @"C:\XforceMsw\pt2-conv\work";

        Console.Write("1. extract … ");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        var cf = CompoundFile.Open(src);
        var orig = cf.ListStreams();
        foreach (var s in orig)
        {
            string full = Path.Combine(work, "t", s.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, s.Data);
        }
        Console.WriteLine($"{orig.Count} streams");

        Console.Write("2. rebuild (recompute crc) … ");
        var tree = new List<(string, byte[])>();
        foreach (var s in orig)
        {
            // walk disk tree as produced in step 1 via ReadTree semantics
        }
        var onDisk = ReadTree(Path.Combine(work, "t"));
        RewriteCrcs(onDisk);
        string outFile = Path.Combine(work, "rebuild.pt2x");
        CompoundFileWriter.Write(outFile, onDisk);
        Console.WriteLine($"{outFile} ({new FileInfo(outFile).Length:N0} B)");

        Console.Write("3. re-extract rebuilt … ");
        var cf2 = CompoundFile.Open(outFile);
        var rebuilt = cf2.ListStreams();
        Console.WriteLine($"{rebuilt.Count} streams");

        Console.Write("4. compare all streams … ");
        var d1 = orig.ToDictionary(x => x.Path, x => x.Data, StringComparer.OrdinalIgnoreCase);
        var d2 = rebuilt.ToDictionary(x => x.Path, x => x.Data, StringComparer.OrdinalIgnoreCase);
        int added = d2.Keys.Where(k => !d1.ContainsKey(k)).Count();
        int removed = d1.Keys.Where(k => !d2.ContainsKey(k)).Count();
        int changed = d1.Keys.Where(k => d2.ContainsKey(k) && !d1[k].SequenceEqual(d2[k])).Count();
        Console.WriteLine($"added={added} removed={removed} changed={changed}");
        bool pass = added == 0 && removed == 0 && changed == 0;
        if (!pass)
        {
            foreach (var k in d1.Keys.Where(k => d2.ContainsKey(k) && !d1[k].SequenceEqual(d2[k])).Take(10))
                Console.WriteLine($"   CHANGE {k}: {d1[k].Length}->{d2[k].Length}");
            foreach (var k in d1.Keys.Where(k => !d2.ContainsKey(k)).Take(10))
                Console.WriteLine($"   MISSING {k}");
            foreach (var k in d2.Keys.Where(k => !d1.ContainsKey(k)).Take(10))
                Console.WriteLine($"   EXTRA {k}");
        }

        Console.Write("5. verify rebuilt _crc … ");
        var data = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var crcs = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in rebuilt)
        {
            if (s.Path.EndsWith("_crc", StringComparison.OrdinalIgnoreCase)) crcs[s.Path[..^4]] = s.Data;
            else data[s.Path] = s.Data;
        }
        int ok = data.Count(kv => crcs.TryGetValue(kv.Key, out var c) &&
                                  Encoding.ASCII.GetString(c).Trim().ToLowerInvariant() == Sha256Hex(kv.Value));
        Console.WriteLine($"{ok}/{data.Count}");

        Console.WriteLine(pass && ok == data.Count ? "SELFTEST PASS" : "SELFTEST FAIL");
        return (pass && ok == data.Count) ? 0 : 1;
    }

    public static string Sha256Hex(byte[] data)
    {
        byte[] h = SHA256.HashData(data);
        var sb = new StringBuilder(h.Length * 2);
        foreach (byte b in h) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    public sealed class StreamRecord
    {
        public string path { get; set; } = "";
        public long size { get; set; }
        public string sha256 { get; set; } = "";
        public StreamRecord() { }
        public StreamRecord(string p, long s, string sha) { path = p; size = s; sha256 = sha; }
    }

    public sealed class ManifestDoc
    {
        public string source { get; set; } = "";
        public int version { get; set; }
        public List<StreamRecord> streams { get; set; } = new();
    }
}