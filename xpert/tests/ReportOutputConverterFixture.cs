using System;
using System.IO;

// A process-level contract fixture, not a PDF renderer or a hardware program.
internal static class ReportOutputConverterFixture
{
    static int Main(string[] args)
    {
        string input = args[args.Length - 1];
        int option = Array.IndexOf(args, "--outdir");
        string content = Path.GetFileName(input).StartsWith("invalid") ? "not a PDF" :
            Path.GetFileName(input).StartsWith("truncated") ? "%PDF-1.4\nmissing trailer" : "%PDF-1.4\n% synthetic envelope fixture, not rendered content\n%%EOF\n";
        File.WriteAllText(Path.Combine(args[option + 1], Path.GetFileNameWithoutExtension(input) + ".pdf"), content);
        return 0;
    }
}
