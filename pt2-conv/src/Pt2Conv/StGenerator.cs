using System.Text;
using System.Xml.Linq;

namespace Pt2Conv;

/// <summary>
/// Generates a TriStation .ST container stream from a PLCopen-XML POU.
/// Verified rule: .ST = export body ST CDATA with LF normalized to CRLF, plus a trailing CRLF.
/// </summary>
public static class StGenerator
{
    public static int Run(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: genst <xml> <pouName> <out.ST>"); return 1; }
        var bytes = Generate(args[1], args[2]);
        File.WriteAllBytes(args[3], bytes);
        Console.WriteLine($"Wrote {args[3]} ({bytes.Length:N0} B)");
        return 0;
    }

    public static byte[] Generate(string xmlPath, string pouName)
    {
        var doc = XmlRepair.Load(xmlPath);
        var pou = doc.Descendants().FirstOrDefault(
            e => e.Name.LocalName == "pou" && (string?)e.Attribute("name") == pouName)
            ?? doc.Descendants().FirstOrDefault(
                e => e.Name.LocalName == "pou" && string.Equals((string?)e.Attribute("name"), pouName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"POU '{pouName}' not found in {xmlPath}");

        var st = pou.Descendants().FirstOrDefault(e => e.Name.LocalName == "ST")
            ?? throw new InvalidOperationException($"POU '{pouName}' has no ST body");

        string text = st.Nodes().OfType<XText>().Select(t => t.Value)
            .SingleOrDefault() ?? st.Value;

        text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
        return new UTF8Encoding(false).GetBytes(text + "\r\n");
    }
}