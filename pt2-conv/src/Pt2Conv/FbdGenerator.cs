using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Pt2Conv;

/// <summary>
/// Generates a TriStation CFBDDocXml (.FBD container stream) from a PLCopen XML POU export.
/// Rules reverse-engineered from reference containers FB05_DCS_CMD and FB04_DI.
/// Accepted deviations: wire ids are contiguous (reference gaps are edit-history artifacts),
/// displayables emitted in id order (reference file order is editor draw order),
/// dateTime is generation time.
/// </summary>
public static partial class FbdGenerator
{
    /// <summary>Authoritative per-tag info (from the template tag database) used for program global declarations.</summary>
    public readonly struct TagInfo
    {
        public readonly string DataType, Desc;
        public TagInfo(string dataType, string desc) { DataType = dataType; Desc = desc; }
    }

    // ============================================================ library metadata (OBSERVED)

    public sealed class LibPin
    {
        public string Name = "", DataType = "", InitVal = "", Desc = "";
        public int Vt, Flags, RefCount, FbRefCount;
    }

    private sealed class LibMeta
    {
        public int DogTag, Type, ExtOptFlags, CemEI, CemAO, EventId, User = 2;
        public long SubType;
        public string Date = "";
        public List<LibPin> Pins = new();
    }

    /// <summary>Snapshot data for a POU defined by the project itself (a function block that
    /// another POU instantiates). TriStation resolves such a block from the catalogue entry and
    /// the FB's own interface, not from the platform library.
    ///
    /// The identity has to be the *live* catalogue identity of the element: its DogTag plus the
    /// eventId of its EventConnectionsChanged record. A reference written against eventId 0 (or
    /// a DogTag of 0) is not resolvable, and the editor draws the instance as an unknown block
    /// with a red cross over it.</summary>
    public sealed class ProjectTypeInfo
    {
        public int DogTag, ElemType = 2, SubType = 0, ExtOptFlags = 2, CemEI, CemAO, EventId, User = 1;
        public string Date = "";
        public List<LibPin> Pins = new();
    }

    /// <summary>Reads the interface of a project POU (inputs → vt0, outputs → vt1, inOut/local →
    /// vt3) so it can be written as the snapshot of a block instance in another POU.</summary>
    public static ProjectTypeInfo BuildProjectType(string xmlPath, string pouName)
    {
        var p = Parse(xmlPath, pouName, null, null);
        var info = new ProjectTypeInfo();
        foreach (var v in p.Vars)
        {
            int vt = v.IsInstance ? 3 : v.Section switch
            {
                "output" => 1,
                "inOut" => 3,
                "local" => 3,
                "input" => 0,
                _ => 5,
            };
            int refCount = v.IsInstance
                ? p.Blocks.Count(b => b.InstanceName == v.Name)
                : p.Placements.Count(x => x.Expression == v.Name);
            int flags = v.IsInstance ? (Lib.ContainsKey(v.Derived) ? 100663298 : 33554434) : 0;
            info.Pins.Add(new LibPin
            {
                Name = v.Name,
                DataType = v.IsInstance ? v.Derived : v.DataType,
                InitVal = v.InitVal,
                Desc = v.Doc,
                Vt = vt,
                Flags = flags,
                RefCount = refCount,
                FbRefCount = v.IsInstance ? refCount : 0,
            });
        }
        return info;
    }

