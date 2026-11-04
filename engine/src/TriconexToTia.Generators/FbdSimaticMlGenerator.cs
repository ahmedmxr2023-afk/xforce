using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using TriconexToTia.Core.Enums;
using TriconexToTia.Core.Interfaces;
using TriconexToTia.Core.Models;
using TriconexToTia.Core.Results;

namespace TriconexToTia.Generators;

/// <summary>
/// Emits FBD-language blocks as TIA Portal SimaticML (<c>SW.Blocks.FB/FC</c> documents whose body is
/// an <c>FlgNet</c> network) instead of SCL, opt-in via <c>--fbd-output fbd|auto</c>.
/// </summary>
/// <remarks>
/// <para>
/// The document spine mirrors genuine TIA V16 exports (the <c>ladlabs</c> workspaces under
/// <c>goldens/v16</c>, the CON100 workspace export, and the LGF library): the
/// <c>Document</c>/<c>DocumentInfo</c> prologue, the block <c>AttributeList</c> with
/// <c>Interface/Sections</c>, per-network <c>SW.Blocks.CompileUnit</c> &gt; <c>FlgNet</c> with
/// <c>Parts</c> (Access/Part/Call) and <c>Wires</c> (NameCon/IdentCon/OpenCon), and the
/// <c>MultilingualText</c> comment/title objects.
/// </para>
/// <para>
/// The FBD box spelling is now proven by live TIA V16 import, not just copied from LAD exports:
/// Boolean gates are the single-letter <c>A</c>/<c>O</c> parts carrying only a <c>Card</c> template
/// value (the typed <c>And</c>/<c>Or</c> spelling instead requires an explicit <c>SrcType</c>), a
/// Boolean assignment is a single-sink <c>IdentCon</c> onto the target access (a <c>Coil</c> is
/// rejected once an <c>en</c>-bearing box shares the rung), a re-used operand needs its own
/// <c>Access</c> part per sink (a single <c>IdentCon</c> cannot fan out), and a stateful box output
/// (R_TRIG/timer/counter/instance) may only land on an assignment, never on a gate input. The
/// constants live in <see cref="FbdVocabulary"/>; each rule is noted at the site that depends on it.
/// A block that cannot be expressed this way falls back to SCL (the default path) and is reported
/// under "FBD FALLBACK" rather than dropped or guessed.
/// </para>
/// </remarks>
public sealed class FbdSimaticMlGenerator : IGenerator
{
    public string Id => "fbd";

    public string Description => "FBD blocks as SimaticML XML";

    public IEnumerable<GeneratedFile> Generate(GenerationContext context)
    {
        // Scl is the default mode: today's output, unchanged.
        if (context.Options.FbdOutput == FbdOutputMode.Scl)
        {
            yield break;
        }

        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var libraryFiles = new Dictionary<string, GeneratedFile>(StringComparer.OrdinalIgnoreCase);
        var wrapperOrdinal = 0;

        foreach (var block in context.Project.Blocks)
        {
            if (block.Language != PlcLanguage.FBD)
            {
                continue;
            }

            if (block.Logic is not StructuredLogic logic)
            {
                Fallback(context, block, "the FBD body is not a resolved structured network.");
                continue;
            }

            var rendered = FbdBlockRenderer.Render(block, logic, context);
            if (rendered.File is null)
            {
                Fallback(context, block, rendered.Fallback ?? "the FBD body cannot be expressed in SimaticML.");
                continue;
            }

            // In Auto mode a block whose FBD network needs an approximation (an inserted conversion
            // or a helper block) stays in SCL, where the approximation is at least text-verifiable.
            if (context.Options.FbdOutput == FbdOutputMode.Auto && rendered.Approximations.Count > 0)
            {
                Fallback(
                    context,
                    block,
                    "auto mode: the FBD rendering needs an approximated construct: "
                    + string.Join("; ", rendered.Approximations));
                continue;
            }

            // A block instance lives in a Static member, and TIA rejects a Static section in an OB
            // ("Section 'Static' is not valid for this block."). When an OB's diagram needs one (a
            // timer/trigger/instance box), the body moves into a generated wrapper function block and
            // the OB is reduced to a single call through the wrapper's instance DB. This mirrors the
            // SCL path (SclGenerator) so both modes name the artifacts identically.
            if (block.BlockType == BlockType.OB && rendered.InstanceDeclarations.Count > 0)
            {
                var wrapperName = block.Name + "_WRAP";
                var dbName = "DB_" + block.Name;
                var wrapper = FbdBlockRenderer.Render(
                    block,
                    logic,
                    context,
                    blockTypeOverride: BlockType.FB,
                    nameOverride: wrapperName,
                    numberOverride: 10000 + ++wrapperOrdinal);
                if (wrapper.File is null)
                {
                    Fallback(context, block, "wrapper function block: " + (wrapper.Fallback ?? "cannot be rendered."));
                    continue;
                }

                MarkRendered(context, block, rendered);
                foreach (var helper in wrapper.Helpers)
                {
                    libraryFiles.TryAdd(helper.RelativePath, helper);
                }

                var version = context.Options.ClampTiaVersion();
                yield return FbdSimaticMlGeneratorHelpers.Unique(wrapper.File, usedFiles);
                yield return FbdSimaticMlGeneratorHelpers.Unique(BuildObStub(block, context, wrapperName, dbName), usedFiles);
                yield return FbdSimaticMlGeneratorHelpers.Unique(
                    SclGenerator.RenderInstanceDbXml(dbName, wrapperName, version), usedFiles);
                continue;
            }

            MarkRendered(context, block, rendered);

            foreach (var helper in rendered.Helpers)
            {
                libraryFiles.TryAdd(helper.RelativePath, helper);
            }

            yield return FbdSimaticMlGeneratorHelpers.Unique(rendered.File, usedFiles);
        }

        foreach (var helper in libraryFiles.Values.OrderBy(file => file.RelativePath, StringComparer.Ordinal))
        {
            yield return helper;
        }
    }

    private static void MarkRendered(GenerationContext context, PlcBlock block, FbdBlockRenderResult rendered)
    {
        context.FbdOutcomes.Add(new FbdBlockOutcome
        {
            Block = block.Name,
            Rendered = "FBD",
        });
        foreach (var approach in rendered.Approximations)
        {
            context.FbdOutcomes[^1].Approximations.Add(approach);
        }
    }

    /// <summary>
    /// The organization block left behind once its body moved into a wrapper function block: nothing
    /// but a single, unconditionally enabled call through the wrapper's instance DB.
    /// </summary>
    private static GeneratedFile BuildObStub(PlcBlock block, GenerationContext context, string wrapperName, string dbName)
    {
        var network = new FbdNetwork();
        var call = network.AddCall();
        call.CallName = wrapperName;
        call.BlockType = "FB";
        call.IsInstance = true;
        call.InstanceComponent = dbName;
        call.InstanceScope = "GlobalVariable";
        call.InstanceUId = network.NextId();

        // A function-block call is rejected unless its en pin is connected ("The connection with the
        // name 'en' is not connected to the object ..."), and the power rail cannot drive it, so feed
        // it a Boolean TRUE literal as every other box call does.
        var enable = network.NewAccess();
        enable.Scope = "LiteralConstant";
        enable.ConstantType = "Bool";
        enable.ConstantValue = "true";
        network.Wire(
            new FbdNode { UId = enable.UId, IsAccess = true },
            new FbdNode { UId = call.UId, Pin = "en" });

        var result = new FbdBlockRenderResult { BlockComment = block.Comment };
        return FbdXmlWriter.Write(
            block,
            new List<FbdNetworkDocument> { new FbdNetworkDocument { Network = network } },
            result,
            context);
    }

    private static void Fallback(GenerationContext context, PlcBlock block, string reason)
    {
        context.FbdFallbackBlocks.Add(block.Name);
        context.FbdOutcomes.Add(new FbdBlockOutcome
        {
            Block = block.Name,
            Rendered = "SCL",
            Reason = reason,
        });
    }
}

/// <summary>File-name helpers used by the FBD generator.</summary>
internal static class FbdSimaticMlGeneratorHelpers
{
    public static GeneratedFile FileFor(string blockName) => new()
    {
        RelativePath = $"Blocks/{GenerationHelpers.SanitizeFileName(blockName)}.xml",
        Content = string.Empty,
        Kind = ArtifactKind.Block,
        EmitBom = true,
    };

    /// <summary>Gives a generated block file a name that is unique on disk, case-insensitively.</summary>
    public static GeneratedFile Unique(GeneratedFile file, HashSet<string> used)
    {
        var separator = file.RelativePath.LastIndexOf('/');
        var directory = file.RelativePath[..(separator + 1)];
        var fileName = file.RelativePath[(separator + 1)..];
        var stem = fileName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".xml".Length]
            : fileName;

        var candidate = stem;
        var suffix = 1;
        while (!used.Add(candidate))
        {
            candidate = stem + "_" + suffix.ToString(GenerationHelpers.Culture);
            suffix++;
        }

        if (candidate == stem) return file;

        return new GeneratedFile
        {
            RelativePath = directory + candidate + ".xml",
            Content = file.Content,
            Kind = file.Kind,
            EmitBom = file.EmitBom,
        };
    }
}

/// <summary>
/// How a TriStation function maps onto a TIA FBD box. Determined by live V16 import and genuine V16
/// exports: Boolean gates carry only a <c>Card</c>; comparison boxes carry only a <c>SrcType</c>;
/// arithmetic boxes carry a <c>Card</c>, an automatic <c>SrcType</c>, and a mandatory <c>en</c>
/// connection. <c>Not</c> and <c>Move</c> are spelled per <see cref="FbdBoxDialect"/>: Update 3 uses
/// the genuine <c>Not</c> (Card + SrcType Bool + en) and <c>Move</c> (Card + AutomaticTyped + en),
/// while V16.0 GA uses a negated single-input <c>A</c> gate for <c>Not</c> and a Card + <c>en</c>
/// <c>Move</c> whose Card is its output count. Any other function has no verified FBD form and forces
/// the block to fall back to SCL.
/// </summary>
internal enum FbdBoxKind
{
    Unsupported,
    Gate,
    Compare,
    Math,
    Negate,

    /// <summary>TriStation <c>LIMIT</c> becomes TIA <c>Limit</c>. Verified on V16.0 GA: the part needs an
    /// explicit <c>value_type</c> template and <c>DisabledENO="true"</c>; its pins are <c>MN</c>,
    /// <c>in</c>, <c>MX</c> and <c>out</c>.</summary>
    Limit,

    /// <summary>Single-operand math boxes that take a concrete <c>SrcType</c> template value rather
    /// than the arithmetic <c>Card</c>+<c>AutomaticTyped</c> pair (verified: <c>Sqrt</c>, <c>Abs</c>,
    /// <c>Sin</c>, <c>Cos</c>, <c>Tan</c>, <c>Exp</c>, <c>Ln</c>).</summary>
    Unary,

    /// <summary>TriStation <c>MIN</c>/<c>MAX</c> become TIA <c>Min</c>/<c>Max</c>. Verified on V16.0 GA:
    /// the part carries a lowercase <c>card</c> (operand count) and a <c>value_type</c> template, its
    /// pins are <c>in1</c>...<c>inN</c> and <c>out</c>, it has a mandatory <c>en</c>, and it requires
    /// <c>DisabledENO="false"</c> (probes PBMIN_K/L/M, PBMAX_K).</summary>
    MinMax,

    /// <summary>TriStation <c>POW</c> becomes TIA <c>Expt</c>. Verified on V16.0 GA: the part carries
    /// <c>SrcType</c> and <c>DestType</c> template values, pins <c>in1</c>/<c>in2</c> and <c>out</c>,
    /// with a mandatory <c>en</c> (probe PBPOW_D).</summary>
    Power,

    /// <summary>TriStation <c>SHL</c>/<c>SHR</c> become TIA <c>SHL</c>/<c>SHR</c>. Verified on V16.0 GA:
    /// the part carries only a <c>SrcType</c> (no <c>Card</c>), pins <c>in1</c>/<c>in2</c> and
    /// <c>out</c>, with a mandatory <c>en</c> (probes PBSHL_EN/PBSHR_EN).</summary>
    Shift,

    /// <summary>TriStation <c>SEL</c> becomes TIA <c>Sel</c>. Verified on V16.0 GA import: the part
    /// carries a <c>value_type</c> template value, its pins are <c>G</c>/<c>in0</c>/<c>in1</c> and
    /// <c>out</c>, and it has a mandatory <c>en</c> (probes MINSEL_G/H).</summary>
    Select,

    /// <summary>TriStation <c>MUX</c> becomes TIA <c>Mux</c>. Verified on V16.0 GA import: the part
    /// carries a <c>Card</c> (the number of data inputs, at least two) and a <c>SrcType</c> template
    /// value, its pins are <c>K</c>, <c>in0</c>...<c>in(n-1)</c>, <c>else</c> and <c>out</c>, and it
    /// has a mandatory <c>en</c> (probes MINMUX_L/N/Q).</summary>
    Mux,
}

/// <summary>
/// Every FBD-specific element name and pin name the renderer emits. The overall FlgNet spine
/// (Access/Part/Call/CallInfo/Wire/NameCon/IdentCon/OpenCon and the template-value attributes) is
/// copied from genuine TIA V16 exports; the Boolean-gate, assignment and fan-out shapes below are
/// additionally confirmed by live TIA V16 import.
/// </summary>
internal static class FbdVocabulary
{
    // Verified against genuine V16 exports (ladlabs + CON100 + LGF library + CON8_38 FB04_DI/FB05_FO)
    // and live V16 import: box parts, wire elements, template values and block spelling. Verified shapes:
    //   <Part Name="O" UId=".."><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue></Part>
    //   <Part Name="A" UId=".."><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue></Part>
    //     (single-letter Boolean gates; the typed And/Or spelling demands a SrcType template value)
    //   <Part Name="And" UId=".."><TemplateValue Name="Card" Type="Cardinality">17</TemplateValue><TemplateValue Name="SrcType" Type="Type">Bool</TemplateValue></Part>
    //   <Part Name="Not" UId=".."><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue><TemplateValue Name="SrcType" Type="Type">Bool</TemplateValue></Part>
    //   <Part Name="Move" UId=".."><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue><AutomaticTyped Name="SrcType" /></Part>
    //     (the two shapes above are the V16 Update 3 dialect; on V16.0 GA "Not" does not import at all
    //      and is emitted as <Part Name="A" ..><TemplateValue Name="Card" Type="Cardinality">1</...><Negated Name="in1"/></Part>,
    //      while "Move" drops AutomaticTyped and carries Card = output count)
    //   <Part Name="Mul" UId=".." DisabledENO="true"><TemplateValue Name="Card" Type="Cardinality">2</TemplateValue><AutomaticTyped Name="SrcType"/></Part>
    //   <Part Name="TON" Version="1.0" UId=".."><Instance Scope="GlobalVariable" UId=".."><Component Name=".."/></Instance><TemplateValue Name="time_type" Type="Type">Time</TemplateValue></Part>
    //   <Part Name="CTU" Version="1.0" UId=".."><Instance .../><TemplateValue Name="value_type" Type="Type">Int</TemplateValue></Part>
    //   <Part Name="Coil" UId=".."/> (pins in + operand; genuine assignment shape)
    //   <Part Name="R_TRIG" Version="1.0" UId=".."><Instance Scope="LocalVariable" UId=".."><Component Name=".."/></Instance></Part>
    //   <Access Scope="GlobalVariable" UId=".."><Symbol><Component Name=".."/>...</Symbol></Access>
    //   <Access Scope="LiteralConstant" UId=".."><Constant><ConstantType>DInt</ConstantType><ConstantValue>1000</ConstantValue></Constant></Access>
    //   <Access Scope="TypedConstant" UId=".."><Constant><ConstantValue>T#5000MS</ConstantValue></Constant></Access>
    //   <Wire UId=".."><IdentCon UId=".."/>... / <NameCon UId=".." Name="pin"/>... / <OpenCon UId=".."/></Wire>
    //     (Powerrail is a LAD rung source; live V16 import rejects it inside an FBD network)

    /// <summary>The TIA FBD box part name for a TriStation function. The Boolean gates map to the
    /// single-letter <c>A</c>/<c>O</c> parts and <c>NOT</c>/<c>MOVE</c> to the genuine <c>Not</c>/
    /// <c>Move</c> parts (all verified); the remaining spellings follow the LAD/box conventions and are
    /// unverified for FBD.</summary>
    public static string Box(string triName) => triName switch
    {
        "AND" => "A",
        "OR" => "O",
        "XOR" => "X",
        "NOT" => "Not",
        "MOVE" => "Move",
        "EQ" => "Eq",
        "NE" => "Ne",
        "GT" => "Gt",
        "GE" => "Ge",
        "LT" => "Lt",
        "LE" => "Le",
        "ADD" => "Add",
        "SUB" => "Sub",
        "MUL" => "Mul",
        "DIV" => "Div",
        "MOD" => "Mod",
        "SEL" => "Sel",
        "MUX" => "Mux",
        "LIMIT" => "Limit",
        "MIN" => "Min",
        "MAX" => "Max",
        "SQRT" => "Sqrt",
        "ABS" => "Abs",
        "SIN" => "Sin",
        "COS" => "Cos",
        "TAN" => "Tan",
        "EXP" => "Exp",
        "LN" => "Ln",
        "LOG" => "Log",
        "POW" => "Expt",
        "SHL" => "SHL",
        "SHR" => "SHR",
        "ROL" => "ROL",
        "ROR" => "ROR",
        "SEL_I" => "Sel",
        _ => triName,
    };

