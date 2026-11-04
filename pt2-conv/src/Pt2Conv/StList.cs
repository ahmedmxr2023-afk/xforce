using System.Text;

namespace Pt2Conv;

/// <summary>
/// Writes the ST listing TriStation keeps beside an FBD element (the <c>.IST</c> stream).
/// Used to inspect/compare compiler output; the build writes these streams by itself.
/// </summary>
public static class StList
{
    public static int Run(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: stlist <xml> <pouName> [out.IST]");
            return 1;
        }

        string xml = args[1];
        string pou = args[2];
        string outPath = args.Length >= 4 ? args[3] : Path.ChangeExtension(pou, ".IST");

        string text = FbdGenerator.BuildListing(xml, pou);
        File.WriteAllText(outPath, text, new UTF8Encoding(false));
        Console.WriteLine($"{pou}: {text.Length:N0} chars -> {outPath}");
        return 0;
    }
}