    /// <summary>
    /// Loads the platform library identities observed in a TriStation 5.8 project
    /// (dogTag/type/subType/flags/event/pin list per element). The built-in table above is
    /// authoritative where it overlaps; this resource fills the remaining library types so
    /// snapshots can be written without a template project that already carries them.
    /// </summary>
    private static bool _snapshotsLoaded;
    public static void EnsureLibrarySnapshots()
    {
        if (_snapshotsLoaded) return;
        _snapshotsLoaded = true;
        try
        {
            using var stream = typeof(FbdGenerator).Assembly
                .GetManifestResourceStream("Pt2Conv.Resources.library_snapshots.json");
            if (stream == null) return;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var doc = System.Text.Json.JsonDocument.Parse(reader.ReadToEnd());
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var v = entry.Value;
                var meta = new LibMeta
                {
                    DogTag = v.GetProperty("dogTag").GetInt32(),
                    Type = v.GetProperty("type").GetInt32(),
                    SubType = v.GetProperty("subType").GetInt64(),
                    ExtOptFlags = v.GetProperty("extOptFlags").GetInt32(),
                    CemEI = v.GetProperty("cemEI").GetInt32(),
                    CemAO = v.GetProperty("cemAO").GetInt32(),
                    EventId = v.GetProperty("eventId").GetInt32(),
                    Date = v.GetProperty("date").GetString() ?? "",
                    User = v.GetProperty("user").GetInt32(),
                };
                foreach (var pin in v.GetProperty("pins").EnumerateArray())
                    meta.Pins.Add(new LibPin
                    {
                        Name = pin.GetProperty("name").GetString() ?? "",
                        DataType = pin.GetProperty("dataType").GetString() ?? "",
                        InitVal = pin.GetProperty("initVal").GetString() ?? "",
                        Vt = pin.GetProperty("vt").GetInt32(),
                        Flags = pin.GetProperty("flags").GetInt32(),
                    });
                // The resource is a capture of the *installed* library, so it supersedes the
                // built-in table (which mirrors the older library the embedded base was made
                // with); stale dogTags here are reported by TriStation as out-of-date elements.
                Lib[entry.Name] = meta;
            }
        }
        catch { /* resource is optional; the built-in table still works */ }
    }

    // Element names of every library function/function block/data type in the project
    // library. A program variable whose name matches one of these (case-insensitively)
    // is resolved by TriStation as a library reference, not as an unresolved tag, so it
    // is NOT written to the FBD declarations (e.g. "max"/"min" vs. the MAX/MIN functions).
    public static readonly HashSet<string> LibraryNames = new(StringComparer.OrdinalIgnoreCase);

    public static void LoadLibraryNames(string projectXml)
    {
        EnsureLibrarySnapshots();
        var doc = System.Xml.Linq.XDocument.Parse(projectXml);
        foreach (var e in doc.Descendants())
        {
            var n = e.Attribute("ElemName");
            if (n != null && !string.IsNullOrEmpty(n.Value)) LibraryNames.Add(n.Value);
        }
        foreach (var k in Lib.Keys) LibraryNames.Add(k);
    }

    private static readonly Dictionary<string, LibMeta> Lib = new()
    {
        ["R_TRIG"] = new()
        {
            DogTag = 184, Type = 2, SubType = 6, ExtOptFlags = 159, CemEI = 1, CemAO = 1,
            EventId = 297, Date = "1995-07-12 17:31:07",
            Pins =
            {
                new LibPin { Name = "CLK", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "Q", DataType = "BOOL", Vt = 1 },
                new LibPin { Name = "M", DataType = "BOOL", Vt = 3 },
            },
        },
        ["OR"] = new()
        {
            DogTag = 434, Type = 8, SubType = 2147483652L, ExtOptFlags = 7, CemEI = 1, CemAO = 0,
            EventId = 292, Date = "1995-07-12 17:30:51",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_BIT", Vt = 0, Flags = 1 },
                new LibPin { Name = "OR", DataType = "ANY_BIT", Vt = 1 },
            },
        },
        ["AND"] = new()
        {
            DogTag = 358, Type = 8, SubType = 2147483652L, ExtOptFlags = 7, CemEI = 2, CemAO = 0,
            EventId = 20, Date = "1995-07-12 17:24:00",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_BIT", Vt = 0, Flags = 1 },
                new LibPin { Name = "AND", DataType = "ANY_BIT", Vt = 1 },
            },
        },
        ["MOVE"] = new()
        {
            DogTag = 428, Type = 8, SubType = 1, ExtOptFlags = 31, CemEI = 1, CemAO = 1,
            EventId = 276, Date = "1995-07-12 17:30:27",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY", Vt = 1 },
            },
        },
        ["ADD"] = new()
        {
            DogTag = 357, Type = 8, SubType = 2147483652L, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 16, Date = "1995-07-12 17:23:55",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NUM", Vt = 0, Flags = 1 },
                new LibPin { Name = "+", DataType = "ANY_NUM", Vt = 1 },
            },
        },
        ["SUB"] = new()
        {
            DogTag = 461, Type = 8, SubType = 2, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 403, Date = "1995-07-12 17:34:26",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NUM", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NUM", Vt = 0 },
                new LibPin { Name = "-", DataType = "ANY_NUM", Vt = 1 },
            },
        },
        ["MUL"] = new()
        {
            DogTag = 429, Type = 8, SubType = 2147483652L, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 280, Date = "1995-07-12 17:30:33",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NUM", Vt = 0, Flags = 1 },
                new LibPin { Name = "*", DataType = "ANY_NUM", Vt = 1 },
            },
        },
        ["DIV"] = new()
        {
            DogTag = 384, Type = 8, SubType = 2, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 120, Date = "1995-07-12 17:26:35",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NUM", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NUM", Vt = 0 },
                new LibPin { Name = "/", DataType = "ANY_NUM", Vt = 1 },
            },
        },
        ["NOT"] = new()
        {
            DogTag = 432, Type = 8, SubType = 1, ExtOptFlags = 31, CemEI = 1, CemAO = 1,
            EventId = 288, Date = "1995-07-12 17:30:45",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_BIT", Vt = 0 },
                new LibPin { Name = "NOT", DataType = "ANY_BIT", Vt = 1 },
            },
        },
        ["R_TRIG"] = new()
        {
            DogTag = 435, Type = 2, SubType = 6, ExtOptFlags = 159, CemEI = 1, CemAO = 1,
            EventId = 297, Date = "1995-07-12 17:31:07",
            Pins =
            {
                new LibPin { Name = "CLK", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "Q", DataType = "BOOL", Vt = 1 },
                new LibPin { Name = "M", DataType = "BOOL", Vt = 3 },
            },
        },
        ["F_TRIG"] = new()
        {
            DogTag = 392, Type = 2, SubType = 6, ExtOptFlags = 159, CemEI = 1, CemAO = 1,
            EventId = 157, Date = "1995-07-12 17:27:34",
            Pins =
            {
                new LibPin { Name = "CLK", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "Q", DataType = "BOOL", Vt = 1 },
                new LibPin { Name = "M", DataType = "BOOL", InitVal = "1", Vt = 3 },
            },
        },
        ["RS"] = new()
        {
            DogTag = 445, Type = 2, SubType = 6, ExtOptFlags = 143, CemEI = 1, CemAO = 1,
            EventId = 349, Date = "1995-07-12 17:32:38",
            Pins =
            {
                new LibPin { Name = "S", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "R1", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "Q1", DataType = "BOOL", Vt = 1 },
            },
        },
        ["SR"] = new()
        {
            DogTag = 460, Type = 2, SubType = 6, ExtOptFlags = 143, CemEI = 1, CemAO = 1,
            EventId = 400, Date = "1995-07-12 17:34:20",
            Pins =
            {
                new LibPin { Name = "S1", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "R", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "Q1", DataType = "BOOL", Vt = 1 },
            },
        },
        ["TON"] = new()
        {
            DogTag = 216, Type = 2, SubType = 6, ExtOptFlags = 159, CemEI = 1, CemAO = 1,
            EventId = 603, Date = "1995-07-12 19:31:52",
            Pins =
            {
                new LibPin { Name = "IN", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "PT", DataType = "TIME", Vt = 0 },
                new LibPin { Name = "Q", DataType = "BOOL", Vt = 1 },
                new LibPin { Name = "ET", DataType = "TIME", Vt = 1 },
                new LibPin { Name = "M", DataType = "BOOL", Vt = 3 },
                new LibPin { Name = "T0", DataType = "DT", Vt = 3 },
            },
        },
        ["EQ"] = new()
        {
            DogTag = 389, Type = 8, SubType = 2, ExtOptFlags = 7, CemEI = 2, CemAO = 0,
            EventId = 678, Date = "1995-07-20 21:21:32",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "=", DataType = "BOOL", Vt = 1 },
            },
        },
        ["NE"] = new()
        {
            DogTag = 0, Type = 8, SubType = 2, ExtOptFlags = 7, CemEI = 2, CemAO = 0,
            EventId = 0, Date = "",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "<>", DataType = "BOOL", Vt = 1 },
            },
        },
        ["GT"] = new()
        {
            DogTag = 397, Type = 8, SubType = 2, ExtOptFlags = 7, CemEI = 2, CemAO = 0,
            EventId = 682, Date = "1995-07-20 21:21:33",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = ">", DataType = "BOOL", Vt = 1 },
            },
        },
        ["GE"] = new()
        {
            DogTag = 395, Type = 8, SubType = 2, ExtOptFlags = 7, CemEI = 2, CemAO = 0,
            EventId = 680, Date = "1995-07-20 21:21:33",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = ">=", DataType = "BOOL", Vt = 1 },
            },
        },
        ["LT"] = new()
        {
            DogTag = 421, Type = 8, SubType = 2, ExtOptFlags = 7, CemEI = 2, CemAO = 0,
            EventId = 686, Date = "1995-07-20 21:21:34",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "<", DataType = "BOOL", Vt = 1 },
            },
        },
        ["LE"] = new()
        {
            DogTag = 408, Type = 8, SubType = 2, ExtOptFlags = 7, CemEI = 2, CemAO = 0,
            EventId = 684, Date = "1995-07-20 21:21:34",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY_NOTE1", Vt = 0 },
                new LibPin { Name = "<=", DataType = "BOOL", Vt = 1 },
            },
        },
        ["SEL"] = new()
        {
            DogTag = 447, Type = 8, SubType = 8, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 625, Date = "1995-07-12 19:47:27",
            Pins =
            {
                new LibPin { Name = "G", DataType = "BOOL", Vt = 0 },
                new LibPin { Name = "IN0", DataType = "ANY", Vt = 0 },
                new LibPin { Name = "IN1", DataType = "ANY", Vt = 0 },
                new LibPin { Name = "", DataType = "ANY", Vt = 1 },
            },
        },
        ["SQRT"] = new()
        {
            DogTag = 459, Type = 8, SubType = 1, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 395, Date = "1995-07-12 17:34:03",
            Pins =
            {
                new LibPin { Name = "", DataType = "ANY_REAL", Vt = 0 },
                new LibPin { Name = "BIF", DataType = "ANY_REAL", Vt = 1 },
            },
        },
        ["REAL_TO_DINT"] = new()
        {
            DogTag = 81, Type = 8, SubType = 1, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 300, Date = "1995-07-12 17:31:12",
            Pins =
            {
                new LibPin { Name = "", DataType = "REAL", Vt = 0 },
                new LibPin { Name = "()", DataType = "DINT", Vt = 1 },
            },
        },
        ["DINT_TO_DWORD"] = new()
        {
            DogTag = 125, Type = 8, SubType = 1, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 1276, Date = "1996-07-08 19:32:30",
            Pins =
            {
                new LibPin { Name = "BIF", DataType = "DWORD", Vt = 1 },
                new LibPin { Name = "", DataType = "DINT", Vt = 0 },
            },
        },
        ["DWORD_TO_DINT"] = new()
        {
            DogTag = 137, Type = 8, SubType = 1, ExtOptFlags = 3, CemEI = 0, CemAO = 0,
            EventId = 1281, Date = "1996-07-08 19:32:32",
            Pins =
            {
                new LibPin { Name = "BIF", DataType = "DINT", Vt = 1 },
                new LibPin { Name = "", DataType = "DWORD", Vt = 0 },
            },
        },
    };

    /// <summary>
    /// Learns library block metadata (dogTag/type/subType/flags/eventId/pins) from a reference
    /// FBD stream's &lt;snapshots&gt; collection. Called with the template's own POUs so
    /// regenerated snapshots reproduce the project's library identities exactly.
    /// </summary>
    public static void LoadLibraryFromFbd(string fbdXml)
    {
        int si = fbdXml.IndexOf("<Collection Name=\"snapshots\"", StringComparison.Ordinal);
        if (si < 0) return;
        string block = fbdXml.Substring(si);

        var head = System.Text.RegularExpressions.Regex.Matches(block,
            "<Class name=\"([^\"]*)\" dogTag=\"(\\d+)\" type=\"(\\d+)\" subType=\"(-?\\d+)\" " +
            "instanceCount=\"\\d+\" commentCount=\"\\d+\" extOptFlags=\"(-?\\d+)\" " +
            "cemEventInputs=\"(\\d+)\" cemActionOutputs=\"(\\d+)\">");
        for (int i = 0; i < head.Count; i++)
        {
            string name = System.Net.WebUtility.HtmlDecode(head[i].Groups[1].Value);
            if (name.Length == 0) continue;
            var meta = new LibMeta
            {
                DogTag = int.Parse(head[i].Groups[2].Value),
                Type = int.Parse(head[i].Groups[3].Value),
                SubType = long.Parse(head[i].Groups[4].Value),
                ExtOptFlags = int.Parse(head[i].Groups[5].Value),
                CemEI = int.Parse(head[i].Groups[6].Value),
                CemAO = int.Parse(head[i].Groups[7].Value),
            };
            int start = head[i].Index;
            int end = (i + 1 < head.Count) ? head[i + 1].Index : block.Length;
            string chunk = block.Substring(start, end - start);
            var ev = System.Text.RegularExpressions.Regex.Match(chunk,
                "<Class Embed=\"eventConnectionsChanged\" eventType=\"\\d+\" eventId=\"(\\d+)\" date=\"([^\"]*)\" user=\"(\\d+)\"");
            if (ev.Success)
            {
                meta.EventId = int.Parse(ev.Groups[1].Value);
                meta.Date = ev.Groups[2].Value;
                meta.User = int.Parse(ev.Groups[3].Value);
            }

            int di = chunk.IndexOf("<Collection Name=\"declarations\"", StringComparison.Ordinal);
            if (di >= 0)
            {
                int de = chunk.IndexOf("</Collection>", di, StringComparison.Ordinal);
                if (de < 0) de = chunk.Length;
                string pins = chunk.Substring(di, de - di);
                foreach (System.Text.RegularExpressions.Match pm in System.Text.RegularExpressions.Regex.Matches(pins,
                    "<Class name=\"([^\"]*)\" dataType=\"([^\"]*)\" initVal=\"([^\"]*)\" desc=\"[^\"]*\" " +
                    "varVTType=\"(\\d+)\" refCount=\"\\d+\" fbRefCount=\"\\d+\" flags=\"(\\d+)\" writes=\"0\""))
                {
                    meta.Pins.Add(new LibPin
                    {
                        Name = System.Net.WebUtility.HtmlDecode(pm.Groups[1].Value),
                        DataType = System.Net.WebUtility.HtmlDecode(pm.Groups[2].Value),
                        InitVal = System.Net.WebUtility.HtmlDecode(pm.Groups[3].Value),
                        Vt = int.Parse(pm.Groups[4].Value),
                        Flags = int.Parse(pm.Groups[5].Value),
                    });
                }
            }
            // The built-in table holds the standard TriStation library identities and is
            // authoritative; template-project streams only fill in types we do not know
            // (a stale base project must not override the platform library).
            if (!Lib.ContainsKey(name)) Lib[name] = meta;
        }
    }

    private static int LineOffset(string type, string pin, int inputIndex, int inputCount)
    {
        if (type == "R_TRIG" && pin == "CLK") return 4;
        if (type == "MOVE" && pin == "IN1") return 4;
        if (pin == "IN1") return 3;
        if (pin == "IN2") return 7;
        return inputCount <= 1 ? 4 : (inputIndex == 0 ? 3 : 7);
    }

    // ============================================================ export model

    private abstract class ExElem
    {
        public int DocIndex;
        public int ExportId = -1;
        public int CopyId;
        public int Sheet = 1;
        public int X, Y;
        public bool Clipped;
    }

    // Elements whose bottom edge lies below the drawing frame are dropped by the
    // TriStation editor. The export coordinates start at the top-left corner of the
    // drawing area, so the usable height of template D (3300 x 2100 with a 30 unit
    // margin all round) is 2100 - 2 * 30 = 2040.
    private const int SheetHeightExport = 2040;
    private static bool IsClipped(int y, int h) => y + h > SheetHeightExport;

    private sealed class ExVar : ExElem
    {
        public string Name = "", Doc = "", Section = "", DataType = "", Derived = "", InitVal = "";
        public bool IsInstance => Derived.Length > 0;
    }

    private sealed class ExComment : ExElem
    {
        public int W, H;
        public string Text = "", Align = "Center", Border = "Single", Size = "10";
        public string Expand = "FALSE", Macro = "FALSE", Behind = "FALSE";
    }

    private sealed class ExDivider : ExElem
    {
        public string Type = "HORIZONTAL";
    }

    private sealed class ExConn
    {
        public int SrcExportId;
        public string SrcPin = "";
        public ExElem? Owner;
        public string OwnerPin = "";
        public bool FeedBack;
        public List<(int X, int Y)> Path = new();
    }

    private sealed class ExVarPlace : ExElem
    {
        public string Kind = "in", Expression = "";
        public int W, H;
        public ExConn? InConn;
        public bool IsConstant;
        public ExElem? DirectInSource;
        public string DirectInPin = "";
        public string DataType = "";
        public string Annotation = "%DESCRIPTION", Annotate = "TRUE", MonitorValue = "FALSE";
    }

    private sealed class ExPinOut
    {
        public string Name = "";
        public int RelY;
        public bool Negated;
    }

    private sealed class ExPinIn
    {
        public string Name = "";
        public ExConn? Conn;
        public int RelY;
        public bool Negated;
    }

    private sealed class ExBlock : ExElem
    {
        public string TypeName = "", InstanceName = "";
        public int W, H;
        public bool DoubleSpace;
        public List<ExPinIn> Inputs = new();
        public List<ExPinOut> Outputs = new();
        public bool IsFunction => InstanceName.Length == 0;
    }

    private sealed class ExPou
    {
        public string Name = "", PouType = "functionblock", Version = "1.0";
        public bool IsBuilt = true;
        public List<ExVar> Vars = new();
        public List<ExComment> Comments = new();
        public List<ExVarPlace> Placements = new();
        public List<ExBlock> Blocks = new();
        public List<ExDivider> Dividers = new();
        public List<ExElem> ElemOrder = new();
        public List<ExConn> ConnOrder = new();
        public Dictionary<int, ExElem> ByExportId = new();
        public List<(int Number, string Title)> Sheets = new();
        public string DrawingNumber = "", DrawingTitle = "", Rev = "";
        public string SheetTemplate = "";
        public int SheetWidth, SheetHeight;
        public IReadOnlyDictionary<string, TagInfo>? TagDesc;
    }

    // ============================================================ generated wire model

    private sealed class Wire
    {
        public int Id;
        public ExElem Source = null!;
        public ExVarPlace? SourceVar;
        public ExBlock? SourceBlock;
        public string SourcePin = "";
        public ExElem Target = null!;
        public ExVarPlace? TargetVar;
        public ExBlock? TargetBlock;
        public string TargetPin = "";
        public bool TargetIsBlockInput;
        public bool IsBranch;
        public bool Suppress;
        // A zero-length connection from a real (non-literal) variable is silently dropped by
        // the importer: the pin stays bare and the variable gets no outConnection.
        public bool Drop;
        public bool FeedBack;
        public Wire? Parent;
        public int Vn;
        public List<(int X, int Y)> PathExport = new();

        // Set by the net decomposer for a wire merged through a junction: the two endpoints
        // may be arbitrary element pins (not the connection's original source/target).
        public bool Decomposed;
        public ExElem? EndA;
        public string EndAPin = "";
        public ExElem? EndB;
        public string EndBPin = "";
        // The connection's path as exported (target -> source). Net decomposition runs on
        // this raw geometry after the ST layout has been computed from the logical wires.
        public List<(int X, int Y)> RawPath = new();
    }

    private sealed class BlockPlan
    {
        public ExBlock B = null!;
        public int Id, Sequence;
        public string Temp = "";
        public int FirstLine, LineCount;
        public Dictionary<string, Wire> OutWires = new();
        public List<Wire> SinkAssigns = new();
        public List<Wire?> InWires = new();
    }

    private sealed class PlacePlan
    {
        public ExVarPlace P = null!;
        public int Id, LineNumber;
        public Wire? OutWire, InWire;
    }

    private static string A(XElement e, string name, string def = "")
        => (string?)e.Attribute(name) ?? def;

    /// <summary>Attribute lookup by local name (export uses the ts: namespace for some attrs).</summary>
    private static string AL(XElement e, string local, string def = "")
    {
        foreach (var at in e.Attributes())
            if (at.Name.LocalName == local) return at.Value;
        return def;
    }

    private static XElement? Pos(XElement el)
        => el.Elements().FirstOrDefault(e => e.Name.LocalName == "position");

    private static string TextTrim(XElement? e)
    {
        if (e == null) return "";
        // XCData derives from XText, so this also picks up <![CDATA[…]]> content.
        var t = e.Nodes().OfType<XText>().Select(t => t.Value).FirstOrDefault() ?? "";
        return t.Trim();
    }

    private static ExPou Parse(string xmlPath, string pouName, IReadOnlyDictionary<string, TagInfo>? tagDesc = null, IReadOnlySet<string>? referencedTypes = null)
    {
        var doc = XmlRepair.Load(xmlPath);
        var pou = doc.Descendants().FirstOrDefault(
            e => e.Name.LocalName == "pou" && A(e, "name") == pouName)
            ?? doc.Descendants().FirstOrDefault(
                e => e.Name.LocalName == "pou" && string.Equals(A(e, "name"), pouName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"POU '{pouName}' not found in {xmlPath}");

        var p = new ExPou { Name = pouName, PouType = A(pou, "pouType", "functionBlock").ToLowerInvariant(), TagDesc = tagDesc };
        p.IsBuilt = referencedTypes == null ||
                    referencedTypes.Contains(pouName) ||
                    referencedTypes.Any(r => string.Equals(r, pouName, StringComparison.OrdinalIgnoreCase));
        // TriStation's import resets the POU version: function blocks are written at the current
        // FBD schema version (1.1), programs at 1.0, regardless of the XML's declared revision.
        p.Version = p.PouType == "functionblock" ? "1.1" : "1.0";

        var iface = pou.Elements().FirstOrDefault(e => e.Name.LocalName == "interface");
        if (iface != null)
        {
            foreach (var (secName, secKind) in new[] { ("inputVars", "input"), ("inOutVars", "inOut"),
                                                       ("outputVars", "output"), ("localVars", "local"),
                                                       ("globalVars", "global") })
            {
                var secEls = iface.Elements().Where(e => e.Name.LocalName == secName).ToList();
                foreach (var v in secEls.SelectMany(s => s.Elements()).Where(e => e.Name.LocalName == "variable"))
                {
                    var typeEl = v.Elements().FirstOrDefault(e => e.Name.LocalName == "type");
                    var derived = typeEl?.Elements().FirstOrDefault(e => e.Name.LocalName == "derived");
                    string dtype;
                    if (derived != null) dtype = A(derived, "name");
                    else
                    {
                        var t = typeEl?.Elements().FirstOrDefault(e => e.Name.LocalName != "array");
                        dtype = t?.Name.LocalName ?? "";
                    }
                    var initEl = v.Elements().FirstOrDefault(e => e.Name.LocalName == "initialValue")
                        ?.Elements().FirstOrDefault(e => e.Name.LocalName == "simpleValue");
                    p.Vars.Add(new ExVar
                    {
                        Name = A(v, "name"),
                        Section = secKind,
                        DataType = dtype,
                        Derived = derived != null ? A(derived, "name") : "",
                        Doc = TextTrim(v.Elements().FirstOrDefault(e => e.Name.LocalName == "documentation")),
                        InitVal = initEl != null ? A(initEl, "value") : "",
                    });
                }
            }
            // The export repeats every referenced tag inside each program's <globalVars>;
            // the FBD's declarations collection lists each name once, and omits literals.
            // Names that are not identifiers (empty, "dir/…", "L%4102"), or that shadow a
            // library element (max/min), are not variables in TriStation's declarations.
            var seen = new HashSet<string>(StringComparer.Ordinal);
            p.Vars = p.Vars.Where(x => seen.Add(x.Name) && IsDeclarableName(x.Name)).ToList();
        }

        var fbd = pou.Descendants().FirstOrDefault(e => e.Name.LocalName == "FBD")
            ?? throw new InvalidOperationException($"POU '{pouName}' has no FBD body");

        var sheetsEl = fbd.Elements().FirstOrDefault(e => e.Name.LocalName == "SHEETS");
        if (sheetsEl != null)
        {
            p.SheetTemplate = A(sheetsEl, "Template");
            p.SheetWidth = int.Parse(A(sheetsEl, "Width", "0"));
            p.SheetHeight = int.Parse(A(sheetsEl, "Height", "0"));
            foreach (var sh in sheetsEl.Elements().Where(e => e.Name.LocalName == "SHEET"))
            {
                string title = "";
                foreach (var mac in sh.Elements().Where(e => e.Name.LocalName == "Macros"))
                    if (A(mac, "Name") == "SHEET_TITLE")
                        title = mac.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value ?? "";
                int sheetNo = int.Parse(A(sh, "Number", "1"));
                p.Sheets.Add((sheetNo, title));
                int divDoc = 0;
                foreach (var d in sh.Elements().Where(e => e.Name.LocalName == "NETWORKDIVIDER"))
                {
                    var dv = new ExDivider
                    {
                        DocIndex = divDoc++,
                        Sheet = sheetNo,
                        X = int.Parse(A(d, "X", "0")),
                        Y = int.Parse(A(d, "Y", "0")),
                        Type = A(d, "Type", "HORIZONTAL"),
                        ExportId = int.Parse(A(d, "localId", A(d, "LocalId", "-1"))),
                    };
                    dv.Clipped = IsClipped(dv.Y, 0);
                    p.Dividers.Add(dv);
                }
            }
        }
        if (p.Sheets.Count == 0) p.Sheets.Add((1, ""));

        var ext = pou.Elements().FirstOrDefault(e => e.Name.LocalName == "POU_EXT");
        if (ext != null)
        {
            foreach (var mac in ext.Elements().Where(e => e.Name.LocalName == "Macros"))
            {
                string n = A(mac, "Name");
                if (n != "DRAWING_NUMBER" && n != "DRAWING_TITLE" && n != "REVISION") continue;
                string val = mac.Elements().FirstOrDefault(e => e.Name.LocalName == "Value")?.Value ?? "";
                if (n == "DRAWING_NUMBER") p.DrawingNumber = val;
                else if (n == "DRAWING_TITLE") p.DrawingTitle = val;
                else p.Rev = val;
            }
        }

        int docIndex = 0;
        foreach (var el in fbd.Elements())
        {
            string ln = el.Name.LocalName;
            if (ln == "comment")
            {
                var c = new ExComment
                {
                    DocIndex = docIndex++,
                    X = int.Parse(A(Pos(el)!, "x", "0")),
                    Y = int.Parse(A(Pos(el)!, "y", "0")),
                    W = int.Parse(A(el, "width", "0")),
                    H = int.Parse(A(el, "height", "0")),
                    Sheet = int.Parse(AL(Pos(el)!, "SheetNumber", "1")),
                };
                var cx = el.Elements().FirstOrDefault(e => e.Name.LocalName == "COMMENT_EXT");
                if (cx != null)
                {
                    c.Align = TextTrim(cx.Elements().FirstOrDefault(e => e.Name.LocalName == "TEXTALIGNMENT"));
                    c.Border = TextTrim(cx.Elements().FirstOrDefault(e => e.Name.LocalName == "BORDER"));
                    c.Size = TextTrim(cx.Elements().FirstOrDefault(e => e.Name.LocalName == "TEXTSIZE"));
                    c.Expand = TextTrim(cx.Elements().FirstOrDefault(e => e.Name.LocalName == "EXPANDMACROS"));
                    c.Macro = TextTrim(cx.Elements().FirstOrDefault(e => e.Name.LocalName == "MACROEDITING"));
                    c.Behind = TextTrim(cx.Elements().FirstOrDefault(e => e.Name.LocalName == "BEHINDLOGIC"));
                }
                c.Text = el.Elements().FirstOrDefault(e => e.Name.LocalName == "content")?.Value ?? "";
                // Frame comments legitimately extend past the drawing frame (the reference
                // keeps them), so only logic items are clipped.
                c.Clipped = false;
                p.Comments.Add(c);
                p.ElemOrder.Add(c);
                c.ExportId = int.Parse(A(el, "localId", "-1"));
                p.ByExportId[c.ExportId] = c;
            }
            else if (ln is "inVariable" or "outVariable" or "inOutVariable")
            {
                string kind = ln == "inVariable" ? "in" : ln == "outVariable" ? "out" : "inOut";
                var v = new ExVarPlace
                {
                    DocIndex = docIndex++,
                    Kind = kind,
                    X = int.Parse(A(Pos(el)!, "x", "0")),
                    Y = int.Parse(A(Pos(el)!, "y", "0")),
                    W = int.Parse(A(el, "width", "0")),
                    H = int.Parse(A(el, "height", "0")),
                    Sheet = int.Parse(AL(Pos(el)!, "SheetNumber", "1")),
                    Expression = TextTrim(el.Elements().FirstOrDefault(e => e.Name.LocalName == "expression")),
                };
                var cpi = el.Elements().FirstOrDefault(e => e.Name.LocalName == "connectionPointIn");
                v.InConn = ParseConn(cpi?.Elements().FirstOrDefault(e => e.Name.LocalName == "connection"));
                var itemExt = el.Elements().FirstOrDefault(e => e.Name.LocalName == "FBDITEM_EXT");
                var dtEl = itemExt?.Elements().FirstOrDefault(e => e.Name.LocalName == "DATATYPE");
                if (dtEl != null) v.DataType = TextTrim(dtEl);
                // Fall back to the declared type: the export does not always carry DATATYPE on the
                // placement, and the compiler temp type is derived from the operand's type.
                if (v.DataType.Length == 0)
                {
                    var decl = p.Vars.FirstOrDefault(x => x.Name.Equals(v.Expression, StringComparison.OrdinalIgnoreCase));
                    if (decl != null) v.DataType = decl.IsInstance ? decl.Derived : decl.DataType;
                }
                var annText = itemExt?.Elements().FirstOrDefault(e => e.Name.LocalName == "TEXT");
                if (annText != null) v.Annotation = TextTrim(annText);
                var annFlag = itemExt?.Elements().FirstOrDefault(e => e.Name.LocalName == "ANNOTATE");
                if (annFlag != null) v.Annotate = TextTrim(annFlag);
                var annMon = itemExt?.Elements().FirstOrDefault(e => e.Name.LocalName == "ANNOTATEMONITORVALUE");
                if (annMon != null) v.MonitorValue = TextTrim(annMon);
                v.IsConstant = IsConstantName(v.Expression);
                // TriStation drops drawn placeholders that are not valid identifiers (empty,
                // "10", "dir/…", "L%4102", "max"/"min"); a literal is only valid on an input
                // (inVariable) where it is a constant. Which placements survive is visible in
                // the imported FBD: invalid names appear nowhere, not even as declarations.
                bool keepPlacement = v.Kind == "in"
                    ? v.IsConstant || IsDeclarableName(v.Expression)
                    : IsDeclarableName(v.Expression);
                if (!keepPlacement) continue;
                if (v.InConn != null) { v.InConn.Owner = v; p.ConnOrder.Add(v.InConn); }
                p.Placements.Add(v);
                p.ElemOrder.Add(v);
                v.ExportId = int.Parse(A(el, "localId", "-1"));
                p.ByExportId[v.ExportId] = v;
            }
            else if (ln == "block")
            {
                var b = new ExBlock
                {
                    DocIndex = docIndex++,
                    TypeName = A(el, "typeName"),
                    InstanceName = A(el, "instanceName"),
                    X = int.Parse(A(Pos(el)!, "x", "0")),
                    Y = int.Parse(A(Pos(el)!, "y", "0")),
                    W = int.Parse(A(el, "width", "0")),
                    H = int.Parse(A(el, "height", "0")),
                    Sheet = int.Parse(AL(Pos(el)!, "SheetNumber", "1")),
                };
                var iv = el.Elements().FirstOrDefault(e => e.Name.LocalName == "inputVariables");
                if (iv != null)
                    foreach (var pinEl in iv.Elements().Where(e => e.Name.LocalName == "variable"))
                    {
                        var pin = new ExPinIn { Name = A(pinEl, "formalParameter") };
                        pin.Negated = A(pinEl, "negated", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
                        var rel = pinEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "relPosition");
                        if (rel != null) int.TryParse(A(rel, "y", "0"), out pin.RelY);
                        var cpi = pinEl.Elements().FirstOrDefault(e => e.Name.LocalName == "connectionPointIn");
                        pin.Conn = ParseConn(cpi?.Elements().FirstOrDefault(e => e.Name.LocalName == "connection"));
                        if (pin.Conn != null) { pin.Conn.Owner = b; pin.Conn.OwnerPin = pin.Name; p.ConnOrder.Add(pin.Conn); }
                        b.Inputs.Add(pin);
                    }
                var ov = el.Elements().FirstOrDefault(e => e.Name.LocalName == "outputVariables");
                if (ov != null)
                    foreach (var pinEl in ov.Elements().Where(e => e.Name.LocalName == "variable"))
                    {
                        var po = new ExPinOut { Name = A(pinEl, "formalParameter") };
                        po.Negated = A(pinEl, "negated", "false").Equals("true", StringComparison.OrdinalIgnoreCase);
                        var relPos = pinEl.Descendants().FirstOrDefault(e => e.Name.LocalName == "relPosition");
                        if (relPos != null) int.TryParse(A(relPos, "y", "0"), out po.RelY);
                        b.Outputs.Add(po);
                    }
                b.Clipped = IsClipped(b.Y, b.H);
                var dsEl = el.Elements().FirstOrDefault(e => e.Name.LocalName == "DOUBLE_SPACE_TERMINAL");
                if (dsEl != null) b.DoubleSpace = TextTrim(dsEl).Equals("true", StringComparison.OrdinalIgnoreCase);
                p.Blocks.Add(b);
                p.ElemOrder.Add(b);
                b.ExportId = int.Parse(A(el, "localId", "-1"));
                p.ByExportId[b.ExportId] = b;
            }
        }

        // Function-block instances used in the body are declared in the FBD's
        // declarations collection even when they are not named in <globalVars>.
        var declared = new HashSet<string>(p.Vars.Select(v => v.Name), StringComparer.Ordinal);
        foreach (var b in p.Blocks)
            if (b.InstanceName.Length > 0 && declared.Add(b.InstanceName))
                p.Vars.Add(new ExVar { Name = b.InstanceName, Section = "", DataType = "", Derived = b.TypeName, Doc = "" });

        return p;
    }

    private static bool IsConstantName(string name)
    {
        if (name.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("FALSE", StringComparison.OrdinalIgnoreCase)) return true;
        return double.TryParse(name, NumberStyles.Any, CultureInfo.InvariantCulture, out _);
    }

    // A name is written to the FBD declarations only when it is a plain identifier, is not
    // a literal, and does not shadow a library element. Anything else (empty, "dir/…",
    // "L%4102", "max"/"min") is a drawn placeholder that TriStation never declares.
    public static bool IsDeclarableName(string name)
    {
        if (string.IsNullOrEmpty(name) || IsConstantName(name)) return false;
        if (name.IndexOfAny(new[] { '/', '%', ' ' }) >= 0) return false;
        if (!char.IsLetter(name[0]) && name[0] != '_') return false;
        for (int i = 1; i < name.Length; i++)
        {
            char c = name[i];
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        }
        return !LibraryNames.Contains(name);
    }

    private static bool IsBoolConstant(ExVarPlace v) =>
        v.DataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase) ||
        v.Expression.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ||
        v.Expression.Equals("FALSE", StringComparison.OrdinalIgnoreCase);

    private static ExConn? ParseConn(XElement? connEl)
    {
        if (connEl == null) return null;
        var c = new ExConn
        {
            SrcExportId = int.Parse(A(connEl, "refLocalId", "-1")),
            SrcPin = A(connEl, "formalParameter"),
            FeedBack = A(connEl, "FeedBack", "false").Equals("true", StringComparison.OrdinalIgnoreCase),
        };
        foreach (var pos in connEl.Elements().Where(e => e.Name.LocalName == "position"))
            c.Path.Add((int.Parse(A(pos, "x", "0")), int.Parse(A(pos, "y", "0"))));
        return c;
    }

    // ============================================================ wire building

    private sealed class Plan
    {
        public ExPou P = null!;
        public List<Wire> Wires = new();
        public Dictionary<ExElem, PlacePlan> Places = new();
        public Dictionary<ExDivider, int> DividerIds = new();
        public List<BlockPlan> Blocks = new();
        public Dictionary<string, ExVar> VarByName = new();
        public List<BlockPlan> BlockSeq = new();
        public int TempCount;
        public int LastId;
        public int PrimarySpan;
        public int WireIdStart;
        /// <summary>The ST listing TriStation stores beside an FBD element (<c>.IST</c>). Built while
        /// the layout is computed; it is also the source of the compiler temp variables that a
        /// caller's block snapshot has to declare.</summary>
        public List<string>? Listing;
        /// <summary>Project-defined function blocks (identity + interface) so instances of them
        /// can be wired to a real snapshot event instead of the unknown-type fallback.</summary>
        public IReadOnlyDictionary<string, ProjectTypeInfo>? ProjectTypes;
    }

    private static int TargetPriority(ExPou p, Wire w)
    {
        if (w.TargetIsBlockInput) return 2;
        var name = w.TargetPin;
        var v = p.Vars.FirstOrDefault(x => x.Name == name);
        return v?.Section switch
        {
            "output" => 0,
            "inOut" => 1,
            "local" => 1,
            _ => 3,
        };
    }

    private static int Cross((int X, int Y) o, (int X, int Y) a, (int X, int Y) b)
        => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

    private static bool OnSegment((int X, int Y) q, (int X, int Y) a, (int X, int Y) b)
    {
        if (Cross(q, a, b) != 0) return false;
        return Math.Min(a.X, b.X) <= q.X && q.X <= Math.Max(a.X, b.X)
            && Math.Min(a.Y, b.Y) <= q.Y && q.Y <= Math.Max(a.Y, b.Y);
    }

    // ============================================================ net decomposition
    //
    // TriStation stores one authoring net as arms split at junctions: through each junction
    // the two collinear arms are merged into one wire and every other arm is emitted as its
    // own wire. The export lists one connection per sink, so a merge net shows up as several
    // connections sharing geometry. This rebuilds the editor's decomposition:
    //   * arms that continue straight through a junction merge into one chain (the trunk);
    //   * an arm that stops at a junction becomes a branch whose output points at the trunk
    //     with effectiveParam "Vn" (n = index of the junction point in the trunk's path);
    //   * a chain may start/end on any element pin (including an input pin), not only on the
    //     element that the original connection named.
    // The decomposition runs on the raw exported geometry after ComputeLayout so the ST
    // layout keeps using the original logical wires.
    private sealed class NetChain
    {
        public List<(int X, int Y)> Pts = new();
        public Wire Wire = null!;
        public (int X, int Y) StartJunction, EndJunction;
        public bool StartIsJunction, EndIsJunction;
        public Wire? Parent;
        public int Vn;
    }

    private static void DecomposeNets(Plan plan)
    {
        var live = plan.Wires.Where(w => !w.Suppress && w.RawPath.Count >= 2).ToList();
        if (live.Count < 2) return;

        // A merge net is a fan-out from one source pin: the export repeats the shared route
        // for every sink. Grouping by source pin (not by arbitrary geometric touching) is what
        // keeps two unrelated wires that merely cross/touch from being merged.
        var groups = live.GroupBy(w => (Source: w.Source, Pin: w.SourcePin)).ToList();
        var chainsByFirst = new Dictionary<Wire, List<Wire>>();
        var replaced = new HashSet<Wire>();
        foreach (var g in groups)
        {
            var net = g.ToList();
            if (net.Count < 2) continue;
            var chains = TryDecomposeNet(plan, net);
            if (chains == null || chains.Count != net.Count) continue;
            var wires = chains.Select(c => c.Wire).ToList();
            chainsByFirst[net[0]] = wires;
            foreach (var w in net) replaced.Add(w);
        }
        if (chainsByFirst.Count == 0) return;

        var final = new List<Wire>();
        foreach (var w in plan.Wires)
        {
            if (chainsByFirst.TryGetValue(w, out var repl)) final.AddRange(repl);
            else if (!replaced.Contains(w)) final.Add(w);
        }
        plan.Wires = final;
    }

    /// <summary>
    /// Decomposes one geometric net into chains. Returns null when the result cannot be
    /// mapped back one-to-one to the net's connections (then the caller keeps the export's
    /// per-connection wires).
    /// </summary>
    private static List<NetChain>? TryDecomposeNet(Plan plan, List<Wire> net)
    {
        // ---- planar graph of the net's raw geometry ----
        var pts = new HashSet<(int X, int Y)>();
        foreach (var w in net) foreach (var p in w.RawPath) pts.Add(p);
        var all = pts.ToList();

        var adj = new Dictionary<(int X, int Y), HashSet<(int X, int Y)>>();
        void AddEdge((int X, int Y) a, (int X, int Y) b)
        {
            if (a == b) return;
            if (!adj.TryGetValue(a, out var la)) { la = new HashSet<(int X, int Y)>(); adj[a] = la; }
            if (!adj.TryGetValue(b, out var lb)) { lb = new HashSet<(int X, int Y)>(); adj[b] = lb; }
            la.Add(b); lb.Add(a);
        }
        foreach (var w in net)
            for (int k = 0; k + 1 < w.RawPath.Count; k++)
            {
                var a = w.RawPath[k]; var b = w.RawPath[k + 1];
                var between = all.Where(s => s != a && s != b && OnSegment(s, a, b))
                    .OrderBy(s => Math.Abs(s.X - a.X) + Math.Abs(s.Y - a.Y)).ToList();
                var chain = new List<(int X, int Y)> { a };
                chain.AddRange(between); chain.Add(b);
                for (int t = 0; t + 1 < chain.Count; t++) AddEdge(chain[t], chain[t + 1]);
            }

        int Deg((int X, int Y) p) => adj.TryGetValue(p, out var s) ? s.Count : 0;
        var junctions = adj.Keys.Where(p => Deg(p) >= 3).ToHashSet();
        if (junctions.Count == 0) return null;

        // ---- pins at the exported path ends (raw paths run target -> source) ----
        var pinAt = new Dictionary<(int X, int Y), List<(ExElem Elem, string Pin, bool IsSource)>>();
        void AddPin((int X, int Y) p, ExElem e, string pin, bool isSource)
        {
            if (!pinAt.TryGetValue(p, out var l)) { l = new List<(ExElem, string, bool)>(); pinAt[p] = l; }
            if (!l.Any(x => ReferenceEquals(x.Elem, e) && x.Pin == pin && x.IsSource == isSource))
                l.Add((e, pin, isSource));
        }
        foreach (var w in net)
        {
            AddPin(w.RawPath[0], w.Target, w.TargetPin, false);
            AddPin(w.RawPath[^1], w.Source, w.SourcePin, true);
        }

        // ---- arms between special nodes (junctions / path ends) ----
        var arms = new List<List<(int X, int Y)>>();
        var armEnds = new List<((int X, int Y) Pt, bool AtStart)>();
        var usedEdge = new HashSet<string>();
        void WalkFrom((int X, int Y) start, (int X, int Y) first)
        {
            var arm = new List<(int X, int Y)> { start, first };
            var prev = start; var cur = first;
            while (Deg(cur) == 2)
            {
                var nxt = adj[cur].First(x => x != prev);
                arm.Add(nxt); prev = cur; cur = nxt;
            }
            for (int t = 0; t + 1 < arm.Count; t++) usedEdge.Add(EdgeKey(arm[t], arm[t + 1]));
            arms.Add(arm);
        }
        foreach (var p in adj.Keys.Where(p => Deg(p) != 2))
            foreach (var nb in adj[p])
                if (!usedEdge.Contains(EdgeKey(p, nb))) WalkFrom(p, nb);
        if (arms.Count == 0) return null;

        // ---- merge collinear opposite arm ends at each junction ----
        var pair = new Dictionary<(int Arm, int End), (int Arm, int End)>();
        foreach (var j in junctions)
        {
            var ends = new List<(int Arm, int End, (int X, int Y) Dir)>();
            for (int a = 0; a < arms.Count; a++)
                for (int e = 0; e < 2; e++)
                {
                    var endPt = e == 0 ? arms[a][0] : arms[a][^1];
                    if (endPt != j) continue;
                    var nb = e == 0 ? arms[a][1] : arms[a][^2];
                    ends.Add((a, e, Dir(j, nb)));
                }
            var used = new bool[ends.Count];
            for (int i = 0; i < ends.Count; i++)
            {
                if (used[i]) continue;
                for (int k = i + 1; k < ends.Count; k++)
                {
                    if (used[k]) continue;
                    if (ends[i].Dir.X * ends[k].Dir.Y != ends[i].Dir.Y * ends[k].Dir.X) continue;
                    if (ends[i].Dir.X * ends[k].Dir.X + ends[i].Dir.Y * ends[k].Dir.Y >= 0) continue;
                    used[i] = used[k] = true;
                    pair[(ends[i].Arm, ends[i].End)] = (ends[k].Arm, ends[k].End);
                    pair[(ends[k].Arm, ends[k].End)] = (ends[i].Arm, ends[i].End);
                    break;
                }
            }
        }

        // ---- chains: walk arms linked by merge pairs ----
        var chains = new List<NetChain>();
        var armUsed = new bool[arms.Count];
        for (int a0 = 0; a0 < arms.Count; a0++)
        {
            for (int e0 = 0; e0 < 2; e0++)
            {
                if (armUsed[a0] || pair.ContainsKey((a0, e0))) continue;
                int curA = a0, startEnd = e0, end = 1 - e0;
                var ptsChain = new List<(int X, int Y)>(arms[curA]);
                if (startEnd == 1) ptsChain.Reverse();
                armUsed[curA] = true;
                var startPt = ptsChain[0];
                var lastPt = ptsChain[^1];
                int guard = 0;
                while (pair.TryGetValue((curA, end), out var nx) && guard++ < arms.Count * 2)
                {
                    int nb = nx.Arm;
                    if (armUsed[nb]) break;
                    var arm = arms[nb];
                    var oriented = nx.End == 0 ? new List<(int X, int Y)>(arm) : Enumerable.Reverse(arm).ToList();
                    for (int t = 1; t < oriented.Count; t++) ptsChain.Add(oriented[t]);
                    armUsed[nb] = true;
                    curA = nb; end = 1 - nx.End;
                    lastPt = ptsChain[^1];
                }
                var chain = new NetChain { Pts = ptsChain };
                chains.Add(chain);
            }
        }
        foreach (var a in arms.Select((v, i) => (v, i)))
            if (!armUsed[a.i]) return null;
        if (chains.Count == 0) return null;

        // ---- endpoints -> pins / junctions, then build the wire for each chain ----
        string? EndPin(List<(ExElem Elem, string Pin, bool IsSource)>? l)
            => l == null || l.Count == 0 ? null : l[0].Pin;

        foreach (var c in chains)
        {
            var p0 = c.Pts[0];
            var p1 = c.Pts[^1];
            bool pin0 = pinAt.ContainsKey(p0) && !junctions.Contains(p0);
            bool pin1 = pinAt.ContainsKey(p1) && !junctions.Contains(p1);
            bool j0 = junctions.Contains(p0);
            bool j1 = junctions.Contains(p1);

            if (pin0 && pin1)
            {
                var a = pinAt[p0][0];
                var b = pinAt[p1][0];
                // Start at the connection's source pin when exactly one end is a source;
                // otherwise use the larger (Y, X) end - the editor's usual trunk direction.
                bool reverse;
                if (a.IsSource && !b.IsSource) reverse = false;
                else if (!a.IsSource && b.IsSource) reverse = true;
                else reverse = p1.Y > p0.Y || (p1.Y == p0.Y && p1.X > p0.X);
                if (reverse) { c.Pts.Reverse(); (a, b) = (b, a); }
                c.Wire = new Wire
                {
                    Source = a.Elem, SourcePin = a.Pin,
                    Target = b.Elem, TargetPin = b.Pin,
                    PathExport = c.Pts, Decomposed = true,
                };
            }
            else if (pin0 && j1)
            {
                var a = pinAt[p0][0];
                c.StartIsJunction = false; c.EndIsJunction = true; c.EndJunction = p1;
                c.Wire = new Wire
                {
                    Source = a.Elem, SourcePin = a.Pin,
                    Target = a.Elem, TargetPin = a.Pin, // branches are oriented pin -> Vn
                    PathExport = c.Pts, Decomposed = true, IsBranch = true,
                };
            }
            else if (j0 && pin1)
            {
                var b = pinAt[p1][0];
                c.Pts.Reverse();
                c.StartIsJunction = true; c.EndIsJunction = false; c.StartJunction = p0;
                c.Wire = new Wire
                {
                    Source = b.Elem, SourcePin = b.Pin,
                    Target = b.Elem, TargetPin = b.Pin,
                    PathExport = c.Pts, Decomposed = true, IsBranch = true,
                };
            }
            else return null; // junction->junction chain: not a shape the editor emits
        }

        // ---- parent trunk + Vn for branches ----
        foreach (var c in chains.Where(x => x.Wire.IsBranch))
        {
            var j = c.StartIsJunction ? c.StartJunction : c.EndJunction;
            NetChain? parent = null; int idx = -1;
            foreach (var p in chains)
            {
                if (ReferenceEquals(p, c)) continue;
                int at = p.Pts.FindIndex(q => q == j);
                if (at > 0 && at < p.Pts.Count - 1) { parent = p; idx = at; break; }
            }
            if (parent == null) return null;
            c.Wire.Parent = parent.Wire;
            c.Wire.Vn = idx;
            c.Wire.IsBranch = true;
        }

        // ---- remap the logical pin references (used by block/var emission) ----
        foreach (var c in chains)
        {
            foreach (var (pt, isStart) in new[] { (c.Pts[0], true), (c.Pts[^1], false) })
            {
                if (!pinAt.TryGetValue(pt, out var list) || junctions.Contains(pt)) continue;
                foreach (var (elem, pin, isSource) in list)
                {
                    if (elem is ExVarPlace vp)
                    {
                        if (!plan.Places.TryGetValue(vp, out var pp)) continue;
                        if (isSource) pp.OutWire = c.Wire; else pp.InWire = c.Wire;
                    }
                    else if (elem is ExBlock b)
                    {
                        var bp = plan.Blocks.FirstOrDefault(x => x.B == b);
                        if (bp == null) continue;
                        if (isSource) bp.OutWires[pin] = c.Wire;
                        else
                        {
                            int ix = b.Inputs.FindIndex(x => x.Name == pin);
                            if (ix >= 0 && ix < bp.InWires.Count) bp.InWires[ix] = c.Wire;
                        }
                    }
                }
            }
        }

        return chains;
    }

    /// <summary>Repairs placement/block wire references that still point at wires removed by
    /// <see cref="DecomposeNets"/>. A decomposed net replaces the export's per-connection wires
    /// with chains; when a pin sits on a geometric junction the chain remap skips it, leaving the
    /// emitted element with a <c>connectId</c> for a wire that is not written. TriStation then
    /// reports the element as unconnected (WG0012) and rewrites it into a broken link variable.
    /// Re-emit a plain wire for each such dangling reference.</summary>
    private static void RepairWireRefs(Plan plan)
    {
        var live = new HashSet<Wire>(plan.Wires);
        var added = new Dictionary<string, Wire>(StringComparer.Ordinal);

        Wire? AddWire(Wire stale, ExElem? target, string targetPin, ExElem? source, string sourcePin, bool targetIsVar, bool sourceIsVar)
        {
            if (source == null || target == null) return null;
            string key = $"{elementIdOf(plan, source)}.{sourcePin}->{elementIdOf(plan, target)}.{targetPin}";
            if (added.TryGetValue(key, out var existing)) return existing;
            added[key] = null!;
            var path = new List<(int X, int Y)>();
            var raw = stale.RawPath.Count >= 2 ? stale.RawPath : stale.PathExport;
            if (raw.Count >= 2) { path.Add(raw[0]); path.Add(raw[^1]); }
            if (path.Count == 0) path.AddRange(stale.PathExport);
            var w = new Wire
            {
                Source = source, SourcePin = sourcePin,
                Target = target, TargetPin = targetPin,
                SourceVar = sourceIsVar ? source as ExVarPlace : null,
                TargetVar = targetIsVar ? target as ExVarPlace : null,
                SourceBlock = sourceIsVar ? null : source as ExBlock,
                TargetBlock = targetIsVar ? null : target as ExBlock,
                PathExport = path,
                TargetIsBlockInput = !targetIsVar,
            };
            plan.Wires.Add(w);
            live.Add(w);
            added[key] = w;
            if (target is ExVarPlace tv && plan.Places.TryGetValue(tv, out var tp)) tp.InWire = w;
            if (source is ExVarPlace sv && plan.Places.TryGetValue(sv, out var sp)) sp.OutWire = w;
            return w;
        }

        foreach (var pp in plan.Places.Values)
        {
            if (pp.InWire != null && !live.Contains(pp.InWire))
            {
                var w = AddWire(pp.InWire, pp.P, pp.InWire.TargetPin, pp.InWire.Source, pp.InWire.SourcePin, true, pp.InWire.SourceVar != null);
                pp.InWire = w;
            }
            if (pp.OutWire != null && !live.Contains(pp.OutWire))
            {
                var w = AddWire(pp.OutWire, pp.OutWire.Target, pp.OutWire.TargetPin, pp.P, pp.OutWire.SourcePin, pp.OutWire.TargetVar != null, true);
                pp.OutWire = w;
            }
        }
        foreach (var bp in plan.Blocks)
        {
            foreach (var pin in bp.OutWires.Keys.ToList())
            {
                var w = bp.OutWires[pin];
                if (live.Contains(w)) continue;
                var repl = AddWire(w, w.Target, w.TargetPin, bp.B, pin, w.TargetVar != null, false);
                if (repl != null) bp.OutWires[pin] = repl; else bp.OutWires.Remove(pin);
            }
            for (int i = 0; i < bp.InWires.Count; i++)
            {
                var w = bp.InWires[i];
                if (w == null || live.Contains(w)) continue;
                if (i >= bp.B.Inputs.Count) { bp.InWires[i] = null; continue; }
                string pin = bp.B.Inputs[i].Name;
                bp.InWires[i] = AddWire(w, bp.B, pin, w.Source, w.SourcePin, false, w.SourceVar != null);
            }
        }
    }

    /// <summary>
    /// Assigns wire ids after the final geometry is known: per sheet, in plan order, a wire
    /// consumes one id per path segment and keeps the last one (TriStation's edit-history
    /// gaps). Recomputes PrimarySpan/lastUniqueID from the last id.
    /// </summary>
    private static void AssignWireIds(Plan plan)
    {
        int next = plan.WireIdStart;
        foreach (var sheet in plan.P.Sheets.OrderBy(s => s.Number))
        {
            foreach (var w in plan.Wires.Where(w => !w.Suppress && w.Target != null && w.Target.Sheet == sheet.Number))
            {
                int segs = Math.Max(1, w.PathExport.Count - 1);
                next += segs - 1;
                w.Id = next++;
            }
        }
        plan.PrimarySpan = next - 1;
        plan.LastId = plan.P.PouType == "program" ? plan.PrimarySpan : 2 * plan.PrimarySpan;
    }

    private static string EdgeKey((int X, int Y) a, (int X, int Y) b)
        => a.X < b.X || (a.X == b.X && a.Y <= b.Y)
            ? $"{a.X},{a.Y}|{b.X},{b.Y}" : $"{b.X},{b.Y}|{a.X},{a.Y}";

    private static (int X, int Y) Dir((int X, int Y) a, (int X, int Y) b) => (b.X - a.X, b.Y - a.Y);

    private static Plan BuildPlan(ExPou p)
    {
        var plan = new Plan { P = p };
        foreach (var v in p.Vars) plan.VarByName[v.Name] = v;

        // 1. raw wires in connection document order
        var connWire = new Dictionary<ExConn, Wire>();

        // A POU may reuse localIds: SYSTEMSOE exports four identical SOE sub-networks that
        // share block localIds (1238/1239) and variable localIds. The sub-networks repeat at a
        // fixed document period (each begins with the same block id), so group the elements
        // into copies and resolve each refLocalId only against elements in the owner's own
        // copy. A connection whose source id has no element in the same copy is dropped (this
        // matches SYSTEMSOE's lossy 4th copy, which only carries 5 of its 32 input vars).
        var byId = new Dictionary<int, List<ExElem>>();
        foreach (var e in p.ElemOrder)
        {
            if (e.ExportId < 0) continue;
            if (!byId.TryGetValue(e.ExportId, out var lst)) { lst = new List<ExElem>(); byId[e.ExportId] = lst; }
            lst.Add(e);
        }

        int period = 0, anchorDoc = -1;
        {
            foreach (var e in p.ElemOrder) if (e is ExBlock) { anchorDoc = e.DocIndex; break; }
            if (anchorDoc >= 0)
            {
                int anchorId = p.ElemOrder.First(e => e is ExBlock).ExportId;
                foreach (var e in p.ElemOrder)
                    if (e.DocIndex > anchorDoc && e.ExportId == anchorId) { period = e.DocIndex - anchorDoc; break; }
            }
        }
        foreach (var e in p.ElemOrder)
            e.CopyId = (period > 0 && e.DocIndex >= anchorDoc) ? (e.DocIndex - anchorDoc) / period : 0;

        ExElem? ResolveSrc(int srcId, ExElem owner)
        {
            if (!byId.TryGetValue(srcId, out var lst) || lst.Count == 0) return null;
            if (lst.Count == 1) return lst[0];
            ExElem? best = null;
            foreach (var e in lst)
                if (e.CopyId == owner.CopyId && (best == null || Math.Abs(e.DocIndex - owner.DocIndex) < Math.Abs(best.DocIndex - owner.DocIndex))) best = e;
            return best;
        }

        foreach (var c in p.ConnOrder)
        {
            var src = ResolveSrc(c.SrcExportId, c.Owner!);
            if (src == null) continue;
            var tgt = c.Owner!;
            if (src.Clipped || tgt.Clipped) continue;
            string srcPin = c.SrcPin, ownerPin = c.OwnerPin;
            var w = new Wire { Source = src, Target = tgt, FeedBack = c.FeedBack, PathExport = new List<(int, int)>(c.Path) };
            w.RawPath = new List<(int X, int Y)>(c.Path);
            if (src is ExVarPlace sv) { w.SourceVar = sv; w.SourcePin = sv.Expression; }
            else if (src is ExBlock sb)
            {
                w.SourceBlock = sb;
                w.SourcePin = srcPin.Length > 0 ? srcPin
                    : (sb.Outputs.Count > 0 ? sb.Outputs[0].Name : "");
            }
            if (tgt is ExVarPlace tv) { w.TargetVar = tv; w.TargetPin = tv.Expression; }
            else if (tgt is ExBlock tb) { w.TargetBlock = tb; w.TargetPin = ownerPin; w.TargetIsBlockInput = true; }
            bool degenerate = c.Path.Count >= 1 && c.Path.All(pp => pp == c.Path[0]);
            // A literal that feeds a block input is joined by a normal wire (TriStation stores a
            // CDGMWireXml between the CONSTANT out-variable and the pin). A literal that feeds a
            // plain variable keeps the direct pin link only; wiring that shape is rejected by the
            // compiler as "Feature not implemented".
            bool constToBlock = w.SourceVar != null && w.SourceVar.IsConstant && w.TargetIsBlockInput;
            bool srcDrop = (w.SourceVar != null && w.SourceVar.IsConstant && !constToBlock) || degenerate;
            bool tgtDrop = w.TargetVar != null && w.TargetVar.IsConstant && w.TargetVar.Kind != "in";
            if (srcDrop || tgtDrop) w.Suppress = true;
            if (degenerate && (w.SourceVar == null || !w.SourceVar.IsConstant)) w.Drop = true;
            // A zero-length link is stored as a direct pin link on the destination
            // (inConnection = source pin/name, inConnectId = source element), not as a wire.
            // Without it the destination is reported as an unconnected element.
            if (degenerate && w.TargetVar != null)
            {
                if (w.SourceVar != null && !w.SourceVar.IsConstant)
                {
                    w.TargetVar.DirectInSource = w.SourceVar;
                    w.TargetVar.DirectInPin = w.SourceVar.Expression;
                }
                else if (w.SourceBlock != null && w.SourcePin.Length > 0)
                {
                    w.TargetVar.DirectInSource = w.SourceBlock;
                    w.TargetVar.DirectInPin = w.SourcePin;
                }
            }
            plan.Wires.Add(w);
            connWire[c] = w;
        }

        // A zero-length outgoing connection from a variable that is itself a sink is dropped
        // by the importer; one from a pure source is kept as a direct pin link.
        var inboundVars = new HashSet<ExVarPlace>(plan.Wires
            .Where(w => !w.Suppress && w.TargetVar != null).Select(w => w.TargetVar!));
        foreach (var w in plan.Wires)
            if (w.Drop && w.SourceVar != null && !inboundVars.Contains(w.SourceVar)) w.Drop = false;

        // 2. group by source -> choose main wire, mark branches (fallback for nets that the
        //    geometric decomposer does not rebuild; it runs after the ST layout).
        foreach (var g in plan.Wires.Where(w => !w.Suppress && !w.Decomposed).GroupBy(w => (w.Source, w.SourcePin)))
        {
            var list = g.ToList();
            if (list.Count == 1) continue;
            var main = list.OrderBy(w => TargetPriority(p, w)).ThenByDescending(w => w.Target.X).First();
            foreach (var b in list)
            {
                if (ReferenceEquals(b, main)) continue;
                b.IsBranch = true;
                b.Parent = main;

                var pathB = b.PathExport;
                var pathM = main.PathExport;
                int k = 0;
                while (k < pathB.Count && k < pathM.Count
                       && pathB[pathB.Count - 1 - k] == pathM[pathM.Count - 1 - k]) k++;
                int keep = pathB.Count - k;
                if (keep < 1) keep = 1;
                b.PathExport = pathB.Take(keep).ToList();
            }
        }

        // 3. main paths: reverse, insert junctions in path order, number Vn
        foreach (var g in plan.Wires.Where(w => !w.Suppress && !w.Decomposed).GroupBy(w => (w.Source, w.SourcePin)))
        {
            var main = g.FirstOrDefault(w => !w.IsBranch);
            if (main == null) continue;
            var branches = g.Where(w => w.IsBranch).ToList();
            if (branches.Count == 0) { main.PathExport.Reverse(); continue; }

            var rev = Enumerable.Reverse(main.PathExport).ToList();
            var junctions = branches.Select(b => (Point: b.PathExport[^1], Branch: b)).ToList();

            var ordered = new List<((int X, int Y) Point, Wire Branch, int Seg, long Dist)>();
            foreach (var j in junctions)
            {
                for (int i = 0; i + 1 < rev.Count; i++)
                {
                    if (OnSegment(j.Point, rev[i], rev[i + 1]))
                    {
                        var d = (long)(j.Point.X - rev[i].X) * (j.Point.X - rev[i].X)
                              + (long)(j.Point.Y - rev[i].Y) * (j.Point.Y - rev[i].Y);
                        ordered.Add((j.Point, j.Branch, i, d));
                        break;
                    }
                }
            }
            ordered = ordered.OrderBy(o => o.Seg).ThenBy(o => o.Dist).ToList();
            for (int n = 0; n < ordered.Count; n++) ordered[n].Branch.Vn = n + 1;

            var final = new List<(int, int)>();
            var bySeg = ordered.GroupBy(o => o.Seg).ToDictionary(g2 => g2.Key, g2 => g2.ToList());
            for (int i = 0; i + 1 < rev.Count; i++)
            {
                final.Add(rev[i]);
                if (bySeg.TryGetValue(i, out var js))
                    foreach (var j in js) final.Add(j.Point);
            }
            final.Add(rev[^1]);
            main.PathExport = final;
        }

        // 4. element ids: TriStation renumbers every import from 1, sheet by sheet, and
        //    within a sheet in a fixed kind order: network dividers, blocks/functions,
        //    comments, variable placements, then the wires authored on that sheet. Wires
        //    consume one id per segment (path points - 1) and keep the last one, which is
        //    why the reference's wire ids show gaps where a multi-bend wire was drawn.
        //    Reusing the export's localId is only correct for documents whose ids already
        //    follow that order (the library blocks); renumbering matches both cases.
        _commentIds.Clear();
        int nextId = 1;
        foreach (var sheet in p.Sheets.OrderBy(s => s.Number))
        {
            int sheetNo = sheet.Number;
            foreach (var dv in p.Dividers.Where(d => !d.Clipped && d.Sheet == sheetNo).OrderBy(d => d.DocIndex))
                plan.DividerIds[dv] = nextId++;

            foreach (var b in p.Blocks.Where(b => !b.Clipped && b.Sheet == sheetNo).OrderBy(b => b.DocIndex))
                plan.Blocks.Add(new BlockPlan { B = b, Id = nextId++ });

            foreach (var c in p.ElemOrder.OfType<ExComment>()
                         .Where(c => !c.Clipped && c.Sheet == sheetNo).OrderBy(c => c.DocIndex))
                _commentIds[c] = nextId++;

            foreach (var v in p.ElemOrder.OfType<ExVarPlace>()
                         .Where(v => !v.Clipped && v.Sheet == sheetNo).OrderBy(v => v.DocIndex))
                plan.Places[v] = new PlacePlan { P = v, Id = nextId++ };
        }
        // Wire ids depend on the final (decomposed) geometry, so they are assigned after
        // ComputeLayout + DecomposeNets by AssignWireIds.
        plan.WireIdStart = nextId;
        plan.PrimarySpan = nextId - 1;
        plan.LastId = p.PouType == "program" ? plan.PrimarySpan : 2 * plan.PrimarySpan;
        return plan;
    }

    private static readonly Dictionary<ExComment, int> _commentIds = new();

    // ============================================================ ST layout emulation

    private static void ComputeLayout(Plan plan)
    {
        var p = plan.P;
        // topological order (Kahn), ready ordered by (Y, X)
        var blocks = plan.Blocks;
        var remaining = new List<BlockPlan>(blocks);
        var seq = new List<BlockPlan>();
        var deps = new Dictionary<BlockPlan, HashSet<BlockPlan>>();
        foreach (var b in blocks)
        {
            var d = new HashSet<BlockPlan>();
            foreach (var w in plan.Wires)
                if (w.TargetBlock == b.B && w.SourceBlock != null)
                {
                    var sp = blocks.FirstOrDefault(x => x.B == w.SourceBlock);
                    if (sp != null && !ReferenceEquals(sp, b)) d.Add(sp);
                }
            deps[b] = d;
        }
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(b => deps[b].All(d => !remaining.Contains(d))).ToList();
            if (ready.Count == 0) ready = new List<BlockPlan>(remaining);
            var pick = ready.OrderBy(b => b.B.Y).ThenBy(b => b.B.X).First();
            seq.Add(pick);
            remaining.Remove(pick);
        }
        for (int i = 0; i < seq.Count; i++) seq[i].Sequence = i + 1;

        // wire wiring per block plan
        foreach (var bp in blocks)
        {
            bp.OutWires.Clear();
            for (int oi = 0; oi < bp.B.Outputs.Count; oi++)
            {
                var pin = bp.B.Outputs[oi];
                // The XML names pins positionally ("OUT_17"); the connection's formalParameter
                // carries the library pin name. Try both so a pin is never left unwired.
                string pn = PortName(bp.B, false, oi, pin.Name);
                var w = plan.Wires.Where(x => ReferenceEquals(x.SourceBlock, bp.B) && !x.Suppress
                                              && (x.SourcePin == pn || x.SourcePin == pin.Name))
                                   .OrderBy(x => x.IsBranch ? 1 : 0).FirstOrDefault();
                if (w != null) bp.OutWires[pin.Name] = w;
            }
            bp.InWires = new List<Wire?>();
            for (int i = 0; i < bp.B.Inputs.Count; i++)
            {
                var pin = bp.B.Inputs[i];
                string pn = PortName(bp.B, true, i, pin.Name);
                bp.InWires.Add(plan.Wires.FirstOrDefault(x => x.TargetBlock == bp.B
                                                              && (x.TargetPin == pn || x.TargetPin == pin.Name)));
            }
            bp.SinkAssigns = new List<Wire>();
            foreach (var pin in bp.B.Outputs)
            {
                if (!bp.OutWires.TryGetValue(pin.Name, out var main)) continue;
                if (main.TargetVar != null) bp.SinkAssigns.Add(main);
            }
            // branch var sinks (branch wires off any of this block's main wires)
            var branchSinks = plan.Wires
                .Where(x => x.IsBranch && x.TargetVar != null && !x.Suppress && x.Parent != null
                            && x.Parent.SourceBlock == bp.B)
                .OrderBy(x => x.Vn);
            bp.SinkAssigns.AddRange(branchSinks);
        }

        // Temp allocation. TriStation's compiler keeps one temp per type (_TEMP_BOOL_1,
        // _TEMP_REAL_1, _TEMP_DINT_1, ...), numbering each type independently and reusing a slot
        // once every consumer of the value it holds has been emitted. The declaration order is the
        // order in which the slots are first needed, which is what the listing's VAR_TEMP shows.
        var tempOf = new Dictionary<BlockPlan, int>();
        var liveByType = new Dictionary<string, Dictionary<int, BlockPlan>>(StringComparer.OrdinalIgnoreCase);
        var tempDecls = new List<(string Name, string Type)>();
        var declaredTemps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<BlockPlan>();
        foreach (var b in seq)
        {
            if (b.B.IsFunction)
            {
                // The result type of a generic operator (ADD/AND/GT/...) is the type of its
                // operands, not BOOL: an ADD over DINT tags must land in _TEMP_DINT_n, otherwise the
                // listing assigns a DINT expression to a BOOL variable and the compiler rejects it.
                string type = FunctionResultType(b.B.TypeName);
                if (type == "BOOL")
                {
                    foreach (var iw in b.InWires)
                    {
                        if (iw?.SourceVar != null && iw.SourceVar.DataType.Length > 0)
                        {
                            type = iw.SourceVar.DataType;
                            break;
                        }
                        if (iw?.SourceBlock != null)
                        {
                            var sp = blocks.FirstOrDefault(x => x.B == iw.SourceBlock);
                            if (sp != null && sp.Temp.StartsWith("_TEMP_", StringComparison.Ordinal))
                            {
                                int us = sp.Temp.IndexOf('_', 6);
                                if (us > 6) { type = sp.Temp.Substring(6, us - 6); break; }
                            }
                        }
                    }
                    if (type.StartsWith("ANY", StringComparison.OrdinalIgnoreCase) || type.Length == 0) type = "BOOL";
                }
                if (!liveByType.TryGetValue(type, out var live))
                    liveByType[type] = live = new Dictionary<int, BlockPlan>();
                int idx = 1;
                while (live.ContainsKey(idx)) idx++;
                tempOf[b] = idx;
                b.Temp = $"_TEMP_{type}_{idx}";
                if (declaredTemps.Add(b.Temp)) tempDecls.Add((b.Temp, type));
                live[idx] = b;
            }
            emitted.Add(b);
            foreach (var kv in liveByType.ToList())
            {
                foreach (var slot in kv.Value.ToList())
                {
                    var consumers = blocks.Where(x => x != slot.Value && deps[x].Contains(slot.Value));
                    if (consumers.All(c => emitted.Contains(c))) kv.Value.Remove(slot.Key);
                }
            }
        }
        int tempCount = tempDecls.Count;

        // header line computation
        var lines = new List<string>();
        void Add(string s) => lines.Add(s);
        int Line() => lines.Count + 1;

        Add("(* Line 1: Extern Declaration *)");
        // One annotated declaration per distinct block type, in first-use order, exactly like the
        // listing TriStation writes: functions as FUNCTION EXTERN with their result type, function
        // blocks with their full interface (including project-defined ones).
        var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in seq)
        {
            if (!declared.Add(b.B.TypeName)) continue;
            List<LibPin>? pins = null;
            if (Lib.TryGetValue(b.B.TypeName, out var meta)) pins = meta.Pins;
            else if (plan.ProjectTypes != null && plan.ProjectTypes.TryGetValue(b.B.TypeName, out var pt)) pins = pt.Pins;

            if (b.B.IsFunction)
            {
                // Only declared functions appear in the listing: library functions (SCALE,
                // ReportBadParam, ... typed as element type 4) and the type conversions, whose
                // result pin is the anonymous "()" placeholder. The generic operators
                // (MOVE/AND/OR/NOT/GT/...) are built into the language.
                var result = pins?.FirstOrDefault(x => x.Vt == 1);
                bool isLibFunction = Lib.TryGetValue(b.B.TypeName, out var fm) && fm.Type == 4;
                bool isConversion = result != null && result.Name == "()";
                string resultType = result?.DataType ?? "";
                if (pins == null || resultType.Length == 0 || (!isLibFunction && !isConversion))
                {
                    declared.Remove(b.B.TypeName);
                    continue;
                }
                Add($"FUNCTION EXTERN {b.B.TypeName} : {resultType}");
                var ins = pins!.Where(x => x.Vt == 0).ToList();
                if (ins.Count > 0)
                {
                    Add("VAR_INPUT");
                    int anon = 0;
                    foreach (var x in ins)
                    {
                        string pinName = x.Name.Length > 0 ? x.Name : "IN" + anon++;
                        Add($"\t{pinName} : {x.DataType};");
                    }
                    Add("END_VAR");
                }
                Add("END_FUNCTION");
            }
            else
            {
                Add($"FUNCTION_BLOCK EXTERN {b.B.TypeName}");
                if (pins != null)
                {
                    var ins = pins.Where(x => x.Vt == 0).ToList();
                    var outs = pins.Where(x => x.Vt == 1).ToList();
                    var ios = pins.Where(x => x.Vt == 3).ToList();
                    if (ins.Count > 0) { Add("VAR_INPUT"); foreach (var x in ins) Add($"\t{x.Name} : {x.DataType};"); Add("END_VAR"); }
                    if (outs.Count > 0) { Add("VAR_OUTPUT"); foreach (var x in outs) Add($"\t{x.Name} : {x.DataType};"); Add("END_VAR"); }
                    if (ios.Count > 0) { Add("VAR"); foreach (var x in ios) Add($"\t{x.Name} : {x.DataType};"); Add("END_VAR"); }
                }
                Add("END_FUNCTION_BLOCK");
            }
            Add("");
        }
        Add($"(* Line {Line()}: POU Header *)");
        Add($"FUNCTION_BLOCK {p.Name}");
        var declLine = new Dictionary<string, int>();
        var inputs = p.Vars.Where(v => v.Section == "input").ToList();
        var inouts = p.Vars.Where(v => v.Section == "inOut").ToList();
        var outputs = p.Vars.Where(v => v.Section == "output").ToList();
        // Program variables exported as <globalVars> are plain VAR entries in the listing; leaving
        // them out makes the listing reference undeclared identifiers.
        var locals = p.Vars.Where(v => v.Section == "local" || v.Section == "global").ToList();
        void VarSection(string kw, List<ExVar> vs)
        {
            if (vs.Count == 0) return;
            Add(kw);
            foreach (var v in vs) { declLine[v.Name] = Line(); Add($"\t{v.Name} : {v.DataType};"); }
            Add("END_VAR");
        }
        VarSection("VAR_INPUT", inputs);
        VarSection("VAR_OUTPUT", outputs);
        VarSection("VAR", locals);
        if (tempCount > 0)
        {
            Add("VAR_TEMP");
            foreach (var td in tempDecls) Add($"\t{td.Name} : {td.Type};");
            Add("END_VAR");
        }
        Add("");
        Add($"(* Line {Line()}: POU Body *)");
        // Network<N> headers run up to the last sheet that actually carries logic; TriStation does
        // not list an empty trailing sheet. Element sheet numbers come from the export, so take the
        // highest one in use rather than the declared sheet count.
        int networkCount = 1;
        foreach (var bp0 in plan.Blocks) networkCount = Math.Max(networkCount, bp0.B.Sheet);
        foreach (var vp in plan.Places.Keys) networkCount = Math.Max(networkCount, vp.Sheet);
        for (int n = 1; n <= networkCount; n++)
            Add($"(* Line {Line()}: Network{n}, Begin *)");

        foreach (var b in seq)
        {
            b.FirstLine = Line();
            string t = b.B.TypeName;
            if (b.B.IsFunction)
                Add($"(* Line {b.FirstLine}: Network{b.B.Sheet}, Block {b.Sequence}, FUNCTION \"{t}\"  *)");
            else
                Add($"(* Line {b.FirstLine}: Network{b.B.Sheet}, Block {b.Sequence}, FUNCTION_BLOCK \"{b.B.InstanceName}\", Type \"{t}\"  *)");
            foreach (var l in ExprLines(b)) Add(l);
        }
        Add("END_FUNCTION_BLOCK");
        int endLine = Line() - 1;
        plan.Listing = lines;
        for (int i = 0; i < seq.Count; i++)
        {
            int nextLine = i + 1 < seq.Count ? seq[i + 1].FirstLine : endLine;
            seq[i].LineCount = nextLine - seq[i].FirstLine;
        }

        // (block temps were assigned above when the slots were allocated)

        // attach wires to placements
        foreach (var w in plan.Wires)
        {
            if (w.Drop) continue;
            if (!w.IsBranch && w.SourceVar != null && plan.Places.TryGetValue(w.SourceVar, out var ps))
                ps.OutWire = w;
            if (w.TargetVar != null && plan.Places.TryGetValue(w.TargetVar, out var pt))
                pt.InWire = w;
        }

        foreach (var pp in plan.Places.Values)
        {
            if (pp.OutWire != null)
            {
                var tgt = pp.OutWire.TargetBlock;
                var bp = tgt == null ? null : blocks.FirstOrDefault(x => x.B == tgt);
                pp.LineNumber = bp != null ? bp.FirstLine - 1 : 0;
            }
            else if (pp.InWire != null)
                pp.LineNumber = declLine.TryGetValue(pp.P.Expression, out var ln) ? ln : 0;
            else
                pp.LineNumber = declLine.TryGetValue(pp.P.Expression, out var ln2) ? ln2 : 0;
        }

        // stash for emission
        plan.BlockSeq = seq;
        plan.TempCount = tempCount;

        IEnumerable<string> ExprLines(BlockPlan b)
        {
            string Resolve(Wire w)
            {
                if (w.IsBranch && w.Parent != null) return Resolve(w.Parent);
                if (w.SourceVar != null)
                {
                    var sv = w.SourceVar;
                    if (sv.IsConstant) return TypedLiteral(sv);
                    return sv.Expression;
                }
                if (w.SourceBlock != null)
                {
                    var sp = blocks.FirstOrDefault(x => x.B == w.SourceBlock);
                    return w.SourceBlock.IsFunction ? (sp?.Temp ?? "") : $"{w.SourceBlock.InstanceName}.{w.SourcePin}";
                }
                return "";
            }
            if (!b.B.IsFunction)
            {
                yield return $"\t{b.B.InstanceName}(";
                for (int i = 0; i < b.B.Inputs.Count; i++)
                {
                    var w = b.InWires[i];
                    if (w == null) continue;
                    yield return $"\t{b.B.Inputs[i].Name} :=";
                    yield return "\t(";
                    yield return $"\t\t{Resolve(w)}";
                    yield return "\t)";
                }
                yield return "\t);";
            }
            else
            {
                var ins = b.InWires.Where(w => w != null).Select(w => Resolve(w!)).ToList();
                string op = b.B.Outputs.Count > 0 ? b.B.Outputs[0].Name : b.B.TypeName;
                // A library function with named parameters is called as NAME((a),(b),...); the
                // elementary operators (AND, OR, ADD, GT, ... - anonymous ANY pins) are infix.
                bool isCall = Lib.TryGetValue(b.B.TypeName, out var fm)
                              && fm.Pins.Any(x => x.Vt == 0 && x.Name.Length > 0);
                yield return $"\t{b.Temp} :=(";
                if (isCall && ins.Count > 0)
                {
                    yield return $"\t{b.B.TypeName}(";
                    for (int i = 0; i < ins.Count; i++)
                    {
                        if (i > 0) yield return "\t,";
                        yield return "\t(";
                        yield return $"\t\t{ins[i]}";
                        yield return "\t)";
                    }
                    yield return "\t);";
                }
                else if (ins.Count <= 1)
                {
                    yield return "\t";
                    if (ins.Count == 1)
                    {
                        yield return "\t(";
                        yield return $"\t\t{ins[0]}";
                        yield return "\t)";
                    }
                }
                else
                {
                    for (int i = 0; i < ins.Count; i++)
                    {
                        if (i > 0) yield return $"\t{op}";
                        yield return "\t(";
                        yield return $"\t\t{ins[i]}";
                        yield return "\t)";
                    }
                }
                if (!isCall || ins.Count == 0) yield return "\t);";
            }
            foreach (var s in b.SinkAssigns)
            {
                yield return $"\t{s.TargetVar!.Expression} :=(";
                yield return $"\t\t{Resolve(s)}";
                yield return "\t);";
            }
        }
    }

    // ============================================================ emission

    private static string Esc(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string CData(string s)
        => "<![CDATA[" + s.Replace("]]>", "]]]]><![CDATA[>") + "]]>";

    /// <summary>Maps every library element to the installed library's catalogue identity. The
    /// embedded base predates the installed TriStation library, so its dogTags (and the hex FName
    /// that mirrors them) are stale; the compiler resolves library elements by these ids.</summary>
    public static IReadOnlyDictionary<string, int> LibraryDogTags()
    {
        EnsureLibrarySnapshots();
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in Lib) map[kv.Key] = kv.Value.DogTag;
        return map;
    }

    /// <summary>Compiler temp variables of a POU, exactly as the element's listing declares them.
    /// A caller's block snapshot has to carry these: they are part of the interface TriStation
    /// compares against when a project is compiled, and a snapshot without them is reported as an
    /// out-of-date element.</summary>
    public static List<LibPin> TempPins(string xmlPath, string pouName)
    {
        var pins = new List<LibPin>();
        string listing;
        try { listing = BuildListing(xmlPath, pouName); }
        catch { return pins; }

        bool inTemp = false;
        foreach (var raw in listing.Split('\n'))
        {
            string line = raw.TrimEnd('\r').Trim();
            if (!inTemp)
            {
                if (line == "VAR_TEMP") inTemp = true;
                continue;
            }
            if (line == "END_VAR") break;
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon].Trim();
            string type = line[(colon + 1)..].Trim().TrimEnd(';').Trim();
            if (name.Length == 0 || type.Length == 0) continue;
            pins.Add(new LibPin { Name = name, DataType = type, InitVal = "", Vt = 4, Flags = 48 });
        }
        return pins;
    }

    /// <summary>ST literal with an explicit type, which is what the compiler expects when a
    /// constant feeds a typed pin (<c>REAL#4095.0</c>, <c>DINT#5</c>). Booleans stay TRUE/FALSE.</summary>
    private static string TypedLiteral(ExVarPlace v)
    {
        string expr = v.Expression.Trim();
        if (expr.Length == 0) return expr;
        if (expr.Equals("TRUE", StringComparison.OrdinalIgnoreCase) || expr.Equals("FALSE", StringComparison.OrdinalIgnoreCase))
            return expr.ToUpperInvariant();
        if (!double.TryParse(expr, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return expr;

        string type = v.DataType.Length > 0 && !v.DataType.Equals("BOOL", StringComparison.OrdinalIgnoreCase)
            ? v.DataType
            : (expr.Contains('.') || expr.Contains('e') || expr.Contains('E') ? "REAL" : "DINT");
        return "(" + type + "#" + expr + ")";
    }

    /// <summary>Result type of a function block/function as the listing declares its temp: the
    /// element's first output pin when that type is concrete, BOOL for the generic operators
    /// (MOVE/AND/OR/... whose pins are ANY*).</summary>
    private static string FunctionResultType(string typeName)
    {
        if (Lib.TryGetValue(typeName, out var meta))
        {
            var outPin = meta.Pins.FirstOrDefault(x => x.Vt == 1);
            if (outPin != null && outPin.DataType.Length > 0
                && !outPin.DataType.StartsWith("ANY", StringComparison.OrdinalIgnoreCase))
                return outPin.DataType;
        }
        return "BOOL";
    }

    private static int Tx(int v) => 10 * v + 40;

    public static byte[] GenerateBytes(string xmlPath, string pouName, IReadOnlyDictionary<string, TagInfo>? tagDesc = null, IReadOnlySet<string>? referencedTypes = null, int templateSheetCount = -1, bool forceDuplicated = false, IReadOnlyDictionary<string, ProjectTypeInfo>? projectTypes = null)
    {
        EnsureLibrarySnapshots();
        return Encoding.UTF8.GetBytes(Generate(xmlPath, pouName, tagDesc, referencedTypes, templateSheetCount, forceDuplicated, projectTypes));
    }

    /// <summary>Builds the ST listing TriStation keeps beside an FBD element (the <c>.IST</c>
    /// stream): the element's declaration header plus one annotated block of ST per placed block.
    /// This is the artifact a project needs for its elements to count as compiled.</summary>
    public static string BuildListing(string xmlPath, string pouName)
    {
        EnsureLibrarySnapshots();
        var p = Parse(xmlPath, pouName, null, null);
        var plan = BuildPlan(p);
        plan.ProjectTypes = BuildAllProjectTypes(xmlPath);
        ComputeLayout(plan);
        return plan.Listing == null ? "" : string.Join("\r\n", plan.Listing) + "\r\n";
    }

    /// <summary>Interface (pins only) of every function block defined by the XML, used for the
    /// extern declarations at the top of a listing.</summary>
    public static Dictionary<string, ProjectTypeInfo> BuildAllProjectTypes(string xmlPath)
    {
        var map = new Dictionary<string, ProjectTypeInfo>(StringComparer.OrdinalIgnoreCase);
        var doc = XmlRepair.Load(xmlPath);
        foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "pou"))
        {
            string name = (string?)e.Attribute("name") ?? "";
            string type = (string?)e.Attribute("pouType") ?? "";
            if (name.Length == 0 || type == "program") continue;
            try { map[name] = BuildProjectType(xmlPath, name); }
            catch { /* malformed POU: leave it out of the extern list */ }
        }
        return map;
    }

    private static string Generate(string xmlPath, string pouName, IReadOnlyDictionary<string, TagInfo>? tagDesc = null, IReadOnlySet<string>? referencedTypes = null, int templateSheetCount = -1, bool forceDuplicated = false, IReadOnlyDictionary<string, ProjectTypeInfo>? projectTypes = null)
    {
        var p = Parse(xmlPath, pouName, tagDesc, referencedTypes);
        // TriStation stores the diagram twice only when the export itself carries two copies of
        // the POU (one <pou> element per stored document). A normal project keeps a single copy:
        // writing a second copy here makes the editor draw every block twice on open.
        bool duplicated = forceDuplicated;
        var plan = BuildPlan(p);
        plan.ProjectTypes = projectTypes;
        _ = _commentIds;
        ComputeLayout(plan);
        // The ST layout above uses the logical wires; the FBD document uses the editor's
        // net decomposition and its wire ids.
        DecomposeNets(plan);
        RepairWireRefs(plan);
        AssignWireIds(plan);
        plan.LastId = duplicated ? 2 * plan.PrimarySpan : plan.PrimarySpan;

        string sheetTemplate = p.SheetTemplate.Length > 0 ? p.SheetTemplate : "SheetTemplateD";
        string drawingSize = sheetTemplate.StartsWith("SheetTemplate", StringComparison.Ordinal)
            ? sheetTemplate.Substring("SheetTemplate".Length) : "D";
        // Zone counts are fixed per sheet template (not derived from the XML page size):
        // SheetTemplateC = 12x6, SheetTemplateD = 16x8.
        bool cSize = sheetTemplate.EndsWith("C", StringComparison.Ordinal);
        int hZoneCount = cSize ? 12 : 16;
        int vZoneCount = cSize ? 6 : 8;

        var sb = new StringBuilder(20000);
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\r\n");
        sb.Append("<SerializableClass Type=\"Serializable\" Version=\"1.0\" ClassType=\"CFBDDocXml\"")
              .Append($" pouName=\"{Esc(p.Name)}\" pouType=\"{Esc(p.PouType)}\" pouVersion=\"{Esc(p.Version)}\"")
          .Append($" dateTime=\"{DateTime.Now.ToString("M/d/yyyy h:mm:ss tt", CultureInfo.InvariantCulture)}\"")
          .Append($" sheetTemplate=\"{Esc(sheetTemplate)}\" drawingSize=\"{Esc(drawingSize)}\" hZoneCount=\"{hZoneCount}\" vZoneCount=\"{vZoneCount}\"")
          .Append($" attributes=\"1204\" lastZoom=\"0\" lastMonVar=\"0\" lastUniqueID=\"{plan.LastId}\"")
          .Append(" D0=\"\" D1=\"\" D2=\"\" D3=\"\" D4=\"\" D5=\"\" D6=\"\" D7=\"\" D8=\"\" D9=\"\"")
          .Append($" drawingNumber=\"{Esc(p.DrawingNumber)}\" drawingTitle=\"{Esc(p.DrawingTitle)}\"")
          .Append($" approvedName=\"\" approvedTime=\"\" rev=\"{Esc(p.Rev)}\"");
        sb.Append(p.PouType == "program"
            ? " extOptFlags=\"513\">"
            : " extOptFlags=\"3\" cemEventInputs=\"0\" cemActionOutputs=\"0\">");

        // declarations
        sb.Append("<Collection Name=\"declarations\" ClassType=\"CTS2Vars\" CollectionType=\"1\">");
        // Programs list every referenced global tag first (sorted case-sensitively), then all
        // remaining declarations (locals + function-block instances) sorted together.
        // Function blocks keep their interface order.
        IEnumerable<ExVar> declOrder = p.Vars;
        if (p.PouType == "program")
            declOrder = p.Vars.Where(v => !v.IsInstance && v.Section == "global").OrderBy(v => v.Name, StringComparer.Ordinal)
                .Concat(p.Vars.Where(v => v.IsInstance || v.Section != "global").OrderBy(v => v.Name, StringComparer.Ordinal));
        foreach (var v in declOrder)
        {
            var places = p.Placements.Where(x => x.Expression == v.Name).ToList();
            // varVTType follows the declaration's interface section, not how the wire happens
            // to use it: a local (or inOut) that is only ever written is still a local inOut
            // (3), not an output (1). Reference FBDs confirm this for every POU.
            string vt;
            if (v.Section == "global") vt = "5";
            else if (v.IsInstance) vt = "3";
            else vt = v.Section switch { "input" => "0", "output" => "1", _ => "3" };
            // Instances of library elements inside a POU that is actually referenced (built)
            // carry the 0x04000000 "library instance" bit; unbuilt POUs and project-defined
            // function blocks use the plain instance flags.
            string flags = v.IsInstance
                ? (p.IsBuilt && Lib.ContainsKey(v.Derived) ? "100663298" : "33554434")
                : "0";
            // The diagram is stored twice (primary + secondary copy), so each placement and
            // each FB instance occurrence is counted twice in the reference's reference
            // counts. A POU with no logic is not duplicated.
            int copies = duplicated ? 2 : 1;
            int refCount = copies * (v.IsInstance
                ? p.Blocks.Count(b => b.InstanceName == v.Name)
                : places.Count);
            // TriStation stores fbRefCount equal to refCount for instances (verified across
            // programs and function blocks); non-instance declarations carry 0.
            string fbrc = v.IsInstance ? refCount.ToString() : "0";
            string desc = v.Doc;
            string dataType = v.IsInstance ? v.Derived : v.DataType;
            if (v.Section == "global")
            {
                if (p.TagDesc != null && p.TagDesc.TryGetValue(v.Name, out var ti))
                {
                    // Descriptions in the declarations collection come from the POU's own
                    // interface, never from the tag table (reference FBDs keep desc="" for
                    // globals even when the tag has a description). The *type*, however, is
                    // authoritative in the tag table: FBD global references often carry no
                    // <type> (which we would otherwise default to BOOL) while the tag is
                    // DINT/REAL, and the mismatch fails the build (WC0002).
                    if (ti.DataType.Length > 0) dataType = ti.DataType;
                }
            }
            sb.Append($"<Class name=\"{Esc(v.Name)}\" dataType=\"{Esc(dataType)}\"")
              .Append($" initVal=\"{Esc(v.InitVal)}\" desc=\"{Esc(desc)}\" varVTType=\"{vt}\" refCount=\"{refCount}\"")
              .Append($" fbRefCount=\"{fbrc}\" flags=\"{flags}\" writes=\"0\"/>");
        }
        sb.Append("</Collection>");

        // hiddenVarTypes
        sb.Append("<Collection Name=\"hiddenVarTypes\" ClassType=\"CTS2HiddenVarTypeXML\" CollectionType=\"1\"/>");

        // snapshots (type first-use by doc order)
        if (p.Blocks.Count == 0)
        {
            sb.Append("<Collection Name=\"snapshots\" ClassType=\"CPRJLogElemSnapshot\" CollectionType=\"1\"/>");
        }
        else
        {
            sb.Append("<Collection Name=\"snapshots\" ClassType=\"CPRJLogElemSnapshot\" CollectionType=\"1\">");
            int snapCopies = duplicated ? 2 : 1;
            // Snapshot records follow first use in the stored sheet order: sheets are written
            // in ascending number, elements within a sheet in document order.
            foreach (var type in p.Blocks.OrderBy(b => b.Sheet).ThenBy(b => b.DocIndex).Select(b => b.TypeName).Distinct())
            {
                int count = snapCopies * p.Blocks.Count(b => b.TypeName == type);
                if (!Lib.TryGetValue(type, out var m))
                {
                    // A project POU (function block defined by this project): write its catalogue
                    // identity and interface so instances in other POUs resolve.
                    if (projectTypes != null && projectTypes.TryGetValue(type, out var pt))
                    {
                        // Project function blocks are written exactly the way TriStation's own
                        // importer writes them: the catalogue DogTag, interface subType 0, the
                        // element's ExtOptionFlags and a live eventConnectionsChanged record.
                        // (Library elements keep their subtype and event from the platform.)
                        sb.Append($"<Class name=\"{Esc(type)}\" dogTag=\"{pt.DogTag}\" type=\"{pt.ElemType}\" subType=\"0\"")
                          .Append($" instanceCount=\"{count}\" commentCount=\"0\" extOptFlags=\"{pt.ExtOptFlags}\"")
                          .Append($" cemEventInputs=\"{pt.CemEI}\" cemActionOutputs=\"{pt.CemAO}\">")
                          .Append($"<Class Embed=\"eventConnectionsChanged\" eventType=\"0\" eventId=\"{pt.EventId}\" date=\"{Esc(pt.Date)}\" user=\"{pt.User}\"/>")
                          .Append("<Collection Name=\"declarations\" ClassType=\"CTS2Vars\" CollectionType=\"1\">");
                        foreach (var pin in pt.Pins)
                            sb.Append($"<Class name=\"{Esc(pin.Name)}\" dataType=\"{Esc(pin.DataType)}\" initVal=\"{Esc(pin.InitVal)}\" desc=\"{Esc(pin.Desc)}\"")
                              .Append($" varVTType=\"{pin.Vt}\" refCount=\"{pin.RefCount}\" fbRefCount=\"{pin.FbRefCount}\" flags=\"{pin.Flags}\" writes=\"0\"/>");
                        sb.Append("</Collection></Class>");
                        continue;
                    }
                    sb.Append($"<Class name=\"{Esc(type)}\" dogTag=\"0\" type=\"8\" subType=\"1\" instanceCount=\"{count}\"")
                      .Append(" commentCount=\"0\" extOptFlags=\"0\" cemEventInputs=\"0\" cemActionOutputs=\"0\">")
                      .Append("<Class Embed=\"eventConnectionsChanged\" eventType=\"0\" eventId=\"0\" date=\"\" user=\"2\"/>")
                      .Append("<Collection Name=\"declarations\" ClassType=\"CTS2Vars\" CollectionType=\"1\"/></Class>");
                    continue;
                }
                sb.Append($"<Class name=\"{Esc(type)}\" dogTag=\"{m.DogTag}\" type=\"{m.Type}\" subType=\"{m.SubType}\"")
                  .Append($" instanceCount=\"{count}\" commentCount=\"0\" extOptFlags=\"{m.ExtOptFlags}\"")
                  .Append($" cemEventInputs=\"{m.CemEI}\" cemActionOutputs=\"{m.CemAO}\">")
                  .Append($"<Class Embed=\"eventConnectionsChanged\" eventType=\"0\" eventId=\"{m.EventId}\" date=\"{m.Date}\" user=\"{m.User}\"/>")
                  .Append("<Collection Name=\"declarations\" ClassType=\"CTS2Vars\" CollectionType=\"1\">");
                foreach (var pin in m.Pins)
                    sb.Append($"<Class name=\"{Esc(pin.Name)}\" dataType=\"{Esc(pin.DataType)}\" initVal=\"{Esc(pin.InitVal)}\" desc=\"{Esc(pin.Desc)}\"")
                      .Append($" varVTType=\"{pin.Vt}\" refCount=\"{pin.RefCount}\" fbRefCount=\"0\" flags=\"{pin.Flags}\" writes=\"0\"/>");
                sb.Append("</Collection></Class>");
            }
            sb.Append("</Collection>");
        }

        // sheets. TriStation stores every logic sheet twice: a "primary" copy using the
        // authored localIds and a "secondary" copy offset by the primary span. A POU with a
        // FACESHEET additionally relocates the secondary copy of its first logic comment on
        // to the FACESHEET (that is the faceplate title), so the FACESHEET displayables hold
        // exactly that one comment.
        int span = plan.PrimarySpan;
        var sheets = p.Sheets.OrderBy(s => s.Number).ToList();

        sb.Append("<Collection Name=\"sheets\" ClassType=\"CFBDSheetXml\" CollectionType=\"1\">");
        foreach (var sheet in sheets)
        {
            bool isFace = sheet.Title.Equals("FACESHEET", StringComparison.OrdinalIgnoreCase);
            // TriStation's XML import writes firstBuildNetwork/lastBuildNetwork = 0 for every
            // sheet (verified against the reference import). Non-zero values here make the
            // editor treat the netlist as stale.
            sb.Append(SheetHeader(0, sheet.Title));
            var only = new HashSet<int> { sheet.Number };
            // The FACESHEET holds each of its comments twice, the copy (id+span) first and
            // then the authored object; a logic sheet holds its objects twice with the
            // authored (primary) copy first and the shifted (secondary) copy after.
            if (duplicated && isFace)
            {
                foreach (var item in SheetItems(plan, only, span, null)) sb.Append(item);
                foreach (var item in SheetItems(plan, only, 0, null)) sb.Append(item);
            }
            else
            {
                foreach (var item in SheetItems(plan, only, 0, null)) sb.Append(item);
                if (duplicated)
                    foreach (var item in SheetItems(plan, only, span, null)) sb.Append(item);
            }
            sb.Append("</Collection></Class>");
        }
        // Blank trailing sheets the editor appends/preserves. The count is not derivable
        // from the PLCopen export (empty sheets carry no content), so when a template
        // stream is available we keep however many blanks it had beyond the XML's sheets.
        int trailingEmpty = templateSheetCount >= 0 ? Math.Max(0, templateSheetCount - sheets.Count) : 1;
        for (int i = 0; i < trailingEmpty; i++)
        {
            sb.Append("<Class sheetTitle=\"\" firstBuildNetwork=\"0\" lastBuildNetwork=\"0\"")
              .Append(" S0=\"\" S1=\"\" S2=\"\" S3=\"\" S4=\"\" S5=\"\" S6=\"\" S7=\"\" S8=\"\" S9=\"\">")
              .Append("<Collection Name=\"displayables\" CollectionType=\"1\"/></Class>");
        }
        sb.Append("</Collection>");

        sb.Append("</SerializableClass>\r\n");
        string text = sb.ToString();
        ValidateFbdReferences(p, text);
        return text;
    }

    /// <summary>Sanity check for arbitrary (custom) FBDs: every <c>connectId</c>/<c>inConnectId</c>/
    /// <c>outConnectId</c> must resolve to an element or wire written in the same document.
    /// A dangling reference makes TriStation report the element as unconnected, so surface it
    /// during conversion instead of producing a silently broken project.</summary>
    private static void ValidateFbdReferences(ExPou p, string text)
    {
        var ids = new HashSet<string>();
        foreach (Match m in Regex.Matches(text, "localId=\"(\\d+)\"")) ids.Add(m.Groups[1].Value);
        var missing = new List<string>();
        foreach (Match m in Regex.Matches(text, "(?:connectId|inConnectId|outConnectId)=\"(\\d+)\""))
        {
            string id = m.Groups[1].Value;
            if (id != "0" && !ids.Contains(id)) missing.Add(id);
        }
        if (missing.Count > 0)
            Console.Error.WriteLine($"  warning: {p.Name}: {missing.Count} unresolved connection id(s): "
                                    + string.Join(",", missing.Distinct().Take(8)));
    }

    /// <summary>
    /// Emits a sheet's displayables in TriStation's serialization order: each element in
    /// document (authoring) order, immediately followed by the wire(s) anchored at it.
    /// A wire is anchored at its target when the target is a variable placement, otherwise
    /// at its source (so a block-to-block wire follows the feeding block).
    /// <paramref name="idOffset"/> shifts every localId/connectId for the second copy.
    /// </summary>
    private static IEnumerable<string> SheetItems(Plan plan, IReadOnlySet<int> sheets, int idOffset, IReadOnlySet<ExElem>? skip)
    {
        var p = plan.P;

        int AnchorId(Wire w)
        {
            var anchor = w.Target is ExVarPlace ? w.Target : w.Source;
            return ElemIdRaw(plan, anchor);
        }

        var wires = plan.Wires.Where(w => !w.Suppress && sheets.Contains(w.Target.Sheet)).ToList();
        var byAnchor = new Dictionary<int, List<Wire>>();
        foreach (var w in wires)
        {
            int a = AnchorId(w);
            if (!byAnchor.TryGetValue(a, out var lst)) { lst = new List<Wire>(); byAnchor[a] = lst; }
            lst.Add(w);
        }

        var results = new List<string>();
        // Document order over element kinds, interleaving dividers and wires.
        var elems = p.ElemOrder
            .Where(e => sheets.Contains(e.Sheet) && !e.Clipped && (skip == null || !skip.Contains(e)))
            .OrderBy(e => e.DocIndex).ToList();
        var divs = plan.DividerIds.Where(kv => sheets.Contains(kv.Key.Sheet)).OrderBy(kv => kv.Key.DocIndex).ToList();
        // TriStation writes every comment of a sheet first, in export (document) order -
        // both frame comments and free notes - then the dividers and the logic items.
        foreach (var cc in elems.OfType<ExComment>())
        {
            int id = ElemIdRaw(plan, cc);
            string? xml = EmitComment(cc, id + idOffset);
            if (xml != null) results.Add(xml);
            if (byAnchor.TryGetValue(id, out var aw))
                foreach (var w in aw.OrderBy(x => x.Id))
                    results.Add(EmitWire(plan, w, idOffset));
        }
        // Dividers exist once per sheet: the duplicated diagram copy does not repeat them.
        if (idOffset == 0)
            foreach (var dv in divs)
                results.Add(EmitDivider(plan, dv.Key, dv.Value + idOffset));
        // Emit the logic items in document order.
        foreach (var elem in elems)
        {
            if (elem is ExComment) continue;
            int id = ElemIdRaw(plan, elem);
            string? xml = elem switch
            {
                ExVarPlace v => EmitVar(plan, v, id + idOffset, idOffset),
                ExBlock b => EmitBlock(plan, b, id + idOffset, idOffset),
                _ => null,
            };
            if (xml != null) results.Add(xml);
            if (byAnchor.TryGetValue(id, out var aw))
                foreach (var w in aw.OrderBy(x => x.Id))
                    results.Add(EmitWire(plan, w, idOffset));
        }
        return results;
    }

    /// <summary>Element id (before any copy offset); matches the plan maps.</summary>
    private static int ElemIdRaw(Plan plan, ExElem e) => elementIdOf(plan, e);

    private static string SheetHeader(int buildNetwork, string title)
        => $"<Class sheetTitle=\"{Esc(title)}\" firstBuildNetwork=\"{buildNetwork}\""
         + $" lastBuildNetwork=\"{buildNetwork}\" S0=\"\" S1=\"\" S2=\"\" S3=\"\" S4=\"\""
         + " S5=\"\" S6=\"\" S7=\"\" S8=\"\" S9=\"\"><Collection Name=\"displayables\" CollectionType=\"1\">";

    private static string EmitDivider(Plan plan, ExDivider d, int id)
    {
        bool horiz = d.Type.Equals("HORIZONTAL", StringComparison.OrdinalIgnoreCase);
        // The divider spans the drawing area of the sheet template: 10 x the XML page
        // width (or height) plus the 18-unit right/bottom margin used by the editor.
        int sheetW = plan.P.SheetWidth > 0 ? 10 * plan.P.SheetWidth + 18 : 3238;
        int sheetH = plan.P.SheetHeight > 0 ? 10 * plan.P.SheetHeight + 18 : 2038;
        int posX = horiz ? 31 : Tx(d.X);
        int posY = horiz ? Tx(d.Y) : 31;
        int width = horiz ? sheetW : 1;
        int height = horiz ? 1 : sheetH;
        return $"<Class ClassType=\"CDGMnetworkDividerXml\" orientation=\"{Esc(d.Type)}\" localId=\"{id}\""
             + $" posX=\"{posX}\" posY=\"{posY}\" width=\"{width}\" height=\"{height}\"/>";
    }

    private static string EmitComment(ExComment c, int id)
    {
        string text = c.Text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        return $"<Class ClassType=\"CDGMCommentXml\" localId=\"{id}\" posX=\"{Tx(c.X)}\" posY=\"{Tx(c.Y)}\""
             + $" width=\"{10 * c.W}\" height=\"{10 * c.H}\" textAlignment=\"{Esc(c.Align)}\" border=\"{Esc(c.Border)}\""
             + $" textSize=\"{Esc(c.Size)}\" expandMacros=\"{Title(c.Expand)}\" macroEditing=\"{Title(c.Macro)}\""
             + $" behindLogic=\"{Title(c.Behind)}\"><CData Name=\"text\">{CData(text)}</CData></Class>";
    }

    private static string Title(string s)
        => s.Equals("TRUE", StringComparison.OrdinalIgnoreCase) ? "True"
         : s.Equals("FALSE", StringComparison.OrdinalIgnoreCase) ? "False" : s;

    private static string? EmitVar(Plan plan, ExVarPlace v, int id, int idOffset = 0)
    {
        var pp = plan.Places[v];
        // TriStation stores literal placements without a network attribute (the compiler
        // resolves them from the direct pin link); forcing one makes it look for a net that
        // does not exist and reports the literal as unconnected.
        string netAttr = "";
        bool wired = pp.OutWire != null && !pp.OutWire.Suppress;
        if (v.IsConstant && v.Kind != "in" && !wired) return null;
        if (v.IsConstant && v.Kind == "in")
        {
            var ow = pp.OutWire;
            string outConn; int outId;
            if (ow != null && ow.Suppress) { outConn = ow.TargetPin; outId = elementIdOf(plan, ow.Target) + idOffset; }
            else if (ow != null) { outConn = "IN1"; outId = ow.Id + idOffset; }
            else { outConn = ""; outId = 0; }
            string dt = v.DataType.Length > 0 ? v.DataType : (v.Expression.Contains('.') ? "REAL" : "DINT");
            bool annotate = v.Annotate.Equals("true", StringComparison.OrdinalIgnoreCase);
            int annHeight = annotate ? Math.Max(1, v.H - 2) : 4;
            return $"<Class ClassType=\"CFBDOutVarXml\" varName=\"{Esc(v.Expression)}\" varType=\"CONSTANT\""
                 + $" varDataType=\"{Esc(dt)}\" localId=\"{id}\" posX=\"{Tx(v.X)}\" posY=\"{Tx(v.Y)}\""
                 + $" width=\"{10 * v.W}\" height=\"{10 * v.H}\"{netAttr} outConnection=\"{Esc(outConn)}\""
                 + $" outConnectId=\"{outId}\" annotation=\"{Esc(v.Annotation)}\" annotate=\"{Title(v.Annotate)}\""
                 + $" annotationHeight=\"{annHeight}\" annotateMonitorValue=\"{Title(v.MonitorValue)}\"/>";
        }
        string cls = v.Kind == "in" ? "CFBDOutVarXml" : "CFBDInOutVarXml";
        var sb = new StringBuilder();
        sb.Append($"<Class ClassType=\"{cls}\" varName=\"{Esc(v.Expression)}\" localId=\"{id}\"")
          .Append($" posX=\"{Tx(v.X)}\" posY=\"{Tx(v.Y)}\" width=\"{10 * v.W}\" height=\"{10 * v.H}\"{netAttr}");
        if (pp.OutWire != null)
        {
            if (pp.OutWire.Suppress || pp.OutWire.Drop)
                sb.Append($" outConnection=\"{Esc(pp.OutWire.TargetPin)}\" outConnectId=\"{elementIdOf(plan, pp.OutWire.Target) + idOffset}\"");
            else
                sb.Append($" outConnection=\"IN1\" outConnectId=\"{pp.OutWire.Id + idOffset}\"");
        }
        sb.Append($" annotation=\"{Esc(v.Annotation)}\" annotate=\"{Title(v.Annotate)}\"")
          .Append($" annotationHeight=\"{(Title(v.Annotate) == "True" ? v.H - 2 : 4)}\" annotateMonitorValue=\"{Title(v.MonitorValue)}\"");
        if (pp.InWire != null && !pp.InWire.Suppress && !pp.InWire.Drop)
            sb.Append($" inConnection=\"{(pp.InWire.IsBranch ? "IN1" : "OUT1")}\" inConnectId=\"{pp.InWire.Id + idOffset}\"");
        else if (v.DirectInSource != null)
            sb.Append($" inConnection=\"{Esc(v.DirectInPin)}\" inConnectId=\"{elementIdOf(plan, v.DirectInSource) + idOffset}\"");
        else if (pp.InWire != null)
            // Suppressed wire (a literal link TriStation keeps as a direct pin link): point the
            // variable at the source element instead of a wire id that is not written.
            sb.Append($" inConnection=\"{Esc(pp.InWire.SourcePin)}\" inConnectId=\"{elementIdOf(plan, pp.InWire.Source) + idOffset}\"");
        sb.Append("/>");
        return sb.ToString();
    }

    /// <summary>
    /// The port name TriStation prints: the platform library's pin name for that position
    /// when the type is known (the XML's formalParameter can be a stale alias, e.g. "ET"
    /// for SEL or "CO" for PACK16), otherwise the export's name.
    /// </summary>
    private static string PortName(ExBlock b, bool input, int index, string fallback)
    {
        if (Lib.TryGetValue(b.TypeName, out var meta))
        {
            var pins = meta.Pins.Where(p => (input ? p.Vt == 0 : p.Vt == 1)).ToList();
            if (index >= 0 && index < pins.Count && pins[index].Name.Length > 0) return pins[index].Name;
        }
        return fallback;
    }

    private static string EmitBlock(Plan plan, ExBlock b, int id, int idOffset = 0)
    {
        var bp = plan.Blocks.First(x => x.B == b);
        bool isFn = b.IsFunction;
        var sb = new StringBuilder();
        sb.Append("<Class ClassType=\"").Append(isFn ? "CFBDFunctionXml" : "CFBDFBlockXml").Append('"');
        if (!isFn) sb.Append($" instanceName=\"{Esc(b.InstanceName)}\"");
        // TriStation sizes the box from its input pins: height = lowest pin row + one pin
        // pitch. The export usually already carries that height, but multi-input logic blocks
        // can carry the two-input default (e.g. a 7-input AND exported as height 8) and the
        // importer recomputes it from the pin rows.
        int height = 10 * b.H;
        var rels = b.Inputs.Select(p => p.RelY)
            .Concat(b.Outputs.Select(p => p.RelY))
            .Where(y => y > 0).Distinct().OrderBy(y => y).ToList();
        if (rels.Count >= 2)
        {
            int pitch = rels[^1] - rels[^2];
            if (pitch > 0) height = (rels[^1] + pitch) * 10;
        }
        sb.Append($" typeName=\"{Esc(b.TypeName)}\" localId=\"{id}\"")
          .Append($" posX=\"{Tx(b.X)}\" posY=\"{Tx(b.Y)}\" width=\"{10 * b.W}\" height=\"{height}\"");
        // Line mapping (network/firstLineNumber/lineCount, and the per-pin lineOffset) exists only
        // where TriStation keeps a compiler listing, i.e. for function blocks. A program has no
        // listing, and writing these attributes there makes the compiler take an unsupported path.
        bool withLayout = !plan.P.PouType.Equals("program", StringComparison.OrdinalIgnoreCase);
        if (withLayout)
        {
            sb.Append($" network=\"{b.Sheet}\"")
              .Append(bp.FirstLine > 0 ? $" firstLineNumber=\"{bp.FirstLine}\"" : "")
              .Append(bp.LineCount > 0 ? $" lineCount=\"{bp.LineCount}\"" : "");
        }
        // Build network ids on blocks are editor edit-history state (present on some blocks
        // and absent on others), so they are not emitted; the execute sequence is always 0.
        // The snapshot event ties this instance to the referenced element's interface version:
        // the platform library's event for library types, the project element's own
        // eventConnectionsChanged id for a function block defined by this project. Emitting 0
        // for those leaves the instance unresolved (red cross in the editor).
        int snapEvent = 0;
        // A project-defined function block must be tied to its own catalogue event, not to the
        // template's stale snapshot identity (the template loader also registers project FBs in
        // Lib, which would otherwise win here and leave the instance with a red cross).
        if (plan.ProjectTypes != null && plan.ProjectTypes.TryGetValue(b.TypeName, out var pt)) snapEvent = pt.EventId;
        else if (Lib.TryGetValue(b.TypeName, out var m)) snapEvent = m.EventId;
        sb.Append($" snapEventId=\"{snapEvent}\"")
          .Append($" enEnO=\"False\" doubleSpace=\"{(b.DoubleSpace ? "True" : "False")}\"")
          .Append(" sequence=\"0\">");
        sb.Append("<Collection Name=\"inputs\" ClassType=\"CDGMInputXml\" CollectionType=\"1\">");
        for (int i = 0; i < b.Inputs.Count; i++)
        {
            var w = bp.InWires[i];
            string pinName = PortName(b, true, i, b.Inputs[i].Name);
            string inv = b.Inputs[i].Negated ? " invertedPin=\"True\"" : "";
            // lineOffset is the pin's row inside the block; the network compiler uses it to
            // place the pin when the feeding element is a literal.
            string lineOffset = withLayout && b.Inputs[i].RelY > 0 ? $" lineOffset=\"{b.Inputs[i].RelY}\"" : "";
            if (w == null || w.Drop)
            {
                // Unconnected (or dropped) pins are still listed, with no connection attrs.
                sb.Append($"<Class{inv} formalParam=\"{Esc(pinName)}\"{lineOffset}/>");
                continue;
            }
            if (w.Suppress)
            {
                var sv = (ExVarPlace)w.Source;
                sb.Append($"<Class{inv} formalParam=\"{Esc(pinName)}\" effectiveParam=\"{Esc(sv.Expression)}\"")
                  .Append($" connectId=\"{elementIdOf(plan, w.Source) + idOffset}\"{lineOffset}/>");
                continue;
            }
            sb.Append($"<Class{inv} formalParam=\"{Esc(pinName)}\" effectiveParam=\"{(w.IsBranch || (ReferenceEquals(w.Source, b) && w.SourcePin == b.Inputs[i].Name) ? "IN1" : "OUT1")}\"")
              .Append($" connectId=\"{w.Id + idOffset}\"{lineOffset}/>");
        }
        sb.Append("</Collection>");
        sb.Append("<Collection Name=\"outputs\" ClassType=\"CDGMOutputXml\" CollectionType=\"1\">");
        for (int oi = 0; oi < b.Outputs.Count; oi++)
        {
            var pin = b.Outputs[oi];
            string pinName = PortName(b, false, oi, pin.Name);
            bp.OutWires.TryGetValue(pin.Name, out var w);
            string inv = pin.Negated ? " invertedPin=\"True\"" : "";
            if (w == null)
            {
                // A degenerate (zero-length) link lives only as a direct pin link: the original
                // project stores the sink's variable name + element id on the feeding pin.
                var sink = plan.Places.FirstOrDefault(kv => kv.Key is ExVarPlace ev && !ev.IsConstant
                    && ReferenceEquals(ev.DirectInSource, b) && ev.DirectInPin == pinName);
                if (sink.Key is ExVarPlace sv)
                    sb.Append($"<Class{inv} formalParam=\"{Esc(pinName)}\" effectiveParam=\"{Esc(sv.Expression)}\" connectId=\"{ElemIdRaw(plan, sv) + idOffset}\"/>");
                else
                    sb.Append($"<Class{inv} formalParam=\"{Esc(pinName)}\"/>");
            }
            else if (w.Suppress || w.Drop)
                // Direct pin link (degenerate/suppressed): the editor stores the target on the
                // pin itself (effectiveParam = target pin/name, connectId = target element).
                // Emitting a wire id here would dangle because no wire is written.
                sb.Append($"<Class{inv} formalParam=\"{Esc(pinName)}\" effectiveParam=\"{Esc(w.TargetPin)}\" connectId=\"{elementIdOf(plan, w.Target) + idOffset}\"/>");
            else
                sb.Append($"<Class{inv} formalParam=\"{Esc(pinName)}\" effectiveParam=\"IN1\" connectId=\"{w.Id + idOffset}\"/>");
        }
        sb.Append("</Collection></Class>");
        return sb.ToString();
    }

    private static string EmitWire(Plan plan, Wire w, int idOffset = 0)
    {
        // A branch arm leaves the same source pin as its trunk; its "output" points at the
        // junction on the parent wire (connectId = parent id, effectiveParam = V<n>).
        string inputEffective = w.SourcePin;
        int inputConnect = elementIdOf(plan, w.Source) + idOffset;
        string outputEffective = w.IsBranch ? "V" + w.Vn : w.TargetPin;
        int outputConnect = (w.IsBranch ? w.Parent!.Id : elementIdOf(plan, w.Target)) + idOffset;
        var sb = new StringBuilder();
        int wireSheet = 0;
        // TriStation orders the blocks of a network left-to-right and rejects a wire whose
        // source executes after its target ("illegal cycle", WG0010) unless it is marked as a
        // feedback. The XML's per-connection flag is unreliable (lossy exports carry false on
        // every edge), so derive it from geometry: a wire running backwards in x is feedback.
        bool feedback = w.FeedBack || (!w.IsBranch && w.Source.X > w.Target.X);
        sb.Append($"<Class ClassType=\"CDGMWireXml\" localId=\"{w.Id + idOffset}\" feedBack=\"{(feedback ? "True" : "False")}\" network=\"{wireSheet}\">");
        sb.Append($"<Class Embed=\"input\" formalParam=\"IN1\" effectiveParam=\"{Esc(inputEffective)}\" connectId=\"{inputConnect}\"/>");
        sb.Append($"<Class Embed=\"output\" formalParam=\"OUT1\" effectiveParam=\"{Esc(outputEffective)}\" connectId=\"{outputConnect}\"/>");
        sb.Append("<Collection Name=\"positions\" ClassType=\"CDGMPositionXml\" CollectionType=\"1\">");
        foreach (var (x, y) in w.PathExport)
            sb.Append($"<Class posX=\"{Tx(x)}\" posY=\"{Tx(y)}\"/>");
        sb.Append("</Collection></Class>");
        return sb.ToString();
    }

    private static int ElemId(Plan plan, ExElem e) => elementIdOf(plan, e);

    private static int elementIdOf(Plan plan, ExElem e) => e switch
    {
        ExVarPlace v when plan.Places.TryGetValue(v, out var pp) => pp.Id,
        ExBlock b => plan.Blocks.First(x => x.B == b).Id,
        ExComment c => _commentIds[c],
        _ => 0,
    };

    // ============================================================ CLI

    public static int Run(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: genfbd <xml> <pouName> <out.FBD>");
            return 1;
        }
        string xml = args[1], pouName = args[2], outPath = args[3];
        string text = Generate(xml, pouName);
        File.WriteAllText(outPath, text, new UTF8Encoding(false));
        Console.WriteLine($"Generated {outPath} ({text.Length} chars) from '{pouName}'");
        return 0;
    }
}