    /// <summary>The output pin name of a function box. Verified: A/O (V16 FBD) use <c>out</c>;
    /// other boxes assumed to match.</summary>
    public static string OutputPin(string triName) => "out";

    /// <summary>The input pin name for the i-th (1-based) operand of a function box. Verified: single
    /// operand boxes (<c>Not</c>, <c>Move</c>) use <c>in</c>, multi-operand boxes use <c>in1</c>/
    /// <c>in2</c>/... (genuine V16 exports).</summary>
    public static string InputPin(string triName, int index)
    {
        // Limit's operands are (MN, IN, MX): the lower bound, the value, the upper bound. Verified
        // against live V16.0 GA import (the pin names are MN/in/MX, not min/in/max).
        if (triName == "LIMIT")
        {
            return index switch
            {
                1 => "MN",
                2 => "in",
                3 => "MX",
                _ => "in" + index.ToString(GenerationHelpers.Culture),
            };
        }

        if (index == 1 && triName is "MOVE" or "NOT" or "ABS" or "SQRT" or "LN" or "EXP" or "LOG" or "SIN" or "COS" or "TAN")
        {
            return "in";
        }

        return "in" + index.ToString(GenerationHelpers.Culture);
    }

    /// <summary>
    /// Boolean gates that TIA exports as the single-letter, untyped box (<c>A</c>/<c>O</c>) with only
    /// a <c>Card</c> template value and no <c>SrcType</c>. Live V16 import: <c>A</c>/<c>O</c> with a
    /// <c>Card</c> only are accepted; the typed <c>And</c>/<c>Or</c> spelling instead demands an
    /// explicit <c>SrcType</c> ("The node 'TemplateValue' ... 'SrcType' ... is missing").
    /// </summary>
    public static bool IsBooleanGate(string triName) => triName is "AND" or "OR" or "XOR";

    /// <summary>
    /// The FBD box class of a TriStation function. Verified by live V16 import or genuine V16 exports:
    /// gates (<c>A</c>/<c>O</c>), comparisons, arithmetic, <c>Not</c>, <c>Move</c>, <c>Limit</c>,
    /// <c>Sqrt</c>, <c>Sel</c> and <c>Mux</c>. Every other function (including <c>XOR</c> and the
    /// remaining single-operand math functions) has no confirmed FBD form and must fall back to SCL.
    /// </summary>
    public static FbdBoxKind BoxKind(string triName) => triName switch
    {
        "AND" or "OR" or "XOR" => FbdBoxKind.Gate,
        "EQ" or "NE" or "GT" or "GE" or "LT" or "LE" => FbdBoxKind.Compare,
        "ADD" or "SUB" or "MUL" or "DIV" or "MOD" or "MOVE" => FbdBoxKind.Math,
        "LIMIT" => FbdBoxKind.Limit,
        "MIN" or "MAX" => FbdBoxKind.MinMax,
        "SQRT" or "ABS" or "SIN" or "COS" or "TAN" or "EXP" or "LN" => FbdBoxKind.Unary,
        "POW" => FbdBoxKind.Power,
        "SHL" or "SHR" => FbdBoxKind.Shift,
        "SEL" => FbdBoxKind.Select,
        "MUX" => FbdBoxKind.Mux,
        "NOT" => FbdBoxKind.Negate,
        _ => FbdBoxKind.Unsupported,
    };

    /// <summary>Boxes whose TIA element resolves SrcType automatically (arithmetic, shift, move).</summary>
    public static bool UsesAutomaticSrcType(string triName) => triName switch
    {
        "MOVE" or "ADD" or "SUB" or "MUL" or "DIV" or "MOD" or
        "SQRT" or "ABS" or "SIN" or "COS" or "TAN" or "EXP" or
        "LN" or "LOG" or "POW" or "SHL" or "SHR" or "ROL" or "ROR" => true,
        _ => false,
    };

    private static readonly Dictionary<string, string> IecNames = new(StringComparer.Ordinal)
    {
        ["BOOL"] = "Bool", ["BYTE"] = "Byte", ["WORD"] = "Word", ["DWORD"] = "DWord",
        ["SINT"] = "SInt", ["USINT"] = "USInt", ["INT"] = "Int", ["UINT"] = "UInt",
        ["DINT"] = "DInt", ["UDINT"] = "UDInt", ["REAL"] = "Real", ["LREAL"] = "LReal",
        ["TIME"] = "Time", ["CHAR"] = "Char",
    };

    /// <summary>Canonical IEC name for a type, or null for derived/unknown types.</summary>
    public static string? IecNameForType(string? type)
    {
        var token = ElementaryToken(type);
        if (token is null) return null;
        return IecNames.TryGetValue(token, out var name) ? name : token;
    }

    /// <summary>The TriStation functions that have a TIA FBD box equivalent.</summary>
    private static readonly HashSet<string> KnownBoxes = new(StringComparer.Ordinal)
    {
        "AND", "OR", "XOR", "NOT", "MOVE",
        "EQ", "NE", "GT", "GE", "LT", "LE",
        "ADD", "SUB", "MUL", "DIV", "MOD",
        "SEL", "MUX", "LIMIT", "MIN", "MAX",
        "SQRT", "ABS", "SIN", "COS", "TAN", "EXP", "LN", "LOG", "POW",
        "SHL", "SHR", "ROL", "ROR",
    };

    /// <summary>True when <paramref name="triName"/> renders as one of the box tables above.</summary>
    public static bool IsKnownBox(string triName) => KnownBoxes.Contains(triName);

    /// <summary>32-bit elementary IEC type tokens a CONV box can convert between.</summary>
    private static readonly HashSet<string> Elementary = new(StringComparer.Ordinal)
    {
        "BOOL", "BYTE", "WORD", "DWORD", "INT", "DINT", "SINT", "UINT", "UDINT", "USINT",
        "REAL", "LREAL", "TIME", "CHAR",
    };

    /// <summary>Normalizes a type name to an elementary IEC token, or null for derived types.</summary>
    public static string? ElementaryToken(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;

        var builder = new StringBuilder(type.Length);
        foreach (var character in type)
        {
            if (char.IsLetterOrDigit(character)) builder.Append(char.ToUpperInvariant(character));
        }

        var token = builder.ToString();
        return Elementary.Contains(token) ? token : null;
    }

    /// <summary>True when two primitives differ in a way that needs a conversion in the diagram.</summary>
    public static bool NeedsConversion(string? fromType, string? toType)
    {
        var from = ElementaryToken(fromType);
        var to = ElementaryToken(toType);
        return from is not null && to is not null && from != to;
    }

    /// <summary>
    /// True when the conversion between two elementary tokens must use the <c>CONVERT</c> instruction
    /// rather than a <c>Move</c>. Live V16.0 GA import (probes Q2/Q3 and the T_* matrix): a <c>Move</c>
    /// rejects <c>DInt</c> &lt;-&gt; <c>Real</c> ("Data type DInt cannot be implicitly converted to data
    /// type Real"), whereas <c>CONVERT</c> accepts every conversion that involves a <c>REAL</c>; in turn
    /// <c>CONVERT</c> rejects the same-width bit conversions (<c>DWord</c> -&gt; <c>DInt</c>,
    /// <c>Word</c> -&gt; <c>Int</c>) that a <c>Move</c> copies. A <c>REAL</c> operand therefore selects
    /// <c>CONVERT</c>; every other pair uses a <c>Move</c>.
    /// </summary>
    public static bool ConversionNeedsConvertBox(string? fromType, string? toType)
    {
        var from = ElementaryToken(fromType);
        var to = ElementaryToken(toType);
        return from is "REAL" or "LREAL" || to is "REAL" or "LREAL";
    }
}

/// <summary>Translates an inline-expansion call (PACK16/32, SCALE) into a generated helper FC.</summary>
internal static class FbdHelpers
{
    public static bool TryGet(string triName, out FbdHelper helper)
    {
        switch (triName)
        {
            case "PACK16":
                helper = new FbdHelper("PACK16", pack: true, width: 16);
                return true;
            case "PACK32":
                helper = new FbdHelper("PACK32", pack: true, width: 32);
                return true;
            case "SCALE":
                helper = new FbdHelper("SCALE", pack: false, width: 0);
                return true;
            default:
                helper = null!;
                return false;
        }
    }
}

/// <summary>A stand-in FC that reproduces a TriStation inline-expansion function as a box.</summary>
internal sealed class FbdHelper
{
    public FbdHelper(string triName, bool pack, int width)
    {
        TypeName = triName;
        SiemensName = SupportBlockGenerator.SupportPrefix + triName;
        Inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (pack)
        {
            for (var bit = 1; bit <= width; bit++)
            {
                Inputs["IN" + bit.ToString("D2", GenerationHelpers.Culture)] = "Bool";
            }
        }
        else
        {
            Inputs["INPUT"] = "Real";
            Inputs["MAX1"] = "Real";
            Inputs["MIN1"] = "Real";
            Inputs["MAX2"] = "Real";
            Inputs["MIN2"] = "Real";
        }

        OutputType = pack ? "DWord" : "Real";
    }

    /// <summary>The FBD box pin an FC's return value is exposed on.</summary>
    public const string OutputFormal = "RET_VAL";

    public string TypeName { get; }

    public string SiemensName { get; }

    /// <summary>Input formal names and their Siemens types.</summary>
    public Dictionary<string, string> Inputs { get; }

    public string OutputType { get; }

    public GeneratedFile Render()
    {
        var builder = new StringBuilder();
        // An SCL FUNCTION must declare its return type; without it TIA reports the attribute block as
        // "The specified value \"VERSION\" is invalid". The value is returned through the function name
        // (#<name>), which TIA exposes as the FBD box's RET_VAL pin (<see cref="OutputFormal"/>).
        builder.Append("FUNCTION \"").Append(SiemensName).Append("\" : ").AppendLine(OutputType);
        builder.AppendLine("{ S7_Optimized_Access := 'TRUE' }");
        builder.AppendLine("VERSION : 0.1");
        builder.AppendLine("VAR_INPUT");
        foreach (var (name, type) in Inputs)
        {
            builder.Append("    ").Append(name).Append(" : ").Append(type).AppendLine(";");
        }

        builder.AppendLine("END_VAR");
        builder.AppendLine("BEGIN");

        if (TypeName.StartsWith("PACK", StringComparison.Ordinal))
        {
            var width = TypeName == "PACK32" ? 32 : 16;
            var parts = new List<string>();
            for (var bit = 1; bit <= width; bit++)
            {
                var formal = "IN" + bit.ToString("D2", GenerationHelpers.Culture);
                if (bit == 1)
                {
                    parts.Add($"BOOL_TO_DWORD(#{formal})");
                }
                else
                {
                    parts.Add($"SHL(IN := BOOL_TO_DWORD(#{formal}), N := {(bit - 1).ToString(GenerationHelpers.Culture)})");
                }
            }

            builder.Append("    #").Append(SiemensName).Append(" := ")
                .Append(string.Join(" OR ", parts)).AppendLine(";");
        }
        else
        {
            builder.Append("    #").Append(SiemensName)
                .Append(" := ((#INPUT - #MIN1) / (#MAX1 - #MIN1)) * (#MAX2 - #MIN2) + #MIN2;")
                .AppendLine();
        }

        builder.AppendLine("END_FUNCTION");

        return new GeneratedFile
        {
            RelativePath = $"Library/{GenerationHelpers.SanitizeFileName(SiemensName)}.scl",
            Content = builder.ToString(),
            Kind = ArtifactKind.Block,
            EmitBom = true,
        };
    }
}

/// <summary>The outcome of rendering one FBD block to SimaticML.</summary>
internal sealed class FbdBlockRenderResult
{
    public GeneratedFile? File { get; set; }

    public string? Fallback { get; set; }

    public List<string> Approximations { get; } = new();

    public List<GeneratedFile> Helpers { get; } = new();

    /// <summary>Instance statics the networks reference, as (name, type) pairs.</summary>
    public List<(string Name, string Type)> InstanceDeclarations { get; } = new();

    /// <summary>Intermediate Temp members (name, IEC type) that carry a Call output into a box pin.</summary>
    public List<(string Name, string Type)> TempDeclarations { get; } = new();

    /// <summary>
    /// The block's own variables the networks actually read or write as <c>LocalVariable</c> accesses.
    /// Only these are declared in an OB/FC Temp section: an OB that had to be wrapped references none
    /// of its locals (its body is a single wrapper call), so declaring them would put an FB instance
    /// type in a Temp section, which TIA rejects.
    /// </summary>
    public HashSet<string> ReferencedLocals { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? BlockComment { get; set; }
}

/// <summary>A FlgNet part: an access, a function/instance box, or a block call.</summary>
internal sealed class FbdPart
{
    public string UId { get; set; } = string.Empty;

    public string Kind { get; set; } = "Part"; // Access | Part | Call

    // Access
    public string Scope { get; set; } = "GlobalVariable";
    public List<string> Components { get; } = new();
    public string? ConstantType { get; set; }
    public string? ConstantValue { get; set; }
    public bool IsTypedConstant { get; set; }

    // Part
    public string PartName { get; set; } = string.Empty;
    public bool IsInstance { get; set; }
    public string InstanceUId { get; set; } = string.Empty;
    public string InstanceComponent { get; set; } = string.Empty;
    public string InstanceScope { get; set; } = "LocalVariable";
    public List<FbdTemplateValue> TemplateValues { get; } = new();
    public bool AutomaticSrcType { get; set; }

    /// <summary>Emits <c>DisabledENO="..."</c> when set. Some boxes reject the default on V16.0 GA
    /// ("ENO cannot be deactivated for the '...' instruction"): <c>Limit</c> requires <c>"true"</c>,
    /// while <c>Min</c>/<c>Max</c> require <c>"false"</c>.</summary>
    public string? DisabledEno { get; set; }

    /// <summary>Input pins to declare as inverted (<c>&lt;Negated Name=".." /&gt;</c>). Used by the
    /// V16.0 GA NOT spelling (a negated single-input <c>A</c> gate).</summary>
    public List<string> NegatedPins { get; } = new();

    // Call
    public string CallName { get; set; } = string.Empty;
    public string BlockType { get; set; } = "FC";
    public List<FbdParameter> Parameters { get; } = new();
}

/// <summary>One <c>TemplateValue</c> child of a Part.</summary>
internal sealed class FbdTemplateValue
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = "Type";
    public string Value { get; set; } = string.Empty;
}

/// <summary>One <c>Parameter</c> child of a CallInfo.</summary>
internal sealed class FbdParameter
{
    public string Name { get; set; } = string.Empty;
    public string Section { get; set; } = "Input";
    public string? Type { get; set; }
}

/// <summary>A node a wire refers to: an access output (IdentCon), a part pin (NameCon), or an open.</summary>
internal sealed class FbdNode
{
    public string UId { get; set; } = string.Empty;
    public bool IsAccess { get; set; }
    public bool IsOpen { get; set; }
    public string? Pin { get; set; }

    /// <summary>
    /// True when this node is an output pin of an instance/stateful box (R_TRIG, timers, counters,
    /// an FB instance). Live V16 FBD import rejects such an output feeding a logic-gate pin
    /// ("The power rail (UId=..) contains an invalid connection to (UId=..) at pin 'Q'"); it is only
    /// accepted when it lands on an assignment access. The renderer flags this and falls back to SCL.
    /// </summary>
    public bool IsInstanceOutput { get; set; }

    /// <summary>
    /// True when the node is an output pin of a user <c>&lt;Call&gt;</c> (a helper FC or a user FB).
    /// Live V16 import rejects a Call output used directly as the source of a wire into another box
    /// pin ("The power rail ... contains an invalid connection ... at pin '..'") while a <c>&lt;Part&gt;</c>
    /// output into the same pin is accepted. The builder therefore routes every Call output that feeds
    /// a box pin through an intermediate Temp access (see <c>RouteCallOutputThroughTemp</c>).
    /// </summary>
    public bool IsCallOutput { get; set; }

    /// <summary>The output pin's declared type, used to declare the intermediate Temp access.</summary>
    public string? OutputType { get; set; }

    /// <summary>
    /// True when the node is the output of a terminal assignment instruction (<c>Move</c>). Live
    /// V16.0 GA import rejects a <c>Move</c> output used as the source of any box pin
    /// ("The power rail (UId=..) contains an invalid connection to (UId=..) at pin 'out1'"), so it may
    /// only ever terminate at an operand access. Consumers that would feed it to a box pin must fall
    /// back (or resolve the value another way).
    /// </summary>
    public bool IsTerminalOutput { get; set; }

    public string SourceKey => UId + "|" + (Pin ?? string.Empty);
}

/// <summary>One FlgNet wire: a single source followed by one or more sinks.</summary>
internal sealed class FbdWire
{
    public string UId { get; set; } = string.Empty;
    public FbdNode Source { get; set; } = null!;
    public List<FbdNode> Sinks { get; } = new();
}

/// <summary>
/// Builds one FlgNet body: parts get UIds as they are created. A wire carries one source and one or
/// more sinks; signal fan-out from an access is expressed by duplicating access parts (see
/// <see cref="NewAccess"/>). A box output that would feed several sinks sets <see cref="FanOut"/>,
/// which fails the block over to SCL (see the note there).
/// </summary>
internal sealed class FbdNetwork
{
    private readonly TiaXmlDocument.IdAllocator _ids = new(21);
    private readonly List<FbdPart> _parts = new();
    private readonly List<FbdWire> _wires = new();
    private readonly Dictionary<string, FbdWire> _wiresBySource = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FbdPart> _readAccesses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FbdPart> _writeAccesses = new(StringComparer.Ordinal);

