namespace Mutate4CSharp;

// Internal installed-producer selection DATA. It does not create native/task
// authority. The invoking owner must bind this to its actual admitted package,
// source/build profile and selected SDK before passing it to capture.
internal sealed record GraphProducerImage(string Path, long Bytes, string Sha256);
internal sealed record ResolvedGraphLoggerSelection(string SdkVersion,
    IReadOnlyList<GraphProducerImage> LoggerClosure, GraphProducerImage CscTask, GraphProducerImage? CscDefinition = null)
{
    private static readonly string[] Names = ["Mutate4CSharp.GraphCapture.dll",
        "Mutate4CSharp.GraphCapture.pdb", "Mutate4CSharp.GraphCapture.deps.json",
        "Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.CSharp.dll"];

    internal IReadOnlyList<ResolvedGraphImagePin> ReadActual(ResolvedGraphInputs inputs)
    {
        inputs.CheckWork();
        if (SdkVersion != "10.0.401" || LoggerClosure.Count != Names.Length)
            throw new EvaluationContractException("Exact supported selected401 logger profile required.");
        var retained = new List<ResolvedGraphImagePin>();
        string? directory = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var image in LoggerClosure)
        {
            inputs.CheckWork();
            var name = System.IO.Path.GetFileName(image.Path);
            if (!Names.Contains(name, StringComparer.Ordinal) || !names.Add(name))
                throw new EvaluationContractException("Complete nonaliased logger deployment required.");
            directory ??= System.IO.Path.GetDirectoryName(image.Path);
            if (directory != System.IO.Path.GetDirectoryName(image.Path))
                throw new EvaluationContractException("Logger dependencies must share its admitted directory.");
            retained.Add(Read(image));
        }
        if (System.IO.Path.GetFileName(CscTask.Path) != "Microsoft.Build.Tasks.CodeAnalysis.dll")
            throw new EvaluationContractException("Exact selected Csc task image required.");
        retained.Add(Read(CscTask));
        if (CscDefinition is null || System.IO.Path.GetFileName(CscDefinition.Path) != "Microsoft.CSharp.Core.targets" ||
            System.IO.Path.GetDirectoryName(CscDefinition.Path) != System.IO.Path.GetDirectoryName(CscTask.Path))
            throw new EvaluationContractException("Exact selected stock Csc definition required; no custom factory fallback.");
        retained.Add(Read(CscDefinition, "import"));
        inputs.CheckWork();
        return retained;

        ResolvedGraphImagePin Read(GraphProducerImage image, string role = "compiler")
        {
            inputs.CheckWork();
            if (!System.IO.Path.IsPathFullyQualified(image.Path) ||
                System.IO.Path.GetFullPath(image.Path) != image.Path ||
                image.Path.IndexOfAny(['|', ';', ',', '\r', '\n']) >= 0 ||
                image.Bytes <= 0 || image.Bytes > ResolvedGraphInputs.MaximumFileBytes ||
                image.Sha256.Length != 64 || image.Sha256.Any(c => !char.IsAsciiHexDigitLower(c)))
                throw new EvaluationContractException("Finite exact original logger image tuple required.");
            var actual = inputs.PinImage(image.Path, role);
            if (actual.Bytes != image.Bytes || actual.Sha256 != image.Sha256)
                throw new SnapshotDivergedException("Selected logger/task deployment bytes changed.");
            return actual;
        }
    }

    internal string LoggerPath => LoggerClosure.Single(image =>
        System.IO.Path.GetFileName(image.Path) == "Mutate4CSharp.GraphCapture.dll").Path;
}
