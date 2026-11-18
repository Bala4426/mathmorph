// OoxmlValidator - read-only Open XML schema/semantic validation of a .docx.
// Usage: OoxmlValidator.exe <file.docx>
// stdout: one JSON object per validation error per line (nothing else).
// stderr: summary / diagnostics.
// Exit: 0 = no errors, 1 = errors found, 2 = cannot open / other failure.
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;

internal static class Program
{
    private static int Main(string[] args)
    {
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        try
        {
            if (args.Length != 1)
            {
                Console.Error.WriteLine("usage: OoxmlValidator.exe <file.docx>");
                return 2;
            }
            string path = Path.GetFullPath(args[0]);
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"error: file not found: {path}");
                return 2;
            }

            FileFormatVersions version = NewestFormatVersion();
            int count = 0;
            var jsonOpts = new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, Indented = false };

            // Read-only at the OS level (FileAccess.Read) AND at the SDK level (isEditable: false).
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var doc = WordprocessingDocument.Open(fs, false))
            {
                var validator = new OpenXmlValidator(version) { MaxNumberOfErrors = 0 }; // 0 = unlimited
                foreach (ValidationErrorInfo e in validator.Validate(doc))
                {
                    count++;
                    using var ms = new MemoryStream();
                    using (var w = new Utf8JsonWriter(ms, jsonOpts))
                    {
                        w.WriteStartObject();
                        w.WriteString("part", e.Part?.Uri?.ToString());
                        w.WriteString("id", e.Id);
                        w.WriteString("description", e.Description);
                        w.WriteString("path", e.Path?.XPath);
                        w.WriteString("type", e.ErrorType.ToString());
                        w.WriteEndObject();
                    }
                    stdout.WriteLine(Encoding.UTF8.GetString(ms.ToArray()));
                }
            }
            stdout.Flush();
            Console.Error.WriteLine($"{Path.GetFileName(path)}: {count} error(s) [validator {version}]");
            return count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            try { stdout.Flush(); } catch { }
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    // Highest single (non-combined) FileFormatVersions value available in the SDK.
    private static FileFormatVersions NewestFormatVersion()
    {
        FileFormatVersions best = FileFormatVersions.Office2019;
        foreach (FileFormatVersions v in Enum.GetValues(typeof(FileFormatVersions)))
        {
            int n = (int)v;
            if (n > 0 && (n & (n - 1)) == 0 && n > (int)best) best = v;
        }
        return best;
    }
}