    public IReadOnlyList<FbdPart> Parts => _parts;
    public IReadOnlyList<FbdWire> Wires => _wires;

    /// <summary>
    /// True when a single box-output source was wired to more than one sink. The live probes disagree
    /// with genuine exports here: the CON8_38 export FB04_DI carries one wire whose <c>And</c> output
    /// feeds <c>Or</c> and two <c>Coil</c> pins (a single wire with several sinks), yet the crafted probe
    /// <c>MT_boxfan_gates</c> (an <c>Add</c> output feeding Gt and Lt in one wire) was rejected with
    /// "The power rail (UId=..) contains an invalid connection to (UId=..) at pin 'out'". The safe,
    /// V16-verified behaviour is to fall back to SCL whenever a box output would fan out. Signal
    /// fan-out from an <em>access</em> is instead handled by duplicating access parts
    /// (see <see cref="NewAccess"/>), which is verified to import.
    /// </summary>
    public bool FanOut { get; private set; }

    public FbdPart AddPart() => Add(new FbdPart { Kind = "Part" });

    /// <summary>
    /// A fresh <c>Access</c> part per use. Live V16 import rejects a re-used access as a source:
    /// a single <c>IdentCon</c> cannot fan out to several sinks ("The power rail with UId '..' does
    /// not support IdentCon/OpenCon with UId '..'"), while duplicated accesses with identical
    /// symbols import fine. So every read/write of a signal gets its own part.
    /// </summary>
    public FbdPart NewAccess(int insertAt = -1)
    {
        var part = new FbdPart { Kind = "Access" };
        return Add(part, insertAt);
    }

    public FbdPart AddCall() => Add(new FbdPart { Kind = "Call" });

    /// <summary>Reserves an ID for a nested element of a part (the instance reference).</summary>
    public string NextId() => _ids.Next();

    /// <summary>An open wire end, for a part output nothing consumes.</summary>
    public FbdNode OpenCon() => new() { UId = _ids.Next(), IsOpen = true };

    public FbdPart AddAccess(string key, bool write)
    {
        var map = write ? _writeAccesses : _readAccesses;
        if (map.TryGetValue(key, out var existing)) return existing;

        var part = new FbdPart { Kind = "Access", UId = _ids.Next() };
        map[key] = part;
        _parts.Add(part);
        return part;
    }

    private FbdPart Add(FbdPart part, int insertAt = -1)
    {
        part.UId = _ids.Next();
        if (insertAt >= 0 && insertAt <= _parts.Count)
        {
            _parts.Insert(insertAt, part);
        }
        else
        {
            _parts.Add(part);
        }

        return part;
    }

    public void Wire(FbdNode source, FbdNode sink, int insertAt = -1)
    {
        if (_wiresBySource.TryGetValue(source.SourceKey, out var wire))
        {
            if (wire.Sinks.Count > 0)
            {
                FanOut = true;
            }

            wire.Sinks.Add(sink);
            return;
        }

        wire = new FbdWire { UId = _ids.Next(), Source = source };
        wire.Sinks.Add(sink);
        _wiresBySource[source.SourceKey] = wire;
        if (insertAt >= 0 && insertAt <= _wires.Count)
        {
            _wires.Insert(insertAt, wire);
        }
        else
        {
            _wires.Add(wire);
        }
    }

    /// <summary>True when the part with <paramref name="uid"/> is a user <c>&lt;Call&gt;</c>.</summary>
    public bool IsCallPart(string uid)
    {
        foreach (var part in _parts)
        {
            if (part.UId == uid) return part.Kind == "Call";
        }

        return false;
    }

    /// <summary>True when the part with <paramref name="uid"/> declares <paramref name="pin"/> as
    /// inverted (<c>&lt;Negated Name=".." /&gt;</c>).</summary>
    public bool IsNegatedPin(string uid, string? pin)
    {
        if (string.IsNullOrEmpty(pin)) return false;
        foreach (var part in _parts)
        {
            if (part.UId == uid) return part.NegatedPins.Contains(pin);
        }

        return false;
    }

    /// <summary>The current index of the part with <paramref name="uid"/>, or -1 when absent.</summary>
    public int PartIndexOf(string uid)
    {
        for (var i = 0; i < _parts.Count; i++)
        {
            if (_parts[i].UId == uid) return i;
        }

        return -1;
    }

    /// <summary>The index of the last wire touching the part with <paramref name="uid"/> as source or
    /// sink, or -1 when the part has no wire yet.</summary>
    public int LastWireIndexForPart(string uid)
    {
        for (var i = _wires.Count - 1; i >= 0; i--)
        {
            var wire = _wires[i];
            if (wire.Source.UId == uid) return i;
            foreach (var sink in wire.Sinks)
            {
                if (sink.UId == uid) return i;
            }
        }

        return -1;
    }

    /// <summary>True when <paramref name="node"/> is known to drive a wire.</summary>
    public bool HasWireFrom(FbdNode node) => _wiresBySource.ContainsKey(node.SourceKey);
}

/// <summary>Renders one FBD block into a SimaticML document (or reports why it cannot).</summary>
internal static class FbdBlockRenderer
{
    internal static readonly Regex IntegerLiteral = new(@"^[-+]?\d+$", RegexOptions.Compiled);
    internal static readonly Regex RealLiteral = new(@"^[-+]?(\d+\.\d*|\.\d+)([eE][-+]?\d+)?$", RegexOptions.Compiled);
    internal static readonly Regex TimedConstant = new(@"^T#", RegexOptions.Compiled);

    public static FbdBlockRenderResult Render(
        PlcBlock block,
        StructuredLogic logic,
        GenerationContext context,
        BlockType? blockTypeOverride = null,
        string? nameOverride = null,
        int? numberOverride = null)
    {
        var result = new FbdBlockRenderResult
        {
            BlockComment = block.Comment,
        };

        if (block.Variables.Any(variable => variable.IsArray))
        {
            return new FbdBlockRenderResult
            {
                Fallback = "array-typed interface members are not yet expressible in FBD SimaticML.",
            };
        }

        var knownBlockNames = context.Project.Blocks
            .Select(candidate => candidate.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var localNames = block.Variables
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only the address-free globals live in the GlobalData data block (see GlobalDbGenerator);
        // a point that carries a hardware address stays a PLC tag and must be referenced by its bare
        // name, so qualifying it with "GlobalData" would leave a dangling reference.
        var dbGlobals = GlobalDbGenerator.DatabaseGlobals(context.Project)
            .Select(variable => variable.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // The diagram records the type it expects on some items only; comparisons against bare
        // variables carry no type at all, so this name -> declared type map lets the renderer recover
        // the operand type (e.g. the SrcType of a GT box).
        var variableTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in block.Variables)
        {
            var type = variable.MappedTypeName ?? variable.TypeName;
            if (!string.IsNullOrWhiteSpace(type)) variableTypes[variable.Name] = type;
        }

        foreach (var global in context.Project.GlobalVariables)
        {
            var type = global.MappedTypeName ?? global.TypeName;
            if (!string.IsNullOrWhiteSpace(type) && !variableTypes.ContainsKey(global.Name))
            {
                variableTypes[global.Name] = type;
            }
        }

        var helpers = new Dictionary<string, GeneratedFile>(StringComparer.OrdinalIgnoreCase);
        var networks = new List<FbdNetworkDocument>();
        var state = new FbdBlockState();

        foreach (var sheetGroup in logic.Items.GroupBy(item => item.SheetNumber).OrderBy(group => group.Key))
        {
            var items = Order(sheetGroup.ToList(), out var cyclic);
            if (cyclic)
            {
                // A feedback cycle cannot be expressed as a SimaticML FlgNet (TIA rejects a cyclic
                // wiring graph), so keep the block in SCL where the loop is expressible.
                return new FbdBlockRenderResult
                {
                    Fallback = $"sheet {sheetGroup.Key}: the diagram contains a feedback cycle that FBD cannot express; render it as SCL.",
                };
            }

            var network = new FbdNetwork();
            var build = new FbdNetworkBuilder(
                network, items, localNames, dbGlobals, knownBlockNames, variableTypes,
                result, helpers, state, context.Options.FbdDialect);

            var fallback = build.Build();
            if (fallback is not null)
            {
                return new FbdBlockRenderResult { Fallback = $"sheet {sheetGroup.Key}: {fallback}" };
            }

            var title = sheetGroup.Key <= logic.SheetTitles.Count
                ? logic.SheetTitles[sheetGroup.Key - 1]
                : null;

            networks.Add(new FbdNetworkDocument
            {
                Network = network,
                Title = string.IsNullOrWhiteSpace(title) ? null : title,
                CommentText = build.Comment,
            });
        }

        // A TriStation program whose diagram holds no logic at all (only annotations) is still an FBD
        // block in TIA; keep its (comment-bearing, logic-free) networks so it opens in FBD rather than
        // as an empty SCL file.
        var logicFree = logic.Items.All(item => item is LogicComment);
        if (logicFree && networks.Count == 0)
        {
            networks.Add(new FbdNetworkDocument { Network = new FbdNetwork() });
            result.Approximations.Add("the TriStation diagram has no logic; emitted an empty FBD network");
        }

        result.File = FbdXmlWriter.Write(block, networks, result, context, blockTypeOverride, nameOverride, numberOverride);
        foreach (var helper in helpers.Values)
        {
            result.Helpers.Add(helper);
        }

        // An empty body is not evidence of a working FBD diagram; falling back keeps the SCL (or an
        // empty SCL) so no logic is silently dropped on disk.
        if (!logicFree && networks.All(network => network.Network.Parts.Count == 0 && network.Network.Wires.Count == 0))
        {
            return new FbdBlockRenderResult
            {
                Fallback = "the FBD body has no expressible network content.",
            };
        }

        return result;
    }

    /// <summary>Stable topological order: dependencies first, then sheet/row/column. <paramref name="cyclic"/>
    /// is set when the dependency graph has no acyclic order, which TIA FBD cannot express.</summary>
    private static List<LogicItem> Order(List<LogicItem> items, out bool cyclic)
    {
        cyclic = false;
        var remaining = items.ToDictionary(item => item.SheetKey, item => item, StringComparer.Ordinal);
        var done = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<LogicItem>(items.Count);

        while (remaining.Count > 0)
        {
            var ready = remaining.Values
                .Where(item => Dependencies(item).All(key => done.Contains(key) || !remaining.ContainsKey(key)))
                .OrderBy(item => item.Y)
                .ThenBy(item => item.X)
                .ThenBy(item => item.LocalId, StringComparer.Ordinal)
                .ToList();

            if (ready.Count == 0)
            {
                // Cycle: report it so the caller can fall back to SCL rather than emit a FlgNet that
                // TIA rejects.
                cyclic = true;
                ordered.AddRange(remaining.Values.OrderBy(item => item.Y).ThenBy(item => item.X)
                    .ThenBy(item => item.LocalId, StringComparer.Ordinal));
                break;
            }

            foreach (var item in ready)
            {
                ordered.Add(item);
                done.Add(item.SheetKey);
                remaining.Remove(item.SheetKey);
            }
        }

        return ordered;
    }

    private static IEnumerable<string> Dependencies(LogicItem item)
    {
        switch (item)
        {
            case VariableAssignment assignment when assignment.IsBlockOutput:
                yield return Key(assignment.RefSheetNumber, assignment.RefLocalId!);
                break;

            case BlockCall call:
                foreach (var argument in call.Inputs.Concat(call.InOuts))
                {
                    foreach (var source in argument.Sources.Where(source => source.IsBlockOutput))
                    {
                        yield return Key(source.RefSheetNumber, source.RefLocalId!);
                    }
                }

                break;
        }
    }

    internal static string Key(int sheet, string localId) => $"{sheet}:{localId}";
}

/// <summary>One network of an FBD block, ready to be written as a CompileUnit.</summary>
internal sealed class FbdNetworkDocument
{
    public FbdNetwork Network { get; init; } = null!;
    public string? Title { get; init; }
    public string? CommentText { get; set; }
}

/// <summary>
/// Turns one sheet's items into FlgNet parts and wires. Instance statics and helper files are
/// shared through a per-block state object so sheets do not duplicate them.
/// </summary>
internal sealed class FbdNetworkBuilder
{
    private readonly FbdNetwork _network;
    private readonly List<LogicItem> _items;
    private readonly HashSet<string> _localNames;
    private readonly HashSet<string> _dbGlobals;
    private readonly HashSet<string> _knownBlockNames;
    private readonly IReadOnlyDictionary<string, string> _variableTypes;
    private readonly FbdBlockRenderResult _result;
    private readonly Dictionary<string, GeneratedFile> _helpers;
    private readonly FbdBlockState _state;
    private readonly FbdBoxDialect _dialect;
    private readonly Dictionary<string, BlockCall> _callsBySheetKey = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FbdNode> _functionOutputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FbdPart> _functionParts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FbdNode> _instanceOutputs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FbdPart> _instanceParts = new(StringComparer.Ordinal);

    /// <summary>Temp variable name per Call output pin, so the pin is wired into the temp exactly once.</summary>
    private readonly Dictionary<string, string> _callOutputTemps = new(StringComparer.Ordinal);

    /// <summary>The Temp write access part per producer-output pin, so its reads can be inserted right
    /// after it and the whole access group stays ahead of the consumers (see
    /// <see cref="RouteCallOutputThroughTemp"/>).</summary>
    private readonly Dictionary<string, FbdPart> _callOutputTempWrites = new(StringComparer.Ordinal);

    /// <summary>
    /// Producer-output references (see <see cref="RefKey"/>) that are consumed more than once within
    /// this sheet. A box output feeding two sinks cannot be a single FBD wire, so it is materialized
    /// through a Temp (see <see cref="RouteCallOutputThroughTemp"/>) instead of falling back to SCL.
    /// </summary>
    private readonly HashSet<string> _fanOutRefs = new(StringComparer.Ordinal);

    /// <summary>
    /// A Triconex <c>MOVE</c> block is a value copy, not an assignment. TIA's <c>Move</c> instruction is
    /// terminal (its output may only land on an operand), so a Triconex MOVE whose result also feeds
    /// box pins cannot be a <c>&lt;Part Name="Move"&gt;</c>. Instead the block is dissolved: consumers of
    /// its output resolve the MOVE's own input, which is what the copy evaluates to.
    /// </summary>
    private readonly Dictionary<string, SignalSource> _moveCopySources = new(StringComparer.Ordinal);

    private static readonly HashSet<string> TimerTypes = new(StringComparer.OrdinalIgnoreCase) { "TON", "TOF", "TP" };
    private static readonly HashSet<string> CounterTypes = new(StringComparer.OrdinalIgnoreCase) { "CTU", "CTD", "CTUD" };

    public string? Comment { get; private set; }

    public FbdNetworkBuilder(
        FbdNetwork network,
        List<LogicItem> items,
        HashSet<string> localNames,
        HashSet<string> dbGlobals,
        HashSet<string> knownBlockNames,
        IReadOnlyDictionary<string, string> variableTypes,
        FbdBlockRenderResult result,
        Dictionary<string, GeneratedFile> helpers,
        FbdBlockState state,
        FbdBoxDialect dialect)
    {
        _network = network;
        _items = items;
        _localNames = localNames;
        _dbGlobals = dbGlobals;
        _knownBlockNames = knownBlockNames;
        _variableTypes = variableTypes;
        _result = result;
        _helpers = helpers;
        _state = state;
        _dialect = dialect;
    }

    public string? Build()
    {
        foreach (var call in _items.OfType<BlockCall>())
        {
            _callsBySheetKey[call.SheetKey] = call;
        }

        ComputeFanOut();

        foreach (var item in _items)
        {
            var error = Render(item);
            if (error is not null) return error;
        }

        if (_network.FanOut)
        {
            return "a value is consumed in more than one place (fan-out) which a single FBD wire cannot express; render it as SCL.";
        }

        foreach (var node in _instanceOutputs.Values)
        {
            if (!_network.HasWireFrom(node))
            {
                _network.Wire(node, _network.OpenCon());
            }
        }

        // A timer's elapsed-time output ET is mandatory: live V16 import rejects a TON/TOF/TP whose ET
        // pin is left unconnected ("The connection with the name 'ET' is not connected to the object
        // with the UID '..'"). The genuine V16 export terminates an unused ET with an <OpenCon/>, so
        // mirror that for every timer part whose ET no consumer resolved.
        foreach (var part in _instanceParts.Values)
        {
            if (!part.IsInstance || string.IsNullOrEmpty(part.PartName)) continue;
            if (!TimerTypes.Contains(part.PartName)) continue;

            var elapsed = new FbdNode { UId = part.UId, Pin = "ET", IsInstanceOutput = true };
            if (!_network.HasWireFrom(elapsed))
            {
                _network.Wire(elapsed, _network.OpenCon());
            }
        }

        return null;
    }

    /// <summary>Records every producer output consumed more than once in this sheet, so its consumers
    /// can be materialized through a Temp rather than tripping the single-wire fan-out guard.</summary>
    private void ComputeFanOut()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);

        void Bump(string refKey)
        {
            counts[refKey] = counts.TryGetValue(refKey, out var count) ? count + 1 : 1;
        }

        foreach (var item in _items)
        {
            switch (item)
            {
                case VariableAssignment assignment when assignment.IsBlockOutput:
                    Bump(RefKey(assignment.RefSheetNumber, assignment.RefLocalId!, assignment.RefFormalParameter));
                    break;

                case BlockCall call:
                    foreach (var argument in call.Inputs.Concat(call.InOuts))
                    {
                        foreach (var source in argument.Sources)
                        {
                            if (source.IsBlockOutput)
                            {
                                Bump(RefKey(source.RefSheetNumber, source.RefLocalId!, source.RefFormalParameter));
                            }
                        }
                    }

                    break;
            }
        }

