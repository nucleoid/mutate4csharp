using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Mutate4CSharp;

internal static class ProjectLocator
{
    public static ProjectContext Resolve(Options options, bool requireTestProject = true)
    {
        var target = Path.GetFullPath(options.Target!);
        if (!File.Exists(target) || !target.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Target must be one existing .cs file.");
        if (IsTestPath(target)) throw new ArgumentException("Test source files cannot be mutation targets.");

        var project = options.Project is null ? FindOwningProject(target) : ResolveExisting(options.Project);
        var testProject = options.TestProject is null
            ? (requireTestProject ? FindTestProject(project) : project)
            : ResolveExisting(options.TestProject);
        var root = options.Root is null ? FindRoot(project) : Path.GetFullPath(options.Root);
        root = Path.TrimEndingDirectorySeparator(root);
        if (!Directory.Exists(root)) throw new ArgumentException("--root must be an existing directory.");
        EnsureWithin(target, root, "Target");
        EnsureWithin(project, root, "Project");
        EnsureWithin(testProject, root, "Test project");
        ValidateSelectedPath(target, root, "Target");
        ValidateSelectedPath(project, root, "Project");
        ValidateSelectedPath(testProject, root, "Test project");
        ValidateTree(root);
        ValidateProjectReferences(root);
        return new(root, project, testProject, Path.GetRelativePath(root, target), Path.GetRelativePath(Environment.CurrentDirectory, target));
    }

    private static string FindOwningProject(string target)
    {
        for (var dir = new DirectoryInfo(Path.GetDirectoryName(target)!); dir is not null; dir = dir.Parent)
        {
            var projects = dir.GetFiles("*.csproj", SearchOption.TopDirectoryOnly);
            if (projects.Length == 1) return projects[0].FullName;
            if (projects.Length > 1) throw new ArgumentException($"Multiple projects found in {dir.FullName}; specify --project.");
        }
        throw new ArgumentException("No owning .csproj found; specify --project.");
    }

    private static string FindTestProject(string project)
    {
        var root = FindRoot(project);
        var candidates = EnumerateFilesSafe(root, "*.csproj")
            .Where(IsTestProject).OrderBy(x => x, StringComparer.Ordinal).ToList();
        if (candidates.Count == 1) return candidates[0];
        var solutions = Directory.EnumerateFiles(root, "*.sln*", SearchOption.TopDirectoryOnly).OrderBy(x => x).ToList();
        if (solutions.Count == 1) return solutions[0];
        if (candidates.Count == 0) throw new ArgumentException("No test project found; specify --test-project.");
        throw new ArgumentException("Multiple test projects found; specify --test-project or a solution.");
    }

    private static bool IsTestProject(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        if (name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)) return true;
        try
        {
            var text = File.ReadAllText(path);
            return text.Contains("Microsoft.NET.Test.Sdk", StringComparison.OrdinalIgnoreCase) || text.Contains("<IsTestProject>true", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
    }

    private static string FindRoot(string project)
    {
        var start = File.Exists(project) ? Path.GetDirectoryName(project)! : project;
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")) || File.Exists(Path.Combine(dir.FullName, ".git")) || dir.GetFiles("*.sln*").Length > 0) return dir.FullName;
        }
        return start;
    }

    private static string ResolveExisting(string value)
    {
        var path = Path.GetFullPath(value);
        if (!File.Exists(path)) throw new ArgumentException($"Path does not exist: {value}");
        return path;
    }

    private static bool IsTestPath(string target)
    {
        var parts = target.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(x => x.Equals("test", StringComparison.OrdinalIgnoreCase) || x.Equals("tests", StringComparison.OrdinalIgnoreCase)) ||
               Path.GetFileNameWithoutExtension(target).EndsWith("Tests", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureWithin(string path, string root, string label)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new ArgumentException($"{label} is outside --root.");
    }

    private static void ValidateTree(string root)
    {
        if (HasReparsePointInPath(root))
            throw new ArgumentException($"Symlink/reparse-point root is not allowed: {root}");
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsExcluded(entry) || IsSecret(entry)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new ArgumentException($"Symlink/reparse-point escape risk: {entry}");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
    }

    private static void ValidateProjectReferences(string root)
    {
        foreach (var project in EnumerateFilesSafe(root, "*.csproj"))
        {
            XDocument doc;
            try { doc = XDocument.Load(project); } catch { continue; }
            foreach (var reference in doc.Descendants().Where(x => x.Name.LocalName == "ProjectReference"))
            {
                var include = reference.Attribute("Include")?.Value;
                if (string.IsNullOrWhiteSpace(include)) continue;
                if (include.Contains("$(", StringComparison.Ordinal) || include.IndexOfAny(['*', '?']) >= 0)
                    throw new ArgumentException($"ProjectReference cannot be proven internal: {include}");
                var platformPath = include.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var resolved = Path.GetFullPath(platformPath, Path.GetDirectoryName(project)!);
                EnsureWithin(resolved, root, "ProjectReference");
            }
        }
    }

    private static void ValidateSelectedPath(string path, string root, string label)
    {
        if (IsExcluded(path) || IsSecret(path))
            throw new ArgumentException($"{label} is inside an excluded or secret path.");
        if (HasReparsePointInPath(path))
            throw new ArgumentException($"{label} uses a symlink/reparse-point path: {path}");
    }

    internal static IEnumerable<string> EnumerateDirectoriesSafe(string root)
    {
        foreach (var entry in EnumerateSafe(root, includeDirectories: true, searchPattern: null)) yield return entry;
    }

    internal static IEnumerable<string> EnumerateFilesSafe(string root, string searchPattern = "*")
    {
        foreach (var entry in EnumerateSafe(root, includeDirectories: false, searchPattern)) yield return entry;
    }

    private static IEnumerable<string> EnumerateSafe(string root, bool includeDirectories, string? searchPattern)
    {
        if (HasReparsePointInPath(root)) yield break;
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (IsExcluded(entry) || IsSecret(entry)) continue;
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push(entry);
                    if (includeDirectories) yield return entry;
                }
                else if (!includeDirectories && (searchPattern is null || FileSystemName.MatchesSimpleExpression(searchPattern, Path.GetFileName(entry), OperatingSystem.IsWindows())))
                {
                    yield return entry;
                }
            }
        }
    }

    private static bool HasReparsePointInPath(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true;
            var parent = Path.GetDirectoryName(current);
            if (string.Equals(parent, current, StringComparison.Ordinal)) break;
        }
        return false;
    }

    internal static bool IsExcluded(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] excluded = [".git", "bin", "obj", "TestResults", ".mutate4csharp", ".vs", ".idea"];
        return parts.Any(x => excluded.Contains(x, StringComparer.OrdinalIgnoreCase));
    }

    public static string ContextFingerprint(ProjectContext context, Options? options = null)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var targetFull = Path.GetFullPath(Path.Combine(context.Root, context.TargetRelativePath));
        var identity = string.Join("\0", NormalizeSelection(context.Project, context.Root), NormalizeSelection(context.TestProject, context.Root),
            options?.ReuseCoverage.ToString() ?? string.Empty,
            options?.CoverageReport is null ? string.Empty : NormalizeSelection(Path.GetFullPath(options.CoverageReport), context.Root),
            options?.TimeoutFactor.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
        hash.AppendData(Encoding.UTF8.GetBytes(identity + "\0"));
        if (options?.ReuseCoverage == true)
        {
            var reports = options.CoverageReport is null
                ? EnumerateFilesSafe(context.Root, "*.xml").Where(x => Path.GetFileName(x).Contains("coverage", StringComparison.OrdinalIgnoreCase))
                : [Path.GetFullPath(options.CoverageReport)];
            foreach (var report in reports.OrderBy(x => x, StringComparer.Ordinal))
            {
                hash.AppendData(Encoding.UTF8.GetBytes("coverage:" + NormalizeSelection(report, context.Root) + "\0"));
                if (File.Exists(report)) AppendFile(hash, report);
                else hash.AppendData(Encoding.UTF8.GetBytes("missing\0"));
            }
        }
        foreach (var file in EnumerateFilesSafe(context.Root)
                     .Where(x => IsContextInput(x) && !Path.GetFullPath(x).Equals(targetFull, StringComparison.Ordinal))
                     .OrderBy(x => Path.GetRelativePath(context.Root, x), StringComparer.Ordinal))
        {
            var rel = Path.GetRelativePath(context.Root, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(rel + "\0"));
            AppendFile(hash, file);
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendFile(IncrementalHash hash, string path)
    {
        using var stream = File.OpenRead(path);
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer)) > 0) hash.AppendData(buffer.AsSpan(0, read));
    }

    private static string NormalizeSelection(string path, string root)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, full);
        return relative.Replace('\\', '/');
    }

    private static bool IsContextInput(string path)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var name = Path.GetFileName(path);
        return extension is ".cs" or ".csproj" or ".props" or ".targets" or ".json" or ".config" or ".runsettings" ||
               name.Equals("global.json", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("NuGet.Config", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsSecret(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string[] secretDirectories = [".ssh", ".aws", ".azure", ".gnupg", ".kube"];
        if (parts.Any(x => secretDirectories.Contains(x, StringComparer.OrdinalIgnoreCase))) return true;

        var name = Path.GetFileName(path);
        return name.Equals(".env", StringComparison.OrdinalIgnoreCase) || name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".p12", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("secrets.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".user", StringComparison.OrdinalIgnoreCase);
    }
}
