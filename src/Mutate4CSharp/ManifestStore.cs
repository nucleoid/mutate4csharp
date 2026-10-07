using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Mutate4CSharp;

internal static partial class ManifestStore
{
    public const int Version = 1;
    private const string Prefix = "/* mutate4csharp-manifest:v1:";

    [GeneratedRegex(@"(?:\r\n|\r|\n)?/\* mutate4csharp-manifest:v1:(?<data>[A-Za-z0-9+/=]+) \*/\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex FooterRegex();

    public static string Strip(string source) => FooterRegex().Replace(source, string.Empty);

    public static EmbeddedManifest? Read(string source, out bool malformed)
    {
        malformed = false;
        var match = FooterRegex().Match(source);
        if (!match.Success)
        {
            malformed = source.Contains("mutate4csharp-manifest:", StringComparison.Ordinal);
            return null;
        }
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups["data"].Value));
            var value = JsonSerializer.Deserialize<EmbeddedManifest>(json);
            if (value is null || value.Version != Version || value.Scopes is null) { malformed = true; return null; }
            return value;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            malformed = true;
            return null;
        }
    }

    public static string Compose(AnalysisResult analysis, string contextFingerprint)
    {
        var manifest = new EmbeddedManifest(Version, contextFingerprint,
            SourceAnalyzer.Hash(analysis.SourceWithoutManifest),
            analysis.Scopes.Select(x => new ManifestScope(x.Id, x.Kind, x.StartLine, x.EndLine, x.Hash)).ToList());
        var json = JsonSerializer.Serialize(manifest);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var newline = DetectNewline(analysis.SourceWithoutManifest);
        var trailingNewline = analysis.SourceWithoutManifest.EndsWith('\n') || analysis.SourceWithoutManifest.EndsWith('\r');
        // Strip consumes this added separator and therefore restores the analyzed source's
        // trailing-newline behavior instead of normalizing it.
        return analysis.SourceWithoutManifest + newline + Prefix + encoded + " */" + (trailingNewline ? newline : string.Empty);
    }

    public static void AtomicWriteExpected(string path, string expectedOriginal, string replacement, Action? beforeReplace = null)
    {
        var originalBytes = File.ReadAllBytes(path);
        var encoding = DetectEncoding(originalBytes, out var preambleLength);
        var originalText = encoding.GetString(originalBytes, preambleLength, originalBytes.Length - preambleLength);
        if (!string.Equals(originalText, expectedOriginal, StringComparison.Ordinal))
            throw new IOException("Target changed during the run; manifest was not written.");
        var directory = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        File.WriteAllText(temp, replacement, encoding);
        try
        {
            beforeReplace?.Invoke();
            if (!File.ReadAllBytes(path).SequenceEqual(originalBytes))
                throw new IOException("Target changed during the run; manifest was not written.");
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static Encoding DetectEncoding(byte[] bytes, out int preambleLength)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble())) { preambleLength = 3; return new UTF8Encoding(true); }
        if (bytes.AsSpan().StartsWith(Encoding.UTF32.GetPreamble())) { preambleLength = 4; return Encoding.UTF32; }
        var utf32BigEndian = new UTF32Encoding(true, true);
        if (bytes.AsSpan().StartsWith(utf32BigEndian.GetPreamble())) { preambleLength = 4; return utf32BigEndian; }
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble())) { preambleLength = 2; return Encoding.Unicode; }
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble())) { preambleLength = 2; return Encoding.BigEndianUnicode; }
        preambleLength = 0;
        return new UTF8Encoding(false);
    }

    private static string DetectNewline(string source)
    {
        var newline = source.IndexOf('\n');
        if (newline >= 0) return newline > 0 && source[newline - 1] == '\r' ? "\r\n" : "\n";
        return source.Contains('\r') ? "\r" : Environment.NewLine;
    }
}