        foreach (var pair in counts)
        {
            if (pair.Value > 1) _fanOutRefs.Add(pair.Key);
        }
    }

    /// <summary>A producer-output reference identity: the producing item plus the consumed formal pin.</summary>
    private static string RefKey(int sheet, string localId, string? formal) =>
        FbdBlockRenderer.Key(sheet, localId) + "\u0001" + (formal ?? string.Empty);

    private string? Render(LogicItem item)
    {
        switch (item)
        {
            case LogicComment comment:
                var body = comment.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(body) && !body.StartsWith('%'))
                {
                    Comment = string.IsNullOrEmpty(Comment) ? body : Comment + Environment.NewLine + body;
                }

                return null;

            case VariableReference:
                // The value is consumed through the call arguments' sources; a reference with no
                // consumer needs no part.
                return null;

            case VariableAssignment assignment:
                return RenderAssignment(assignment);

            case BlockCall call:
                return RenderCall(call);

            default:
                return null;
        }
    }

    private string? RenderAssignment(VariableAssignment assignment)
    {
        var target = AssignmentTarget(assignment.Target);
        if (target is null)
        {
            return $"assignment target \"{assignment.Target}\" is not a supported signal.";
        }

        // The target's declared type decides how the value is written: a Bool target is drawn as a
        // Coil (a Move rejects Bool), a mismatched type needs a conversion (see WriteAssignment).
        var targetType = ResolveTargetType(assignment.Target);
        var booleanTarget = IsBooleanType(targetType);

        if (assignment.IsBlockOutput)
        {
            var source = ResolveBlockOutput(
                assignment.RefSheetNumber, assignment.RefLocalId!, assignment.RefFormalParameter);
            if (source is null)
            {
                return $"the value of \"{assignment.Target}\" comes from an item outside this network.";
            }

            WriteAssignment(source, target, assignment.SourceTypeName, targetType, booleanTarget);
            return null;
        }

        var node = ExpressionAccess(assignment.Expression ?? string.Empty, write: false, assignment.SourceTypeName);
        if (node is null)
        {
            return $"assignment to \"{assignment.Target}\" has no usable source expression.";
        }

        WriteAssignment(node, target, assignment.SourceTypeName, targetType, booleanTarget);
        return null;
    }

    /// <summary>The declared IEC type of an assignment target, or null when it is unknown (a tag not
    /// present in the block or global variable tables).</summary>
    private string? ResolveTargetType(string target)
    {
        var name = (target ?? string.Empty).Trim().TrimStart('#').Trim().Trim('"');
        var dot = name.LastIndexOf('.');
        if (dot >= 0) name = name[(dot + 1)..];
        return name.Length == 0 || !_variableTypes.TryGetValue(name, out var declared)
            ? null
            : FbdVocabulary.IecNameForType(declared) ?? declared;
    }

    /// <summary>
    /// Writes a signal into an assignment target. A box/gate output is wired straight onto the target
    /// access (a single-sink <c>NameCon</c> → <c>IdentCon</c>), because feeding a box output into a
    /// <c>Move</c> box's <c>in</c> pin is rejected on V16.0 GA ("The power rail ... contains an invalid
    /// connection ... at pin 'in'"). An access source is written through a <c>Move</c> box
    /// (<c>in</c> → <c>out1</c>), because an access wired straight to another access is rejected
    /// ("No 'NameCon' defined for rung"). A source that is already a terminal instruction (another
    /// Move) is wired straight to the target instead, because chaining Move boxes is rejected.
    /// </summary>
    private void WriteAssignment(FbdNode source, FbdNode target, string? fromType, string? toType, bool booleanTarget)
    {
        // A Call/instance output can drive an assignment access directly, so no temp is inserted here:
        // a direct <NameCon> output -> <IdentCon> target wire is accepted by live V16 import (probe
        // QC_F: Call.O -> R, QC_G: R_TRIG.Q -> R). A box-pin consumer still needs the temp, but that
        // path is handled by the conversion box below (which funnels the source through a temp).

        // A terminal instruction (Move) output can only land on an operand, so wire it straight to the
        // target; adding another Move would create a Move -> Move chain, which live V16.0 GA rejects
        // (probes MV_CHAIN / MVG_CHAIN: "The power rail ... contains an invalid connection ... at pin
        // 'out1'").
        if (source.IsTerminalOutput)
        {
            _network.Wire(source, target);
            return;
        }

        // The wire itself often carries no type (a TriStation bit operand has no DATATYPE); fall back
        // to the producing box's output type.
        fromType ??= source.OutputType;

        // A mismatched target type needs an explicit conversion first (a Move rejects a Bool operand;
        // a Bool target needs a value this box produced). InsertConversion emits Ne/Sel/Move.
        if (FbdVocabulary.NeedsConversion(fromType, toType))
        {
            var converted = InsertConversion(source, fromType, toType);
            _network.Wire(converted, target);
            return;
        }

        // A box/gate output is assigned by wiring it straight onto the target access (a single-sink
        // NameCon -> IdentCon). Feeding a box output into a Move box's 'in' pin is rejected on V16.0 GA
        // ("The power rail (UId=..) contains an invalid connection to (UId=..) at pin 'in'", probe
        // MOVTST/MOVIN2). Only an access source still needs the Move box, because an access wired
        // straight to another access has no NameCon ("No 'NameCon' defined for rung").
        if (!source.IsAccess)
        {
            _network.Wire(source, target);
            return;
        }

        // A Bool operand cannot be written through a Move box ("Data type Bool is not permitted here",
        // probe probe-move/MV_BOOL). The genuine FBD boolean assignment is a Coil: the source feeds the
        // Coil 'in' pin and the target is the Coil 'operand'. Live V16 import+compile accept this for a
        // variable source, a Bool literal and alongside an en-bearing Move on the same network
        // (probes MV_COIL, MV_COIL_LIT, MV_COIL_MOVE).
        if (booleanTarget)
        {
            var coil = _network.AddPart();
            coil.PartName = "Coil";
            _network.Wire(source, new FbdNode { UId = coil.UId, Pin = "in" });
            _network.Wire(target, new FbdNode { UId = coil.UId, Pin = "operand" });
            return;
        }

        var move = _network.AddPart();
        move.PartName = "Move";
        move.TemplateValues.Add(new FbdTemplateValue { Name = "Card", Type = "Cardinality", Value = "1" });
        WireBooleanEnable(move.UId);
        _network.Wire(source, new FbdNode { UId = move.UId, Pin = "in" });
        _network.Wire(new FbdNode { UId = move.UId, Pin = "out1" }, target);
    }

    private FbdNode? AssignmentTarget(string expression)
    {
        var trimmed = expression.Trim();
        if (trimmed.Length == 0) return null;
        return ExpressionAccess(trimmed, write: true);
    }

    /// <summary>TIA auto-adds an <c>en</c> connection to FBD parts; it must be connected. A per-part
    /// TRUE literal feeds it, so every instance/box participates in the network.</summary>
    private void WireEnable(string typeName, string partUId, bool isInstance = false)
    {
        var tn = typeName?.Trim() ?? string.Empty;

        // The IEC timer boxes (TON/TOF/TP) have no en pin: live V16 import rejects a TON/TOF/TP that
        // carries an en connection with "Block 'TON' is not supported by the CPU or library version
        // you are using" (probe MINT_TON_NOEN imports once the en wire is dropped; the genuine V16
        // export of a timer has only IN/PT/Q/ET). Edge triggers keep their en pin, so only skip timers.
        if (TimerTypes.Contains(tn)) return;

        bool mustEn = isInstance
            || tn.Equals("R_TRIG", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("F_TRIG", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("P_TRIG", StringComparison.OrdinalIgnoreCase)
            || TimerTypes.Contains(tn)
            || CounterTypes.Contains(tn)
            || tn.Equals("SR", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("RS", StringComparison.OrdinalIgnoreCase);
        if (!mustEn) return;
        var part = _network.AddAccess("R|LiteralConstant|TRUE|" + tn + partUId, write: false);
        part.Scope = "LiteralConstant";
        part.ConstantType = "Bool";
        part.ConstantValue = "true";
        _network.Wire(new FbdNode { UId = part.UId, IsAccess = true }, new FbdNode { UId = partUId, Pin = "en" });
    }

    /// <summary>Feeds a box's mandatory <c>en</c> pin from a fresh <c>TRUE</c> literal. A fresh access is
    /// used per box so the literal never itself fans out.</summary>
    private void WireBooleanEnable(string partUId)
    {
        var literal = _network.NewAccess();
        literal.Scope = "LiteralConstant";
        literal.ConstantType = "Bool";
        literal.ConstantValue = "true";
        _network.Wire(new FbdNode { UId = literal.UId, IsAccess = true }, new FbdNode { UId = partUId, Pin = "en" });
    }

    private string? RenderCall(BlockCall call)
    {
        if (call.TypeStatus == ConversionStatus.Manual)
        {
            return $"call {call.TypeName} is marked as manually mapped and has no SCL form.";
        }

        if (!call.IsFunction)
        {
            return RenderInstanceCall(call);
        }

        if (FbdHelpers.TryGet(call.TypeName, out var helper))
        {
            _result.Approximations.Add($"{call.TypeName} becomes helper FC {helper.SiemensName}");
            _helpers.TryAdd(helper.SiemensName, helper.Render());
            return RenderHelperCall(call, helper);
        }

        if (call.TypeName.Contains("_TO_", StringComparison.OrdinalIgnoreCase))
        {
            return RenderConversionCall(call);
        }

        if (_knownBlockNames.Contains(call.TypeName))
        {
            return $"user function call {call.TypeName} has no FBD form yet; render it as SCL.";
        }

        return RenderBoxFunction(call);
    }

    private string? RenderBoxFunction(BlockCall call)
    {
        var kind = FbdVocabulary.BoxKind(call.TypeName);
        if (kind == FbdBoxKind.Unsupported)
        {
            return $"function {call.TypeName} has no FBD form verified against TIA V16; render it as SCL.";
        }

        // Not is the only Boolean box, and its spelling is entirely dialect-specific, so it is handled
        // on its own.
        if (kind == FbdBoxKind.Negate)
        {
            return RenderNotBox(call);
        }

        if (kind == FbdBoxKind.Limit)
        {
            return RenderLimitBox(call);
        }

        if (kind == FbdBoxKind.Unary)
        {
            return RenderUnaryBox(call);
        }

        if (kind == FbdBoxKind.MinMax)
        {
            return RenderMinMaxBox(call);
        }

        if (kind == FbdBoxKind.Power)
        {
            return RenderPowerBox(call);
        }

        if (kind == FbdBoxKind.Shift)
        {
            return RenderShiftBox(call);
        }

        if (kind == FbdBoxKind.Select)
        {
            return RenderSelectBox(call);
        }

        if (kind == FbdBoxKind.Mux)
        {
            return RenderMuxBox(call);
        }

        var gaMove = kind == FbdBoxKind.Math
            && call.TypeName == "MOVE"
            && _dialect == FbdBoxDialect.V16Ga;

        // TIA FBD "Move" is a terminal assignment instruction: its output can never feed a box pin
        // (verified: a Move output wired into an And pin is rejected with "The power rail ... contains
        // an invalid connection ... at pin 'out1'"). A TriStation MOVE block, however, is just a value
        // copy whose output frequently fans out into gates. Resolve it to its input instead, so
        // consumers wire directly from the source value.
        if (gaMove)
        {
            var copy = call.Inputs.Concat(call.InOuts)
                .FirstOrDefault(argument => argument.Sources.Count == 1);
            if (copy is not null)
            {
                _moveCopySources[call.SheetKey] = copy.Sources[0];
                return null;
            }
        }

        // The operand type of a box is uniform across its inputs; it types both a comparison's SrcType
        // and any downstream box that consumes the box's output.
        var operandType = call.Inputs.Concat(call.InOuts)
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        var part = _network.AddPart();
        part.PartName = FbdVocabulary.Box(call.TypeName);
        // A GA Move rejects the automatic SrcType the other arithmetic boxes use; it is typed only by
        // its Card (its output count) and its mandatory en. For the remaining arithmetic boxes prefer
        // an explicit SrcType resolved from the operands (compiles cleanly, like Sqrt): the automatic
        // type only succeeds when TIA can infer it, and an all-constant box otherwise fails to import
        // ("A least one constant must be typed" / "Please select a data type").
        var isMove = call.TypeName == "MOVE";
        var explicitMathSrcType = kind == FbdBoxKind.Math && !isMove ? operandType : null;
        part.AutomaticSrcType = isMove
            ? !gaMove
            : kind == FbdBoxKind.Math && explicitMathSrcType is null;

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode
        {
            UId = part.UId,
            Pin = FbdVocabulary.OutputPin(call.TypeName),
            OutputType = kind is FbdBoxKind.Compare or FbdBoxKind.Gate ? "Bool" : operandType,
        };

        if (kind == FbdBoxKind.Math)
        {
            // TIA FBD gives arithmetic and Move boxes an en pin; it must be connected. A TRUE literal
            // feeds it (genuine V16 exports wire a Bool literal into en).
            WireBooleanEnable(part.UId);
        }

        var inputError = WireBoxInputs(call, part);
        if (inputError is not null) return inputError;

        // Genuine V16 exports order the template values Card then SrcType.
        if (kind != FbdBoxKind.Compare && call.TypeName is not ("SUB" or "DIV" or "MOD"))
        {
            var wired = call.Inputs.Concat(call.InOuts).Count(argument => argument.Sources.Count > 0);
            // A GA Move's Card is its output count, so a single-target MOVE is Card 1; every other
            // box's Card is its operand count (at least two).
            var card = gaMove ? 1 : Math.Max(2, wired);
            part.TemplateValues.Add(new FbdTemplateValue
            {
                Name = "Card",
                Type = "Cardinality",
                Value = card.ToString(GenerationHelpers.Culture),
            });
        }

        if (explicitMathSrcType is not null)
        {
            part.TemplateValues.Add(new FbdTemplateValue
            {
                Name = "SrcType",
                Type = "Type",
                Value = explicitMathSrcType,
            });
        }

        if (kind == FbdBoxKind.Compare)
        {
            // A comparison's SrcType must match its operands. Prefer a wired operand's own resolved
            // type, then the variable's declared type, then the comparator's declared input type;
            // only when none of these is known do we fall back. Emitting a guessed type is rejected
            // ("Data type Bool is not permitted here").
            if (operandType is null)
            {
                return $"comparison {call.TypeName} has operands of unknown type; render it as SCL.";
            }

            part.TemplateValues.Add(new FbdTemplateValue
            {
                Name = "SrcType",
                Type = "Type",
                Value = operandType,
            });
        }

        return null;
    }

    /// <summary>
    /// Renders a Boolean <c>NOT</c>. Update-3 targets use the genuine <c>Not</c> part (Card + SrcType
    /// Bool + an <c>en</c>); the V16.0 GA Openness build cannot import that part (it reports an
    /// unformatted error for any <c>Not</c> spelling), so an equivalent single-input <c>A</c> gate
    /// with an inverted <c>in1</c> is emitted instead.
    /// </summary>
    private string? RenderNotBox(BlockCall call)
    {
        var srcType = call.Inputs.Concat(call.InOuts)
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        // A TriStation NOT may take a non-Boolean operand: its alarm workbits are 0/1 booleans stored
        // in an integer, and the SCL fallback coerces both ways (BOOL_TO_DINT(NOT(DINT_TO_BOOL(x)))).
        // The Boolean gate is kept and the same coercion is expressed with Move boxes — WireBoxInputs
        // inserts the input Move (integer -> Bool) from the pin's expected type, and the output Move
        // (Bool -> integer) is added below. Only an operand that is neither Bool nor a known
        // elementary type still has no verified shape.
        if (srcType is not null
            && !srcType.Equals("Bool", StringComparison.OrdinalIgnoreCase)
            && FbdVocabulary.ElementaryToken(srcType) is null)
        {
            return $"NOT has a {srcType} operand; only the Boolean Not box is verified, render it as SCL.";
        }

        var convertOutput = srcType is not null && FbdVocabulary.NeedsConversion(srcType, "Bool");

        var part = _network.AddPart();
        _functionParts[call.SheetKey] = part;
        var gateOut = new FbdNode { UId = part.UId, Pin = "out", OutputType = "Bool" };
        _functionOutputs[call.SheetKey] = convertOutput ? ConvertBooleanOutput(gateOut, srcType!) : gateOut;

        if (_dialect == FbdBoxDialect.V16Ga)
        {
            // A single-input AND whose only input is inverted is a NOT, and it imports on V16.0 GA.
            part.PartName = "A";
            part.TemplateValues.Add(new FbdTemplateValue { Name = "Card", Type = "Cardinality", Value = "1" });
            part.NegatedPins.Add("in1");
            return WireBoxInputs(call, part, pinFor: _ => "in1");
        }

        part.PartName = "Not";
        part.TemplateValues.Add(new FbdTemplateValue { Name = "Card", Type = "Cardinality", Value = "2" });
        part.TemplateValues.Add(new FbdTemplateValue { Name = "SrcType", Type = "Type", Value = "Bool" });
        // TIA FBD gives the Not box an en pin; genuine V16 exports wire a Bool literal into it.
        WireBooleanEnable(part.UId);
        return WireBoxInputs(call, part);
    }

    /// <summary>
    /// Converts a Boolean part output (the NOT gate) back to the integer type the TriStation NOT
    /// produces, so the consumers see the type the diagram declared. A <c>Move</c> rejects a Bool
    /// operand ("Data type Bool is not permitted here"), so the conversion is expressed as
    /// <c>SEL(G := value, IN0 := 0, IN1 := 1)</c>, which maps false/true to 0/1 and imports on V16.0
    /// GA. The gate output is routed through a Bool Temp first so it never feeds a part pin directly
    /// ("The power rail ... contains an invalid connection ... at pin 'in'").
    /// </summary>
    private FbdNode ConvertBooleanOutput(FbdNode source, string toType)
    {
        var tempRead = RouteCallOutputThroughTemp(source, "Bool");
        var destType = FbdVocabulary.IecNameForType(toType) ?? toType;

        var part = _network.AddPart();
        part.PartName = "Sel";
        part.TemplateValues.Add(new FbdTemplateValue { Name = "value_type", Type = "Type", Value = destType });
        WireBooleanEnable(part.UId);

        _network.Wire(tempRead, new FbdNode { UId = part.UId, Pin = "G" });
        _network.Wire(LiteralZero(destType), new FbdNode { UId = part.UId, Pin = "IN0" });
        _network.Wire(LiteralOne(destType), new FbdNode { UId = part.UId, Pin = "IN1" });

        _result.Approximations.Add($"inserted Sel conversion Bool -> {FbdVocabulary.ElementaryToken(toType)}");
        return new FbdNode
        {
            UId = part.UId,
            Pin = "out",
            OutputType = destType,
        };
    }

    /// <summary>
    /// Renders a <c>LIMIT</c>. Live V16.0 GA import: the part is <c>Limit</c> and needs a concrete
    /// <c>value_type</c> template value (the shared elementary type of MN/IN/MX). Its pins are
    /// <c>MN</c>/<c>in</c>/<c>MX</c> and <c>out</c>, and it rejects the default enabled ENO
    /// ("ENO cannot be deactivated for the 'Limit' instruction") unless <c>DisabledENO="true"</c> is
    /// present.
    /// </summary>
    private string? RenderLimitBox(BlockCall call)
    {
        var valueType = call.Inputs.Concat(call.InOuts)
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        if (valueType is null)
        {
            return "LIMIT has operands of unknown type; render it as SCL.";
        }

        var part = _network.AddPart();
        part.PartName = "Limit";
        part.DisabledEno = "true";
        part.TemplateValues.Add(new FbdTemplateValue { Name = "value_type", Type = "Type", Value = valueType });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode { UId = part.UId, Pin = "out", OutputType = valueType };

        WireBooleanEnable(part.UId);
        return WireBoxInputs(call, part);
    }

    /// <summary>
    /// Renders a single-operand math box that carries a concrete <c>SrcType</c> template value rather
    /// than the arithmetic <c>Card</c>+<c>AutomaticTyped</c> pair. Verified for <c>Sqrt</c> on V16.0
    /// GA (<c>&lt;Part Name="Sqrt"&gt;&lt;TemplateValue Name="SrcType" Type="Type"&gt;Real&lt;/TemplateValue&gt;&lt;/Part&gt;</c>,
    /// pins <c>in</c>/<c>out</c> with a mandatory <c>en</c>).
    /// </summary>
    private string? RenderUnaryBox(BlockCall call)
    {
        var srcType = call.Inputs.Concat(call.InOuts)
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        if (srcType is null)
        {
            return $"{call.TypeName} has operands of unknown type; render it as SCL.";
        }

        var part = _network.AddPart();
        part.PartName = FbdVocabulary.Box(call.TypeName);
        part.TemplateValues.Add(new FbdTemplateValue { Name = "SrcType", Type = "Type", Value = srcType });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode { UId = part.UId, Pin = "out", OutputType = srcType };

        WireBooleanEnable(part.UId);
        return WireBoxInputs(call, part);
    }

    /// <summary>
    /// Renders a <c>MIN</c>/<c>MAX</c>. Live V16.0 GA import: the part is <c>Min</c>/<c>Max</c> and
    /// needs a lowercase <c>card</c> (the operand count) and a <c>value_type</c> template value (the
    /// shared elementary type of the operands). Its pins are <c>in1</c>...<c>inN</c> and <c>out</c>,
    /// it has a mandatory <c>en</c>, and it rejects the default ENO unless <c>DisabledENO="false"</c>
    /// is present (probes PBMIN_K/L/M, PBMAX_K). Neither <c>Card</c> nor <c>SrcType</c> is accepted.
    /// </summary>
    private string? RenderMinMaxBox(BlockCall call)
    {
        var valueType = call.Inputs.Concat(call.InOuts)
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        if (valueType is null)
        {
            return $"{call.TypeName} has operands of unknown type; render it as SCL.";
        }

        var wired = call.Inputs.Concat(call.InOuts).Count(argument => argument.Sources.Count > 0);

        var part = _network.AddPart();
        part.PartName = FbdVocabulary.Box(call.TypeName);
        part.DisabledEno = "false";
        part.TemplateValues.Add(new FbdTemplateValue
        {
            Name = "card",
            Type = "Cardinality",
            Value = Math.Max(2, wired).ToString(GenerationHelpers.Culture),
        });
        part.TemplateValues.Add(new FbdTemplateValue { Name = "value_type", Type = "Type", Value = valueType });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode { UId = part.UId, Pin = "out", OutputType = valueType };

        WireBooleanEnable(part.UId);
        return WireBoxInputs(call, part);
    }

    /// <summary>
    /// Renders a <c>POW</c>. Live V16.0 GA import: the part is <c>Expt</c> and needs <c>SrcType</c>
    /// and <c>DestType</c> template values (both the operand type); its pins are <c>in1</c>/<c>in2</c>
    /// and <c>out</c>, with a mandatory <c>en</c> (probe PBPOW_D). It accepts no <c>Card</c>.
    /// </summary>
    private string? RenderPowerBox(BlockCall call)
    {
        var valueType = call.Inputs.Concat(call.InOuts)
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        if (valueType is null)
        {
            return $"{call.TypeName} has operands of unknown type; render it as SCL.";
        }

        var part = _network.AddPart();
        part.PartName = FbdVocabulary.Box(call.TypeName);
        part.TemplateValues.Add(new FbdTemplateValue { Name = "SrcType", Type = "Type", Value = valueType });
        part.TemplateValues.Add(new FbdTemplateValue { Name = "DestType", Type = "Type", Value = valueType });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode { UId = part.UId, Pin = "out", OutputType = valueType };

        WireBooleanEnable(part.UId);
        return WireBoxInputs(call, part);
    }

    /// <summary>
    /// Renders a <c>SHL</c>/<c>SHR</c>. Live V16.0 GA import: the part is <c>SHL</c>/<c>SHR</c> and
    /// carries only a <c>SrcType</c> (it rejects a <c>Card</c>); its pins are <c>in1</c>/<c>in2</c> and
    /// <c>out</c>, with a mandatory <c>en</c> (probes PBSHL_EN/PBSHR_EN).
    /// </summary>
    private string? RenderShiftBox(BlockCall call)
    {
        var valueType = call.Inputs.Concat(call.InOuts)
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        if (valueType is null)
        {
            return $"{call.TypeName} has operands of unknown type; render it as SCL.";
        }

        var part = _network.AddPart();
        part.PartName = FbdVocabulary.Box(call.TypeName);
        part.TemplateValues.Add(new FbdTemplateValue { Name = "SrcType", Type = "Type", Value = valueType });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode { UId = part.UId, Pin = "out", OutputType = valueType };

        WireBooleanEnable(part.UId);
        return WireBoxInputs(call, part);
    }

    /// <summary>
    /// Renders a <c>SEL</c>. Live V16.0 GA import: the part is <c>Sel</c> and needs a concrete
    /// <c>value_type</c> template value; its pins are <c>G</c>, <c>in0</c>, <c>in1</c> and <c>out</c>
    /// (the formal names are <c>G</c>/<c>IN0</c>/<c>IN1</c>), with a mandatory <c>en</c>. The selector
    /// <c>G</c> is Boolean; the two data inputs share the operand type (probes MINSEL_G/H/K).
    /// </summary>
    private string? RenderSelectBox(BlockCall call)
    {
        var operandType = call.Inputs.Concat(call.InOuts)
            .Where(argument => !IsSelectorArgument(argument, "G"))
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        if (operandType is null)
        {
            return "SEL has operands of unknown type; render it as SCL.";
        }

        var part = _network.AddPart();
        part.PartName = "Sel";
        part.TemplateValues.Add(new FbdTemplateValue { Name = "value_type", Type = "Type", Value = operandType });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode { UId = part.UId, Pin = "out", OutputType = operandType };

        WireBooleanEnable(part.UId);
        return WireSelectorInputs(call, part, operandType, "G");
    }

    /// <summary>
    /// Renders a <c>MUX</c>. Live V16.0 GA import: the part is <c>Mux</c> and needs a <c>Card</c>
    /// (the number of data inputs, which must be at least two) and a <c>SrcType</c> template value;
    /// its pins are <c>K</c>, <c>in0</c>...<c>in(n-1)</c>, <c>else</c> and <c>out</c>, with a mandatory
    /// <c>en</c>. TriStation's MUX has no <c>else</c> operand (the value TIA selects when <c>K</c> is
    /// out of range), so a typed zero is wired there as the harmless default.
    /// </summary>
    private string? RenderMuxBox(BlockCall call)
    {
        var dataArguments = call.Inputs.Concat(call.InOuts)
            .Where(argument => !IsSelectorArgument(argument, "K"))
            .ToList();

        if (dataArguments.Count < 2 || dataArguments.Any(argument => argument.Sources.Count == 0))
        {
            return "MUX needs at least two wired data inputs; render it as SCL.";
        }

        var operandType = dataArguments
            .Select(ResolveOperandType)
            .FirstOrDefault(name => name is not null);

        if (operandType is null)
        {
            return "MUX has operands of unknown type; render it as SCL.";
        }

        if (operandType == "Bool")
        {
            return "MUX over Boolean operands is not verified; render it as SCL.";
        }

        var part = _network.AddPart();
        part.PartName = "Mux";
        part.TemplateValues.Add(new FbdTemplateValue
        {
            Name = "Card",
            Type = "Cardinality",
            Value = dataArguments.Count.ToString(GenerationHelpers.Culture),
        });
        part.TemplateValues.Add(new FbdTemplateValue { Name = "SrcType", Type = "Type", Value = operandType });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode { UId = part.UId, Pin = "out", OutputType = operandType };

        WireBooleanEnable(part.UId);
        var inputError = WireSelectorInputs(call, part, operandType, "K");
        if (inputError is not null) return inputError;

        _network.Wire(LiteralZero(operandType), new FbdNode { UId = part.UId, Pin = "else" });
        _result.Approximations.Add("MUX 'else' input defaults to 0");
        return null;
    }

    /// <summary>Maps a TriStation selector formal (<c>G</c> for SEL, <c>K</c> for MUX) or a data
    /// formal (<c>IN0</c>, <c>IN1</c>, ...) to the TIA box pin name (<c>G</c>/<c>K</c> or
    /// <c>in0</c>, <c>in1</c>, ...). Null when the formal has no counterpart on the box.</summary>
    private static string? SelectorPin(string? formal)
    {
        var name = formal?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        if (name.Equals("G", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("K", StringComparison.OrdinalIgnoreCase))
        {
            return name.ToUpperInvariant();
        }

        if (name.StartsWith("IN", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(name.Substring(2), out var index))
        {
            return "in" + index.ToString(GenerationHelpers.Culture);
        }

        return null;
    }

    /// <summary>Wires the selector box's inputs by formal name, converting each to the type its pin
    /// expects: the SEL selector <c>G</c> to <c>Bool</c>, the MUX selector <c>K</c> left as its own
    /// integer type (probe: <c>DInt</c>/<c>UInt</c>/<c>USInt</c>/<c>Int</c> all import against a
    /// <c>Mux.K</c>), and every data input to the shared operand type.</summary>
    private string? WireSelectorInputs(BlockCall call, FbdPart part, string operandType, string selectorPin)
    {
        foreach (var argument in call.Inputs.Concat(call.InOuts))
        {
            var pin = SelectorPin(argument.MappedFormalParameter ?? argument.FormalParameter);
            if (pin is null)
            {
                return $"{call.TypeName}: input {argument.FormalParameter} has no FBD pin on the " +
                       $"{FbdVocabulary.Box(call.TypeName)} box.";
            }

            if (argument.Sources.Count == 0)
            {
                return $"{call.TypeName}: required input {argument.FormalParameter} is not wired; render it as SCL.";
            }

            if (argument.Sources.Count > 1)
            {
                return $"{call.TypeName}: input {argument.FormalParameter} has more than one wire; " +
                       "an FBD pin accepts exactly one.";
            }

            var source = argument.Sources[0];
            var sourceNode = ResolveSource(source);
            if (sourceNode is null)
            {
                return $"{call.TypeName}: the value of input {pin} comes from an item outside this network.";
            }

            var isSelector = string.Equals(pin, selectorPin, StringComparison.Ordinal);
            // The SEL selector G is Boolean; the MUX selector K is an integer, so its source is wired
            // through untouched (forcing Bool onto K fails live import: "The data type Bool of the
            // actual parameter does not match the data type UInt of the formal parameter").
            var targetType = isSelector
                ? (string.Equals(selectorPin, "G", StringComparison.OrdinalIgnoreCase) ? "Bool" : null)
                : operandType;
            WireOrConvert(sourceNode, new FbdNode { UId = part.UId, Pin = pin }, ResolveSourceType(source), targetType);
        }

        return null;
    }

    private static bool IsSelectorArgument(CallArgument argument, string selector) =>
        string.Equals(
            (argument.MappedFormalParameter ?? argument.FormalParameter)?.Trim(),
            selector,
            StringComparison.OrdinalIgnoreCase);

    /// <summary>A fresh literal-zero access of the given elementary type, for a box pin that needs a
    /// neutral default the diagram does not supply (the MUX <c>else</c> input).</summary>
    private FbdNode LiteralZero(string iecType)
    {
        var access = _network.NewAccess();
        access.Scope = "LiteralConstant";
        access.ConstantType = iecType;
        access.ConstantValue = iecType is "Real" or "LReal" ? "0.0" : "0";
        return new FbdNode { UId = access.UId, IsAccess = true, OutputType = iecType };
    }

    /// <summary>A fresh literal-one access of the given elementary type (the SEL <c>IN1</c> operand
    /// used to turn a Boolean into an integer).</summary>
    private FbdNode LiteralOne(string iecType)
    {
        var access = _network.NewAccess();
        access.Scope = "LiteralConstant";
        access.ConstantType = iecType;
        access.ConstantValue = iecType is "Real" or "LReal" ? "1.0" : "1";
        return new FbdNode { UId = access.UId, IsAccess = true, OutputType = iecType };
    }

    private string? RenderConversionCall(BlockCall call)
    {
        var sourceType = call.Inputs.Concat(call.InOuts)
            .SelectMany(argument => argument.Sources)
            .FirstOrDefault(source => !string.IsNullOrWhiteSpace(source.TypeName))
            ?.TypeName;
        var destType = call.Outputs.FirstOrDefault()?.ExpectedType ?? call.ResultType;

        // A conversion block's name already spells both types ("DINT_TO_DWORD"), so it can be rendered
        // even when the wired operands carry no resolved type. Fall back to the operand types when the
        // name does not carry a "<from>_TO_<to>" shape.
        var (nameFrom, nameTo) = SplitConversionName(call.TypeName);
        var fromElementary = FbdVocabulary.ElementaryToken(nameFrom)
            ?? FbdVocabulary.ElementaryToken(sourceType);
        var toElementary = FbdVocabulary.ElementaryToken(nameTo)
            ?? FbdVocabulary.ElementaryToken(destType);
        if (fromElementary is null || toElementary is null)
        {
            return $"conversion call {call.TypeName} has no typed operands ({sourceType} -> {destType}).";
        }

        _result.Approximations.Add($"conversion {call.TypeName} ({fromElementary} -> {toElementary})");

        // A REAL operand needs the CONVERT instruction (SrcType/DestType); every other pair is a Move
        // copy (see FbdVocabulary.ConversionNeedsConvertBox for the probe matrix behind this choice).
        var usesConvert = FbdVocabulary.ConversionNeedsConvertBox(fromElementary, toElementary);

        var part = _network.AddPart();
        if (usesConvert)
        {
            part.PartName = "CONVERT";
            part.TemplateValues.Add(new FbdTemplateValue
            {
                Name = "SrcType",
                Type = "Type",
                Value = FbdVocabulary.IecNameForType(fromElementary) ?? fromElementary,
            });
            part.TemplateValues.Add(new FbdTemplateValue
            {
                Name = "DestType",
                Type = "Type",
                Value = FbdVocabulary.IecNameForType(toElementary) ?? toElementary,
            });
        }
        else
        {
            part.PartName = "Move";
            part.TemplateValues.Add(new FbdTemplateValue { Name = "Card", Type = "Cardinality", Value = "1" });
        }

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode
        {
            UId = part.UId,
            Pin = usesConvert ? "out" : "out1",
            IsTerminalOutput = true,
            OutputType = FbdVocabulary.IecNameForType(toElementary),
        };

        WireBooleanEnable(part.UId);
        return WireBoxInputs(call, part, conversion: true);
    }

    /// <summary>Splits a conversion block name ("DINT_TO_DWORD") into its source and destination type
    /// names. Returns (null, null) when the name does not carry a "&lt;from&gt;_TO_&lt;to&gt;" shape.</summary>
    private static (string? From, string? To) SplitConversionName(string typeName)
    {
        var marker = typeName.IndexOf("_TO_", StringComparison.OrdinalIgnoreCase);
        if (marker < 0) return (null, null);

        var from = typeName.Substring(0, marker);
        var to = typeName.Substring(marker + "_TO_".Length);
        return (from, to);
    }

    /// <summary>
    /// Canonical IEC type for one wired input of a box: a wired operand's own type, else the
    /// variable's declared block/global type, else the callee's declared input type. Null when none
    /// is known.
    /// </summary>
    private string? ResolveOperandType(CallArgument argument)
    {
        foreach (var source in argument.Sources)
        {
            var wired = FbdVocabulary.IecNameForType(source.TypeName);
            if (wired is not null) return wired;

            // A block output carries no type of its own on the wire; recover it from the producing
            // box once that box has been rendered (the dependency order guarantees it already has).
            if (source.IsBlockOutput)
            {
                var produced = FbdVocabulary.IecNameForType(ProducerOutputType(source));
                if (produced is not null) return produced;
            }

            var declared = FbdVocabulary.IecNameForType(VariableTypeOf(source));
            if (declared is not null) return declared;
        }

        return FbdVocabulary.IecNameForType(argument.ExpectedType);
    }

    /// <summary>
    /// Canonical IEC type of a single wired source, resolving a variable's declared type when the
    /// wire itself carries none (a TriStation FBD bit operand frequently has no DATATYPE). Mirrors
    /// <see cref="ResolveOperandType"/> for one source rather than a whole argument.
    /// </summary>
    private string? ResolveSourceType(SignalSource source)
    {
        var wired = FbdVocabulary.IecNameForType(source.TypeName);
        if (wired is not null) return wired;

        if (source.IsBlockOutput)
        {
            return FbdVocabulary.IecNameForType(ProducerOutputType(source));
        }

        return FbdVocabulary.IecNameForType(VariableTypeOf(source));
    }

    /// <summary>The output type recorded for the box that produced a block-output source, or null
    /// when the producer is unknown or its type has not been determined.</summary>
    private string? ProducerOutputType(SignalSource source)
    {
        if (source.RefLocalId is null) return null;

        var key = FbdBlockRenderer.Key(source.RefSheetNumber, source.RefLocalId);
        // A resolved MOVE copy has no box of its own; its value takes the type of the copied source.
        if (_moveCopySources.TryGetValue(key, out var copySource))
        {
            if (copySource.IsBlockOutput) return ProducerOutputType(copySource);
            var copyWired = FbdVocabulary.IecNameForType(copySource.TypeName);
            if (copyWired is not null) return copyWired;
            return VariableTypeOf(copySource);
        }

        if (_functionOutputs.TryGetValue(key, out var function) && function.OutputType is not null)
        {
            return function.OutputType;
        }

        return null;
    }

    /// <summary>The declared type of a bare variable operand, or null when it is a block output/constant.</summary>
    private string? VariableTypeOf(SignalSource source)
    {
        if (source.IsBlockOutput) return null;

        var name = (source.Expression ?? string.Empty).Trim().TrimStart('#').Trim();
        if (name.Length == 0) return null;

        return _variableTypes.TryGetValue(name, out var type) ? type : null;
    }

    private string? RenderHelperCall(BlockCall call, FbdHelper helper)
    {
        var part = _network.AddCall();
        part.CallName = helper.SiemensName;
        part.BlockType = "FC";
        foreach (var (name, type) in helper.Inputs)
        {
            part.Parameters.Add(new FbdParameter { Name = name, Section = "Input", Type = type });
        }

        // An FC's return value is wired as RET_VAL and must be declared with Section="Return"; as a
        // plain "Output" the whole call is rejected ("The parameter list of the call ... cannot be
        // used"), and omitted entirely the wire has no connection.
        part.Parameters.Add(new FbdParameter
        {
            Name = FbdHelper.OutputFormal,
            Section = "Return",
            Type = helper.OutputType,
        });

        _functionParts[call.SheetKey] = part;
        _functionOutputs[call.SheetKey] = new FbdNode
        {
            UId = part.UId,
            Pin = FbdHelper.OutputFormal,
            IsCallOutput = true,
            OutputType = helper.OutputType,
        };

        // TIA requires a <Call>'s mandatory en pin to be wired, exactly as it does for a <Part> box.
        WireBooleanEnable(part.UId);

        return WireNamedPins(call, part, recordParameters: false);
    }

    private string? RenderInstanceCall(BlockCall call)
    {
        var mappedType = call.MappedTypeName ?? call.TypeName;
        var instanceName = InstanceOf(call);

        // TIA resolves a user function block only through <Call>/<CallInfo>; a <Part> name is looked up
        // in the built-in instruction set and fails with "An instruction with the name '..' cannot be
        // found". The stateful system instructions (triggers, timers, counters, SR/RS) keep the
        // <Part> form. Both shapes verified against live V16.0 GA import.
        if (!IsSystemInstanceType(mappedType))
        {
            return RenderUserBlockCall(call, mappedType, instanceName);
        }

        var part = _network.AddPart();
        part.PartName = mappedType;
        part.IsInstance = true;
        part.InstanceComponent = instanceName;
        part.InstanceScope = "LocalVariable";
        part.InstanceUId = _network.NextId();
        var wiredPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (TimerTypes.Contains(mappedType))
        {
            part.TemplateValues.Add(new FbdTemplateValue { Name = "time_type", Type = "Type", Value = "Time" });
        }

        if (CounterTypes.Contains(mappedType))
        {
            part.TemplateValues.Add(new FbdTemplateValue { Name = "value_type", Type = "Type", Value = "Int" });
        }

        _instanceParts[call.SheetKey] = part;
        WireEnable(mappedType, part.UId, isInstance: true);

        var index = 0;
        foreach (var argument in call.Inputs.Concat(call.InOuts))
        {
            if (argument.Sources.Count == 0) continue;

            if (argument.Sources.Count > 1)
            {
                return $"instance {instanceName}: input {argument.FormalParameter} has more than one wire; " +
                       "an FBD pin accepts exactly one.";
            }

            var pin = argument.MappedFormalParameter ?? argument.FormalParameter;
            if (string.IsNullOrWhiteSpace(pin))
            {
                return $"instance {instanceName}: a wired input has no formal pin name.";
            }

            var source = argument.Sources[0];
            var sourceNode = ResolveSource(source);
            if (sourceNode is null)
            {
                return $"instance {instanceName}: the value of input {pin} comes from an item " +
                       "outside this network.";
            }

            WireOrConvert(sourceNode, new FbdNode { UId = part.UId, Pin = pin }, ResolveSourceType(source), argument.ExpectedType);
            wiredPins.Add(pin);
            index++;
        }

        // A stateful Part box (timer/trigger/counter) has mandatory control pins TIA rejects when left
        // unwired ("The connection with the name 'IN' is not connected"). TriStation leaves a pin
        // unconnected to mean the box never senses an event: an unwired Boolean (IN/CLK) reads FALSE
        // and an unwired preset (PT) reads 0. Wire a matching neutral literal so the FBD form stays
        // valid instead of falling back to SCL.
        foreach (var required in RequiredControlPins(mappedType))
        {
            if (wiredPins.Contains(required)) continue;

            _network.Wire(DefaultControlLiteral(required), new FbdNode { UId = part.UId, Pin = required });
            _result.Approximations.Add(
                $"{mappedType} '{instanceName}': unconnected '{required}' defaults to a neutral value");
        }

        return null;
    }

    /// <summary>A neutral literal for a mandatory control pin the source left unconnected: <c>FALSE</c>
    /// for a Boolean pin (<c>IN</c>/<c>CLK</c>), <c>T#0s</c> for the timer preset <c>PT</c>.</summary>
    private FbdNode DefaultControlLiteral(string pin)
    {
        var access = _network.NewAccess();
        if (pin.Trim().Equals("PT", StringComparison.OrdinalIgnoreCase))
        {
            access.Scope = "TypedConstant";
            access.IsTypedConstant = true;
            access.ConstantValue = "T#0s";
            return new FbdNode { UId = access.UId, IsAccess = true, OutputType = "Time" };
        }

        access.Scope = "LiteralConstant";
        access.ConstantType = "Bool";
        access.ConstantValue = "FALSE";
        return new FbdNode { UId = access.UId, IsAccess = true, OutputType = "Bool" };
    }

    /// <summary>The mandatory control input pins of a stateful system box. Timers need their trigger
    /// and preset; edge triggers need their clock. Every other pin is optional.</summary>
    private static string[] RequiredControlPins(string mappedType)
    {
        var tn = mappedType.Trim();
        if (TimerTypes.Contains(tn)) return new[] { "IN", "PT" };
        if (tn.Equals("R_TRIG", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("F_TRIG", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("P_TRIG", StringComparison.OrdinalIgnoreCase)) return new[] { "CLK" };
        return Array.Empty<string>();
    }

    /// <summary>True for the built-in stateful instructions that TIA resolves as a <c>&lt;Part&gt;</c>
    /// box. Every other instance type is a user block and must be called through <c>&lt;Call&gt;</c>.
    /// </summary>
    private static bool IsSystemInstanceType(string typeName)
    {
        var tn = typeName?.Trim() ?? string.Empty;
        return tn.Equals("R_TRIG", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("F_TRIG", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("P_TRIG", StringComparison.OrdinalIgnoreCase)
            || TimerTypes.Contains(tn)
            || CounterTypes.Contains(tn)
            || tn.Equals("SR", StringComparison.OrdinalIgnoreCase)
            || tn.Equals("RS", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renders a call to a user function block. TIA resolves user blocks only through
    /// <c>&lt;Call&gt;/&lt;CallInfo&gt;</c>; every wired pin must be declared as a <c>&lt;Parameter&gt;</c>
    /// and every declared pin must be wired, or the import is rejected ("There is no connection with
    /// the name '..'"). Verified against live V16.0 GA import.
    /// </summary>
    private string? RenderUserBlockCall(BlockCall call, string mappedType, string instanceName)
    {
        var part = _network.AddCall();
        part.CallName = mappedType;
        part.BlockType = "FB";
        part.IsInstance = true;
        part.InstanceComponent = instanceName;
        part.InstanceScope = "LocalVariable";
        part.InstanceUId = _network.NextId();

        _instanceParts[call.SheetKey] = part;
        WireEnable(mappedType, part.UId, isInstance: true);

        foreach (var argument in call.Inputs.Concat(call.InOuts))
        {
            if (argument.Sources.Count == 0) continue;

            if (argument.Sources.Count > 1)
            {
                return $"instance {instanceName}: input {argument.FormalParameter} has more than one wire; " +
                       "an FBD pin accepts exactly one.";
            }

            var pin = argument.MappedFormalParameter ?? argument.FormalParameter;
            if (string.IsNullOrWhiteSpace(pin))
            {
                return $"instance {instanceName}: a wired input has no formal pin name.";
            }

            var source = argument.Sources[0];
            var sourceNode = ResolveSource(source);
            if (sourceNode is null)
            {
                return $"instance {instanceName}: the value of input {pin} comes from an item " +
                       "outside this network.";
            }

            part.Parameters.Add(new FbdParameter
            {
                Name = pin,
                Section = call.InOuts.Contains(argument) ? "InOut" : "Input",
                Type = ParameterType(argument.ExpectedType),
            });

            WireOrConvert(
                sourceNode,
                new FbdNode { UId = part.UId, Pin = pin },
                ResolveSourceType(source),
                argument.ExpectedType);
        }

        return null;
    }

    /// <summary>Wires the wired inputs of a box (or conversion) to its index-based pins.</summary>
    private string? WireBoxInputs(
        BlockCall call,
        FbdPart part,
        bool conversion = false,
        Func<int, string>? pinFor = null)
    {
        var index = 0;
        foreach (var argument in call.Inputs.Concat(call.InOuts))
        {
            if (argument.Sources.Count == 0) continue;

            if (argument.Sources.Count > 1)
            {
                return $"{call.TypeName}: input {argument.FormalParameter} has more than one wire; " +
                       "an FBD pin accepts exactly one.";
            }

            index++;
            var pin = pinFor is not null
                ? pinFor(index)
                : conversion ? "in" : FbdVocabulary.InputPin(call.TypeName, index);
            if (string.IsNullOrWhiteSpace(pin))
            {
                return $"{call.TypeName}: a wired input has no formal pin name.";
            }

            var source = argument.Sources[0];
            var sourceNode = ResolveSource(source);
            if (sourceNode is null)
            {
                return $"{call.TypeName}: the value of input {pin} comes from an item outside " +
                       "this network.";
            }

            WireOrConvert(sourceNode, new FbdNode { UId = part.UId, Pin = pin }, ResolveSourceType(source), argument.ExpectedType);
        }

        return null;
    }

    /// <summary>Wires the wired inputs of an FC call to its named formal pins.</summary>
    private string? WireNamedPins(BlockCall call, FbdPart part, bool recordParameters)
    {
        foreach (var argument in call.Inputs.Concat(call.InOuts))
        {
            if (argument.Sources.Count == 0) continue;

            if (argument.Sources.Count > 1)
            {
                return $"{call.TypeName}: input {argument.FormalParameter} has more than one wire; " +
                       "an FBD pin accepts exactly one.";
            }

            var pin = argument.MappedFormalParameter ?? argument.FormalParameter;
            if (string.IsNullOrWhiteSpace(pin))
            {
                return $"{call.TypeName}: a wired input has no formal pin name.";
            }

            if (recordParameters)
            {
                part.Parameters.Add(new FbdParameter
                {
                    Name = pin,
                    Section = call.InOuts.Contains(argument) ? "InOut" : "Input",
                    Type = ParameterType(argument.ExpectedType),
                });
            }

            var source = argument.Sources[0];
            var sourceNode = ResolveSource(source);
            if (sourceNode is null)
            {
                return $"{call.TypeName}: the value of input {pin} comes from an item outside " +
                       "this network.";
            }

            WireOrConvert(sourceNode, new FbdNode { UId = part.UId, Pin = pin }, ResolveSourceType(source), argument.ExpectedType);
        }

        return null;
    }

    private static string? ParameterType(string? expected) =>
        FbdVocabulary.ElementaryToken(expected) ?? (string.IsNullOrWhiteSpace(expected) ? null : $"\"{expected.Trim('"')}\"");

    /// <summary>Resolves a wired source to a part output or an access.</summary>
    private FbdNode? ResolveSource(SignalSource source)
    {
        if (source.IsBlockOutput)
        {
            return ResolveBlockOutput(source.RefSheetNumber, source.RefLocalId!, source.RefFormalParameter);
        }

        return ExpressionAccess(source.Expression ?? string.Empty, write: false, source.TypeName);
    }

    private FbdNode? ResolveBlockOutput(int sheet, string localId, string? formal)
    {
        var key = FbdBlockRenderer.Key(sheet, localId);
        var fanOut = _fanOutRefs.Contains(RefKey(sheet, localId, formal));

        // A TriStation MOVE block is a value copy, resolved to its input so its output can feed box
        // pins (a terminal Move instruction output cannot; see IsTerminalOutput).
        if (_moveCopySources.TryGetValue(key, out var copySource))
        {
            return MaterializeFanOut(ResolveSource(copySource), fanOut);
        }

        if (!_callsBySheetKey.TryGetValue(key, out var producer))
        {
            return null;
        }

        if (producer.IsFunction)
        {
            return MaterializeFanOut(
                _functionOutputs.TryGetValue(key, out var node) ? node : null, fanOut);
        }

        var pin = InstanceOutputPin(producer, formal);
        var nodeKey = key + "|" + (pin ?? string.Empty);
        if (!_instanceOutputs.TryGetValue(nodeKey, out var instanceNode))
        {
            var part = _instanceParts[key];

            var expected = producer.Outputs
                .FirstOrDefault(argument => string.Equals(
                    argument.FormalParameter, formal, StringComparison.OrdinalIgnoreCase))
                ?.ExpectedType;

            // A <Call> pin must be declared as a <Parameter> to be wired. Outputs are only known to be
            // consumed when a consumer resolves them, so the declaration is added here, once per pin.
            if (part.Kind == "Call" && !string.IsNullOrWhiteSpace(pin))
            {
                var declaresOutput = part.Parameters.Any(parameter =>
                    parameter.Section == "Output" &&
                    string.Equals(parameter.Name, pin, StringComparison.OrdinalIgnoreCase));
                if (!declaresOutput)
                {
                    part.Parameters.Add(new FbdParameter
                    {
                        Name = pin,
                        Section = "Output",
                        Type = ParameterType(expected),
                    });
                }
            }

            instanceNode = new FbdNode
            {
                UId = part.UId,
                Pin = pin,
                IsInstanceOutput = true,
                IsCallOutput = part.Kind == "Call",
                OutputType = expected,
            };
            _instanceOutputs[nodeKey] = instanceNode;
        }

        return MaterializeFanOut(instanceNode, fanOut);
    }

    /// <summary>
    /// When a producer output feeds more than one consumer in the same sheet, FBD cannot express it as a
    /// single wire, so the value is materialized through a Temp: the producer pin is wired once into the
    /// Temp write and each consumer reads a fresh Temp access (see <see cref="RouteCallOutputThroughTemp"/>).
    /// Only non-access nodes are routed; an access source is already duplicated per consumer.
    /// </summary>
    private FbdNode? MaterializeFanOut(FbdNode? node, bool fanOut)
    {
        if (node is null || !fanOut || node.IsAccess) return node;
        return RouteCallOutputThroughTemp(node, node.OutputType);
    }

    private static string? InstanceOutputPin(BlockCall call, string? formal)
    {
        if (string.IsNullOrWhiteSpace(formal)) return null;
        if (call.MappedOutputFormals.TryGetValue(formal, out var mapped)) return mapped;
        return formal;
    }

    /// <summary>Creates or reuses an access part for an expression; null when not expressible.
    /// <paramref name="sourceType"/> is the type the diagram wired at this site (ts:DATATYPE); it
    /// lets a bare <c>0</c>/<c>1</c> literal be typed as Bool when the source says BOOL.</summary>
    private FbdNode? ExpressionAccess(string expression, bool write, string? sourceType = null)
    {
        var template = Classify(expression, sourceType);
        if (template is null)
        {
            return null;
        }

        var part = _network.NewAccess();

        part.Scope = template.Scope;
        part.ConstantType = template.ConstantType;
        part.ConstantValue = template.ConstantValue;
        part.IsTypedConstant = template.IsTypedConstant;
        part.Components.Clear();
        part.Components.AddRange(template.Components);

        // Carry the access's type so a later assignment can tell when a conversion is needed: a Bool
        // literal written to a DInt target must become a Sel, not a Move ("Data type Bool is not
        // permitted here."). For a named operand, look up its declared type; a literal carries its own.
        var outputType = template.Scope is "LiteralConstant" or "TypedConstant"
            ? template.ConstantType
            : ResolveNamedType(template.Components);

        return new FbdNode { UId = part.UId, IsAccess = true, OutputType = outputType };
    }

    /// <summary>The declared IEC type of a named operand (its last component), or null when unknown.</summary>
    private string? ResolveNamedType(IReadOnlyList<string> components)
    {
        if (components.Count == 0) return null;
        var name = components[^1].Trim();
        return _variableTypes.TryGetValue(name, out var declared)
            ? FbdVocabulary.IecNameForType(declared) ?? declared
            : null;
    }

    /// <summary>True when a declared source type is the boolean elementary type.</summary>
    private static bool IsBooleanType(string? type) =>
        string.Equals(FbdVocabulary.ElementaryToken(type), "BOOL", StringComparison.Ordinal);

    /// <summary>Classifies an expression into a local, a DB global, a bare global or a constant.</summary>
    private FbdPart? Classify(string expression, string? sourceType = null)
    {
        var trimmed = expression.Trim().TrimStart('#').Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        var part = new FbdPart { Kind = "Access" };

        if (FbdBlockRenderer.TimedConstant.IsMatch(trimmed))
        {
            part.Scope = "TypedConstant";
            part.IsTypedConstant = true;
            part.ConstantValue = trimmed;
            return part;
        }

        if (string.Equals(trimmed, "TRUE", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "FALSE", StringComparison.OrdinalIgnoreCase))
        {
            part.Scope = "LiteralConstant";
            part.ConstantType = "Bool";
            part.ConstantValue = trimmed.ToUpperInvariant();
            return part;
        }

        // The diagram can wire a boolean constant as 0/1 (ts:DATATYPE = BOOL). Without the declared
        // type those look like integer literals and would be emitted as DInt, which TIA rejects on a
        // Bool pin ("The data type DInt ... does not match ... Bool").
        if (IsBooleanType(sourceType) && FbdBlockRenderer.IntegerLiteral.IsMatch(trimmed)
            && (trimmed == "0" || trimmed == "1"))
        {
            part.Scope = "LiteralConstant";
            part.ConstantType = "Bool";
            part.ConstantValue = trimmed == "1" ? "TRUE" : "FALSE";
            return part;
        }

        if (FbdBlockRenderer.IntegerLiteral.IsMatch(trimmed))
        {
            part.Scope = "LiteralConstant";
            part.ConstantType = "DInt";
            part.ConstantValue = trimmed;
            return part;
        }

        if (FbdBlockRenderer.RealLiteral.IsMatch(trimmed))
        {
            part.Scope = "LiteralConstant";
            part.ConstantType = "Real";
            part.ConstantValue = trimmed;
            return part;
        }

        var components = trimmed
            .Split('.')
            .Select(component => component.Trim().Trim('"'))
            .Where(component => component.Length > 0)
            .ToList();
        if (components.Count == 0)
        {
            return null;
        }

        if (components.Count == 1 && _localNames.Contains(components[0]))
        {
            part.Scope = "LocalVariable";
            part.Components.Add(components[0]);
            _result.ReferencedLocals.Add(components[0]);
            return part;
        }

        part.Scope = "GlobalVariable";
        if (components.Count == 1 && _dbGlobals.Contains(components[0]))
        {
            part.Components.Add("GlobalData");
            part.Components.Add(components[0]);
        }
        else
        {
            part.Components.AddRange(components);
        }

        return part;
    }

    /// <summary>Wires a signal into a pin, inserting a CONV box when their types differ.</summary>
    private void WireOrConvert(FbdNode source, FbdNode sink, string? fromType, string? toType)
    {
        // An instance/stateful box output (a user <Call>, or a <Part> with an <Instance> such as
        // R_TRIG/timers/counters/SR) cannot source a wire into another box pin in live V16 import
        // ("The power rail (UId=..) contains an invalid connection to (UId=..) at pin 'Q'"):
        // probe MINQ (R_TRIG.Q -> Or.in1) fails, while MINQT (R_TRIG.Q -> Temp -> Or.in1) imports.
        // Route it through an intermediate Temp access; the Temp read may then feed any number of box
        // pins without tripping the fan-out guard. An assignment access is a valid direct sink, so it
        // keeps the direct instance -> IdentCon wire (probe MIN1E imports).
        if ((source.IsCallOutput || source.IsInstanceOutput) && !sink.IsAccess)
        {
            source = RouteCallOutputThroughTemp(source, fromType);
        }

        // A box/gate (or any non-operand) output wired straight into a user <Call> input pin is rejected
        // by live V16 import ("contains an invalid connection ... at pin '1'"; probe MINFC_D:
        // Limit.out -> Call.INPUT fails), while the same pin accepts an operand access (probe MINFC_A:
        // literal -> Call.INPUT imports). Route the producer through a Temp first.
        if (_network.IsCallPart(sink.UId) && !source.IsAccess)
        {
            source = RouteCallOutputThroughTemp(source, fromType);
        }

        // A <Negated/> input pin (the V16.0 GA NOT is a single-input A gate whose in1 is inverted)
        // cannot be fed directly by a part output: live V16 import rejects it ("The negation is
        // misplaced", or "The elements must be sorted according to the current flow" when the
        // producer is not adjacent; probe MINPP fails, MINPP3 imports). The same pin fed from an
        // operand access imports fine (probe MINT_A1N), so route the producer through a Temp.
        if (!source.IsAccess && _network.IsNegatedPin(sink.UId, sink.Pin))
        {
            source = RouteCallOutputThroughTemp(source, fromType);
        }

        // Live V16 import requires the <Parts> list to be in "flow" order: a plain logic/comparison
        // part output feeding another part's pin is rejected ("The elements must be sorted according to
        // the current flow") as soon as an unrelated part sits between producer and consumer in the
        // list (probe MINPP4 fails; MINPP3, with the producer immediately before the consumer, imports).
        // Funnelling the value through a Temp access removes the direct part -> part edge entirely, so
        // the list order no longer matters (probe MINPP8 imports). Non-plain producers (instance, Call,
        // Move, negated/terminal) are already rerouted to a Temp above, so anything still non-access
        // here is a stateless part output.
        if (!source.IsAccess && !sink.IsAccess)
        {
            source = RouteCallOutputThroughTemp(source, fromType);
        }

        // Rerouting above may have replaced a typed producer with a temp read access; keep its type so
        // a mismatched pin still gets the conversion box it needs.
        fromType ??= source.OutputType;

        if (!FbdVocabulary.NeedsConversion(fromType, toType))
        {
            // A terminal Move output (a CONV rendered as Move) cannot be wired straight into a box pin
            // ("The power rail ... contains an invalid connection ... at pin 'in'"); route it through a
            // Temp so the Move lands on an access. See IsTerminalOutput.
            if (source.IsTerminalOutput && !sink.IsAccess)
            {
                source = RouteCallOutputThroughTemp(source, fromType);
            }

            _network.Wire(source, sink);
            return;
        }

        // A mismatched type needs an explicit conversion: a Move rejects a Bool operand, so a
        // Bool<->integer conversion uses Ne/Sel, and a numeric mismatch uses a Move.
        var converted = InsertConversion(source, fromType, toType);

        // A terminal Move output (and any conversion box output) may only land on an access; when the
        // consumer is a box pin, route the converted value through a Temp and wire the pin from it.
        if (!sink.IsAccess)
        {
            converted = RouteCallOutputThroughTemp(converted, converted.OutputType);
        }

        _network.Wire(converted, sink);
    }

    /// <summary>
    /// Converts <paramref name="source"/> to <paramref name="toType"/> with a single box. A Bool
    /// target is produced by a <c>Ne</c> comparison against zero (<c>value &lt;&gt; 0</c>); a Bool
    /// source is widened by <c>SEL(G := value, IN0 := 0, IN1 := 1)</c>; any other numeric mismatch uses
    /// <c>CONVERT</c> when a <c>REAL</c> is involved, else a <c>Move</c> (see
    /// <see cref="FbdVocabulary.ConversionNeedsConvertBox"/>). The source is funnelled through a Temp
    /// when it is not already an access, to avoid a raw part to part edge.
    /// </summary>
    private FbdNode InsertConversion(FbdNode source, string? fromType, string? toType)
    {
        if (!source.IsAccess)
        {
            source = RouteCallOutputThroughTemp(source, fromType);
        }

        var fromToken = FbdVocabulary.ElementaryToken(fromType);
        var toToken = FbdVocabulary.ElementaryToken(toType);

        if (toToken == "BOOL")
        {
            var srcType = FbdVocabulary.IecNameForType(fromType) ?? fromType!;
            var compare = _network.AddPart();
            compare.PartName = "Ne";
            compare.TemplateValues.Add(new FbdTemplateValue { Name = "SrcType", Type = "Type", Value = srcType });
            _network.Wire(source, new FbdNode { UId = compare.UId, Pin = "in1" });
            _network.Wire(LiteralZero(srcType), new FbdNode { UId = compare.UId, Pin = "in2" });

            _result.Approximations.Add($"inserted Ne conversion {fromToken} -> BOOL");
            return new FbdNode { UId = compare.UId, Pin = "out", OutputType = "Bool" };
        }

        var destType = FbdVocabulary.IecNameForType(toType) ?? toType!;

        if (fromToken == "BOOL")
        {
            var select = _network.AddPart();
            select.PartName = "Sel";
            select.TemplateValues.Add(new FbdTemplateValue { Name = "value_type", Type = "Type", Value = destType });
            WireBooleanEnable(select.UId);
            _network.Wire(source, new FbdNode { UId = select.UId, Pin = "G" });
            _network.Wire(LiteralZero(destType), new FbdNode { UId = select.UId, Pin = "IN0" });
            _network.Wire(LiteralOne(destType), new FbdNode { UId = select.UId, Pin = "IN1" });

            _result.Approximations.Add($"inserted Sel conversion BOOL -> {toToken}");
            return new FbdNode { UId = select.UId, Pin = "out", OutputType = destType };
        }

        // A REAL operand needs the CONVERT instruction; every other pair is a Move copy (see
        // FbdVocabulary.ConversionNeedsConvertBox for the live-import probe matrix).
        var box = _network.AddPart();
        if (FbdVocabulary.ConversionNeedsConvertBox(fromToken, toToken))
        {
            box.PartName = "CONVERT";
            box.TemplateValues.Add(new FbdTemplateValue
            {
                Name = "SrcType",
                Type = "Type",
                Value = FbdVocabulary.IecNameForType(fromType) ?? fromType!,
            });
            box.TemplateValues.Add(new FbdTemplateValue
            {
                Name = "DestType",
                Type = "Type",
                Value = destType,
            });
        }
        else
        {
            box.PartName = "Move";
            box.TemplateValues.Add(new FbdTemplateValue { Name = "Card", Type = "Cardinality", Value = "1" });
        }

        WireBooleanEnable(box.UId);
        _network.Wire(source, new FbdNode { UId = box.UId, Pin = "in" });

        _result.Approximations.Add($"inserted {box.PartName} conversion {fromToken} -> {toToken}");
        return new FbdNode
        {
            UId = box.UId,
            Pin = box.PartName == "CONVERT" ? "out" : "out1",
            IsTerminalOutput = true,
            OutputType = destType,
        };
    }

    /// <summary>
    /// Routes a Call output pin through an intermediate Temp access: the pin is wired once into a
    /// write access of a fresh Temp variable, and a per-consumer read access of that Temp is returned.
    /// A read is duplicated per consumer so the Temp never itself fans out. Falls back to the raw pin
    /// (no temp) when the output type is unknown, which lets the caller fall back to SCL rather than
    /// emit a member with no datatype.
    /// </summary>
    private FbdNode RouteCallOutputThroughTemp(FbdNode source, string? fromType)
    {
        var type = source.OutputType ?? fromType;
        var datatype = TempDatatype(type);
        if (datatype is null)
        {
            return source;
        }

        if (!_callOutputTemps.TryGetValue(source.SourceKey, out var name))
        {
            name = NewTempName();
            _callOutputTemps[source.SourceKey] = name;
            _result.TempDeclarations.Add((name, datatype));

            // The write access must sit in flow order: after the producing part and before every
            // consumer that reads the temp. The consumer part is created before its inputs are wired,
            // so a plain append would place the write after the consumer and TIA rejects the network
            // ("The elements must be sorted according to the current flow"). Insert the write (and its
            // source wire) right after the producer instead.
            var producerIndex = _network.PartIndexOf(source.UId);
            var writeAt = producerIndex >= 0 ? producerIndex + 1 : -1;
            var producerWire = _network.LastWireIndexForPart(source.UId);
            var wireAt = producerWire >= 0 ? producerWire + 1 : -1;

            var write = _network.NewAccess(writeAt);
            write.Scope = "LocalVariable";
            write.Components.Add(name);
            _callOutputTempWrites[source.SourceKey] = write;
            _network.Wire(source, new FbdNode { UId = write.UId, IsAccess = true }, wireAt);
        }

        // A fresh read per consumer, kept immediately after the write so it still precedes the
        // consumer part in the parts list.
        var writePart = _callOutputTempWrites[source.SourceKey];
        var writeIndex = _network.PartIndexOf(writePart.UId);
        var read = _network.NewAccess(writeIndex >= 0 ? writeIndex + 1 : -1);
        read.Scope = "LocalVariable";
        read.Components.Add(name);

        // Carry the temp's type onto the read node: the caller replaced its (possibly typed) producer
        // with this access, and without the type it can no longer see that a conversion is needed. A
        // Call output of type Bool assigned to a DInt target then degraded to a bare Move, which TIA
        // rejects ("Data type Bool is not permitted here.") instead of the Sel conversion it needs.
        return new FbdNode { UId = read.UId, IsAccess = true, OutputType = datatype };
    }

    /// <summary>The interface datatype for a Temp: an elementary token, else a bare (unquoted) type name.</summary>
    private static string? TempDatatype(string? type)
    {
        if (string.IsNullOrWhiteSpace(type)) return null;
        var trimmed = type.Trim();
        return FbdVocabulary.ElementaryToken(trimmed) ?? trimmed.Trim('"');
    }

    /// <summary>A Temp name not colliding with any interface member or another Temp.</summary>
    private string NewTempName()
    {
        var index = 1;
        while (true)
        {
            var name = "tmp" + index.ToString(GenerationHelpers.Culture);
            if (!_localNames.Contains(name)
                && !_state.InstanceNames.Contains(name)
                && _state.TempNames.Add(name))
            {
                return name;
            }

            index++;
        }
    }

    /// <summary>A unique instance name per block; the declaration is added to the interface Static section.</summary>
    private string InstanceOf(BlockCall call)
    {
        if (_state.InstanceBySheetKey.TryGetValue(call.SheetKey, out var existing)) return existing;

        var baseName = string.IsNullOrWhiteSpace(call.InstanceName) ? call.TypeName : call.InstanceName;
        var candidate = GenerationHelpers.SanitizeIdentifier(baseName);
        if (string.IsNullOrEmpty(candidate)) candidate = "FB";
        if (!char.IsLetter(candidate[0]) && candidate[0] != '_') candidate = "_" + candidate;

        var typeName = GenerationHelpers.SanitizeIdentifier(call.MappedTypeName ?? call.TypeName);
        if (string.Equals(candidate, typeName, StringComparison.OrdinalIgnoreCase))
        {
            candidate += "_" + GenerationHelpers.SanitizeIdentifier(call.LocalId);
        }

        var name = candidate;
        var suffix = 1;
        while (!_state.InstanceNames.Add(name))
        {
            name = candidate + "_" + suffix.ToString(GenerationHelpers.Culture);
            suffix++;
        }

        _state.InstanceBySheetKey[call.SheetKey] = name;
        _result.InstanceDeclarations.Add((name, call.MappedTypeName ?? call.TypeName));
        return name;
    }
}

/// <summary>Per-block shared state for the FBD renderer: instance statics across all sheets.</summary>
internal sealed class FbdBlockState
{
    public HashSet<string> InstanceNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, string> InstanceBySheetKey { get; } = new(StringComparer.Ordinal);

    /// <summary>Names of the intermediate Temp accesses created to route a Call output into a box pin.</summary>
    public HashSet<string> TempNames { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Writes the block document and its FlgNet bodies.</summary>
internal static class FbdXmlWriter
{
    public static GeneratedFile Write(
        PlcBlock block,
        List<FbdNetworkDocument> networks,
        FbdBlockRenderResult result,
        GenerationContext context,
        BlockType? blockTypeOverride = null,
        string? nameOverride = null,
        int? numberOverride = null)
    {
        // The same renderer produces both a block and, when an OB must host instance statics, the
        // wrapper function block the OB delegates to. The overrides let the second pass be named and
        // typed differently without changing the caller's model.
        var blockType = blockTypeOverride ?? block.BlockType;
        var blockName = nameOverride ?? block.Name;

        var buffer = new TiaXmlDocument.Utf8StringWriter();
        using (var writer = XmlWriter.Create(buffer, TiaXmlDocument.CreateSettings()))
        {
            TiaXmlDocument.WritePrologue(writer, "V16", context.Options.Timestamp ?? DateTimeOffset.UtcNow);
            writer.WriteStartElement(BlockElement(blockType));
            writer.WriteAttributeString("ID", "0");
            writer.WriteStartElement("AttributeList");
            WriteInterface(writer, block, blockType, result.InstanceDeclarations, result.TempDeclarations, result.ReferencedLocals);
            writer.WriteElementString("MemoryLayout", "Optimized");
            writer.WriteElementString("Name", blockName);
            // Number is mandatory for OB.ProgramCycle ("The 'Number' attribute is missing"); a valid
            // program-cycle number is 1 or within 123..32767 ("'8' is not a valid OB number").
            writer.WriteElementString("Number", (numberOverride ?? BlockNumber(block, context)).ToString(GenerationHelpers.Culture));
            writer.WriteElementString("ProgrammingLanguage", "FBD");
            if (blockType == BlockType.OB)
            {
                // An OB document must name its OB kind or TIA rejects the whole import
                // ("The argument 'SecondaryType' is missing"). Every converted Triconex POU runs in the
                // cyclic scan, so it is a program-cycle OB.
                writer.WriteElementString("SecondaryType", "ProgramCycle");
            }

            writer.WriteElementString("SetENOAutomatically", "true");
            writer.WriteEndElement();

            var ids = new TiaXmlDocument.IdAllocator(1);
            writer.WriteStartElement("ObjectList");
            WriteMultilingualText(writer, ids, "Comment", result.BlockComment ?? string.Empty);
            foreach (var network in networks)
            {
                if (network.Network.Parts.Count == 0 && network.Network.Wires.Count == 0) continue;
                WriteNetwork(writer, ids, network);
            }

            WriteMultilingualText(writer, ids, "Title", string.Empty);
            writer.WriteEndElement();
            writer.WriteEndElement();
            TiaXmlDocument.CloseDocument(writer);
        }

        var content = buffer.ToString();
        return new GeneratedFile
        {
            RelativePath = $"Blocks/{GenerationHelpers.SanitizeFileName(blockName)}.xml",
            Content = content,
            Kind = ArtifactKind.Block,
            EmitBom = true,
        };
    }

    private static string BlockElement(BlockType blockType) => blockType switch
    {
        BlockType.FB => "SW.Blocks.FB",
        BlockType.FC => "SW.Blocks.FC",
        _ => "SW.Blocks.OB",
    };

    private static int BlockNumber(PlcBlock block, GenerationContext context)
    {
        var blocks = context.Project.Blocks;
        var index = blocks.IndexOf(block);
        if (index < 0) return 101;
        // Function blocks start at 100. STEP 7 Safety numbers its main safety block FB 1 (with
        // Main_Safety_RTG1_DB as DB 1) as soon as an F-CPU is added to a project, so a converted
        // block numbered 1 would collide with it when the package is imported into the customer's
        // project.
        if (block.BlockType != BlockType.OB) return index + 100;

        // A ProgramCycle OB number must be 1 or within 123..32767; TIA rejects the numbers in
        // between ("'8' is not a valid OB number"). 123..127, however, belong to the fail-safe
        // OBs STEP 7 Safety creates for its F-runtime groups (FOB_RTG1 is OB 123 by default): a
        // standard OB that lands on one of those numbers is compiled as part of the safety
        // program, and every standard block it calls is rejected with WF0018 ("out-of-date
        // element") and WF0050 ("Element is not a safety element"). Keep clear of that range.
        var ordinal = 0;
        for (var i = 0; i <= index; i++)
        {
            if (context.Project.Blocks[i].BlockType == BlockType.OB) ordinal++;
        }

        return ordinal <= 1 ? 1 : 198 + ordinal;
    }

    private static void WriteNetwork(XmlWriter writer, TiaXmlDocument.IdAllocator ids, FbdNetworkDocument network)
    {
        writer.WriteStartElement("SW.Blocks.CompileUnit");
        writer.WriteAttributeString("ID", ids.Next());
        writer.WriteAttributeString("CompositionName", "CompileUnits");
        writer.WriteStartElement("AttributeList");
        writer.WriteStartElement("NetworkSource");
        writer.WriteStartElement("FlgNet", TiaXmlDocument.FlgNetNamespace);
        WriteParts(writer, TiaXmlDocument.FlgNetNamespace, network.Network.Parts);
        WriteWires(writer, TiaXmlDocument.FlgNetNamespace, network.Network.Wires);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteElementString("ProgrammingLanguage", "FBD");
        writer.WriteEndElement();
        writer.WriteStartElement("ObjectList");
        WriteMultilingualText(writer, ids, "Comment", network.CommentText ?? string.Empty);
        WriteMultilingualText(writer, ids, "Title", network.Title ?? string.Empty);
        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteParts(XmlWriter writer, string ns, IReadOnlyList<FbdPart> parts)
    {
        writer.WriteStartElement("Parts");
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case "Access":
                    WriteAccess(writer, ns, part);
                    break;
                case "Call":
                    WriteCall(writer, ns, part);
                    break;
                default:
                    WritePart(writer, ns, part);
                    break;
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteAccess(XmlWriter writer, string ns, FbdPart part)
    {
        writer.WriteStartElement("Access", ns);
        writer.WriteAttributeString("Scope", part.Scope);
        writer.WriteAttributeString("UId", part.UId);
        if (part.Scope == "LiteralConstant")
        {
            writer.WriteStartElement("Constant", ns);
            writer.WriteElementString("ConstantType", part.ConstantType ?? string.Empty);
            writer.WriteElementString("ConstantValue", part.ConstantValue ?? string.Empty);
            writer.WriteEndElement();
        }
        else if (part.Scope == "TypedConstant")
        {
            writer.WriteStartElement("Constant", ns);
            writer.WriteElementString("ConstantValue", part.ConstantValue ?? string.Empty);
            writer.WriteEndElement();
        }
        else
        {
            writer.WriteStartElement("Symbol", ns);
            foreach (var component in part.Components)
            {
                writer.WriteStartElement("Component", ns);
                writer.WriteAttributeString("Name", component);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WritePart(XmlWriter writer, string ns, FbdPart part)
    {
        writer.WriteStartElement("Part", ns);
        writer.WriteAttributeString("Name", part.PartName);
        writer.WriteAttributeString("UId", part.UId);
        if (part.DisabledEno is not null)
        {
            writer.WriteAttributeString("DisabledENO", part.DisabledEno);
        }
        if (part.IsInstance)
        {
            writer.WriteAttributeString("Version", "1.0");
            writer.WriteStartElement("Instance", ns);
            writer.WriteAttributeString("Scope", part.InstanceScope);
            writer.WriteAttributeString("UId", part.InstanceUId);
            writer.WriteStartElement("Component", ns);
            writer.WriteAttributeString("Name", part.InstanceComponent);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        foreach (var value in part.TemplateValues)
        {
            writer.WriteStartElement("TemplateValue", ns);
            writer.WriteAttributeString("Name", value.Name);
            writer.WriteAttributeString("Type", value.Type);
            writer.WriteString(value.Value);
            writer.WriteEndElement();
        }

        if (part.AutomaticSrcType)
        {
            writer.WriteStartElement("AutomaticTyped", ns);
            writer.WriteAttributeString("Name", "SrcType");
            writer.WriteEndElement();
        }

        foreach (var pin in part.NegatedPins)
        {
            writer.WriteStartElement("Negated", ns);
            writer.WriteAttributeString("Name", pin);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteCall(XmlWriter writer, string ns, FbdPart part)
    {
        writer.WriteStartElement("Call", ns);
        writer.WriteAttributeString("UId", part.UId);
        writer.WriteStartElement("CallInfo", ns);
        writer.WriteAttributeString("Name", part.CallName);
        writer.WriteAttributeString("BlockType", part.BlockType);
        if (part.IsInstance)
        {
            writer.WriteStartElement("Instance", ns);
            writer.WriteAttributeString("Scope", part.InstanceScope);
            writer.WriteAttributeString("UId", part.InstanceUId);
            writer.WriteStartElement("Component", ns);
            writer.WriteAttributeString("Name", part.InstanceComponent);
            writer.WriteEndElement();
            writer.WriteEndElement();
        }

        foreach (var parameter in part.Parameters)
        {
            writer.WriteStartElement("Parameter", ns);
            writer.WriteAttributeString("Name", parameter.Name);
            writer.WriteAttributeString("Section", parameter.Section);
            if (!string.IsNullOrWhiteSpace(parameter.Type))
            {
                writer.WriteAttributeString("Type", parameter.Type);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteWires(XmlWriter writer, string ns, IReadOnlyList<FbdWire> wires)
    {
        writer.WriteStartElement("Wires");
        foreach (var wire in wires)
        {
            writer.WriteStartElement("Wire", ns);
            writer.WriteAttributeString("UId", wire.UId);
            WriteWireNode(writer, ns, wire.Source);
            foreach (var sink in wire.Sinks)
            {
                WriteWireNode(writer, ns, sink);
            }

            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteWireNode(XmlWriter writer, string ns, FbdNode node)
    {
        if (node.IsOpen)
        {
            writer.WriteStartElement("OpenCon", ns);
            writer.WriteAttributeString("UId", node.UId);
            writer.WriteEndElement();
            return;
        }

        if (node.IsAccess)
        {
            writer.WriteStartElement("IdentCon", ns);
            writer.WriteAttributeString("UId", node.UId);
            writer.WriteEndElement();
            return;
        }

        writer.WriteStartElement("NameCon", ns);
        writer.WriteAttributeString("UId", node.UId);
        writer.WriteAttributeString("Name", node.Pin ?? string.Empty);
        writer.WriteEndElement();
    }

    private static void WriteInterface(
        XmlWriter writer,
        PlcBlock block,
        BlockType blockType,
        IReadOnlyList<(string Name, string Type)> instances,
        IReadOnlyList<(string Name, string Type)> temps,
        ISet<string>? referencedLocals = null)
    {
        writer.WriteStartElement("Interface");
        writer.WriteStartElement("Sections", TiaXmlDocument.InterfaceNamespace);

        // An OB/FC local is a Temp, but only the locals the networks actually reference may be
        // declared: a wrapped OB's body is a single call to its wrapper FB, so its locals (which the
        // wrapper owns as statics) must not appear, and declaring an FB instance type in Temp is
        // rejected by TIA ("The data type ... is not allowed in this section").
        IEnumerable<Variable> TempLocals() => block.Variables.Where(variable =>
            variable.Direction == VariableDirection.Local
            && (referencedLocals is null || referencedLocals.Contains(variable.Name)));

        switch (blockType)
        {
            case BlockType.OB:
                // An OB has no instance data, so the block's local variables are Temp variables and
                // must be declared in the Temp section: the diagram references them as LocalVariable
                // accesses, which TIA rejects when they are not declared.
                WriteSection(writer, "Input", block.Variables.Where(variable => variable.Direction == VariableDirection.Input), instances: null);
                WriteSection(writer, "Temp", TempLocals(), instances: null, temps: temps);
                WriteSection(writer, "Constant", Enumerable.Empty<Variable>(), instances: null);
                break;

            case BlockType.FC:
                WriteSection(writer, "Input", block.Variables.Where(variable => variable.Direction == VariableDirection.Input), instances: null);
                WriteSection(writer, "Output", block.Variables.Where(variable => variable.Direction == VariableDirection.Output), instances: null);
                WriteSection(writer, "InOut", block.Variables.Where(variable => variable.Direction == VariableDirection.InOut), instances: null);
                WriteSection(writer, "Temp", TempLocals(), instances: null, temps: temps);
                WriteSection(writer, "Constant", Enumerable.Empty<Variable>(), instances: null);
                WriteSection(writer, "Return", Enumerable.Empty<Variable>(), instances: null, returnSection: true);
                break;

            default:
                WriteSection(writer, "Input", block.Variables.Where(variable => variable.Direction == VariableDirection.Input), instances: null);
                WriteSection(writer, "Output", block.Variables.Where(variable => variable.Direction == VariableDirection.Output), instances: null);
                WriteSection(writer, "InOut", block.Variables.Where(variable => variable.Direction == VariableDirection.InOut), instances: null);
                WriteSection(writer, "Static", block.Variables.Where(variable => variable.Direction == VariableDirection.Local), instances);
                WriteSection(writer, "Temp", Enumerable.Empty<Variable>(), instances: null, temps: temps);
                WriteSection(writer, "Constant", Enumerable.Empty<Variable>(), instances: null);
                break;
        }

        writer.WriteEndElement();
        writer.WriteEndElement();
    }

    private static void WriteSection(
        XmlWriter writer,
        string name,
        IEnumerable<Variable> variables,
        IReadOnlyList<(string Name, string Type)>? instances,
        bool returnSection = false,
        IReadOnlyList<(string Name, string Type)>? temps = null)
    {
        writer.WriteStartElement("Section", TiaXmlDocument.InterfaceNamespace);
        writer.WriteAttributeString("Name", name);

        if (returnSection)
        {
            writer.WriteStartElement("Member", TiaXmlDocument.InterfaceNamespace);
            writer.WriteAttributeString("Name", "Ret_Val");
            writer.WriteAttributeString("Datatype", "Void");
            writer.WriteEndElement();
        }

        var resolvedInstances = instances is null
            ? null
            : new HashSet<string>(instances.Select(instance => instance.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var variable in variables)
        {
            if (resolvedInstances is not null && resolvedInstances.Contains(variable.Name)) continue;
            WriteMember(writer, variable.Name, TypeForInterface(variable.EffectiveTypeName), variable.Comment ?? variable.Documentation, variable.InitialValue, name == "Static");
        }

        if (instances is not null)
        {
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (instanceName, instanceType) in instances)
            {
                if (!declared.Add(instanceName)) continue;
                var type = instanceType?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(type)) type = "Void";
                WriteMember(writer, instanceName, type, null, null, isStaticMember: true);
            }
        }

        if (temps is not null)
        {
            var declared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tempName, tempType) in temps)
            {
                if (!declared.Add(tempName)) continue;
                var type = tempType?.Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(type)) type = "Bool";
                WriteMember(writer, tempName, type, null, null, isStaticMember: false);
            }
        }

        writer.WriteEndElement();
    }

    private static void WriteMember(
        XmlWriter writer,
        string name,
        string datatype,
        string? comment,
        string? startValue,
        bool isStaticMember)
    {
        writer.WriteStartElement("Member", TiaXmlDocument.InterfaceNamespace);
        writer.WriteAttributeString("Name", name);
        writer.WriteAttributeString("Datatype", datatype);
        if (isStaticMember)
        {
            writer.WriteStartElement("AttributeList", TiaXmlDocument.InterfaceNamespace);
            WriteBooleanAttribute(writer, "ExternalAccessible", false);
            WriteBooleanAttribute(writer, "ExternalVisible", false);
            WriteBooleanAttribute(writer, "ExternalWritable", false);
            writer.WriteEndElement();
        }

        TiaXmlDocument.WriteMemberComment(writer, TiaXmlDocument.InterfaceNamespace, comment);
        if (!string.IsNullOrWhiteSpace(startValue))
        {
            writer.WriteStartElement("StartValue", TiaXmlDocument.InterfaceNamespace);
            writer.WriteString(startValue);
            writer.WriteEndElement();
        }

        writer.WriteEndElement();
    }

    private static void WriteBooleanAttribute(XmlWriter writer, string name, bool value)
    {
        writer.WriteStartElement("BooleanAttribute", TiaXmlDocument.InterfaceNamespace);
        writer.WriteAttributeString("Name", name);
        writer.WriteAttributeString("SystemDefined", "true");
        writer.WriteString(value ? "true" : "false");
        writer.WriteEndElement();
    }

    private static string TypeForInterface(string? type)
    {
        var trimmed = type?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return "Void";
        if (FbdVocabulary.ElementaryToken(trimmed) is not null) return trimmed;
        if (trimmed.StartsWith('"')) return trimmed;
        return "\"" + trimmed + "\"";
    }

    private static void WriteMultilingualText(XmlWriter writer, TiaXmlDocument.IdAllocator ids, string compositionName, string text)
    {
        writer.WriteStartElement("MultilingualText");
        writer.WriteAttributeString("ID", ids.Next());
        writer.WriteAttributeString("CompositionName", compositionName);
        writer.WriteStartElement("ObjectList");
        writer.WriteStartElement("MultilingualTextItem");
        writer.WriteAttributeString("ID", ids.Next());
        writer.WriteAttributeString("CompositionName", "Items");
        writer.WriteStartElement("AttributeList");
        writer.WriteElementString("Culture", TiaXmlDocument.Culture);
        writer.WriteElementString("Text", text);
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
        writer.WriteEndElement();
    }
}