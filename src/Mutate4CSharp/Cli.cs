namespace Mutate4CSharp;

internal static class Cli
{
    public const string HelpText = """
mutate4csharp - isolated mutation testing for a single C# source file

Usage:
  mutate4csharp <file.cs> [options]
  mutate4csharp check (--base REV | --input PATH [--input PATH ...]) [--plan] [--report PATH] [--no-state]
                     [--mutation-id ID --plan-fingerprint SHA256]
  mutate4csharp --help
  mutate4csharp --version

Strict evaluation:
  check                     Use the versioned, fail-closed evaluation contract
  --base REV                Compare a frozen working-tree capture with a direct base commit
  --input PATH              Evaluate an explicit input (repeatable; conflicts with --base)
  --plan                    Describe strict scope; never produces reusable success
  --report PATH             Override the unique JSON report destination
  --no-state                Evaluate normally without publishing discovery/provenance state
  --mutation-id ID          Diagnostic exact-ID rerun (repeatable; requires --plan-fingerprint)
  --plan-fingerprint ID     Refuse a stale exact-ID rerun plan

  Supported Git/configuration contexts run frozen baselines, coverage, and isolated mutants,
  then reduce complete ledgers to PASS, FAIL, or NOT_APPLICABLE. Partial or unstable work is INCOMPLETE.
  Usage, snapshot refusal, and exception paths can stop earlier and may not publish a report.

Modes:
  --scan                    List mutation sites; run no commands and write nothing
  --update-manifest         Refresh only the embedded manifest; run no commands

Selection and coverage:
  --lines 12,18             Restrict mutation to source lines
  --since-last-run          Mutate scopes changed since the embedded manifest
  --mutate-all              Ignore the manifest and mutate every covered site
  --reuse-coverage          Reuse a coverage report instead of collecting coverage
  --coverage-report PATH    OpenCover XML report to reuse

Execution:
  --project PATH            Owning .csproj or .sln/.slnx
  --test-project PATH       Test .csproj or solution to execute
  --root PATH               Repository/solution copy root
  --max-workers N           Concurrent isolated workers (default: 1)
  --timeout-factor N        Mutant timeout / baseline duration (default: 10)
  --mutation-warning N      Warn above this many selected mutants (default: 50)
  --verbose                 Print worker progress
  --help                    Show this help
  --version                 Print package version and embedded source revision

Exit codes: 0 success; 1 usage; 2 red/empty baseline; 3 survivor; 4 infrastructure/error; 5 not applicable.
Custom test commands are intentionally deferred; use --test-project.
""";

    public static (Options? Options, string? Error) Parse(string[] args)
    {
        if (args.Length == 0) return (null, "Exactly one .cs target is required.");
        if (string.Equals(args[0], "check", StringComparison.Ordinal)) return ParseStrict(args[1..]);
        string? target = null, project = null, testProject = null, root = null, coverage = null;
        bool help = false, scan = false, update = false, reuse = false, since = false, all = false, verbose = false;
        HashSet<int>? lines = null; int warning = 50, workers = 1; double factor = 10;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            string Value() { if (++i >= args.Length) throw new ArgumentException($"Missing value for {arg}."); return args[i]; }
            try
            {
                switch (arg)
                {
                    case "--help" or "-h": help = true; break;
                    case "--scan": scan = true; break;
                    case "--update-manifest": update = true; break;
                    case "--reuse-coverage": reuse = true; break;
                    case "--since-last-run": since = true; break;
                    case "--mutate-all": all = true; break;
                    case "--verbose": verbose = true; break;
                    case "--project": project = Value(); break;
                    case "--test-project": testProject = Value(); break;
                    case "--root": root = Value(); break;
                    case "--coverage-report": coverage = Value(); reuse = true; break;
                    case "--lines": lines = Value().Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var n) && n > 0 ? n : throw new ArgumentException("--lines requires positive comma-separated integers.")).ToHashSet(); break;
                    case "--mutation-warning": if (!int.TryParse(Value(), out warning) || warning < 0) throw new ArgumentException("--mutation-warning must be non-negative."); break;
                    case "--max-workers": if (!int.TryParse(Value(), out workers) || workers < 1) throw new ArgumentException("--max-workers must be positive."); break;
                    case "--timeout-factor": if (!double.TryParse(Value(), System.Globalization.CultureInfo.InvariantCulture, out factor) || factor <= 0) throw new ArgumentException("--timeout-factor must be positive."); break;
                    default:
                        if (arg.StartsWith('-')) return (null, $"Unknown option: {arg}");
                        if (target is not null) return (null, "Exactly one .cs target is required.");
                        target = arg; break;
                }
            }
            catch (ArgumentException ex) { return (null, ex.Message); }
        }
        if (help) return (new(null, true, false, false, false, null, false, false, warning, workers, factor, false, null, null, null, null), null);
        if (target is null) return (null, "Exactly one .cs target is required.");
        var conflicts = new List<string>();
        void Conflict(bool condition, string text) { if (condition) conflicts.Add(text); }
        Conflict(scan && (since || all || update || reuse), "--scan conflicts with --since-last-run, --mutate-all, --update-manifest, and coverage reuse.");
        Conflict(update && (lines is not null || since || all || reuse), "--update-manifest conflicts with selection and coverage options.");
        Conflict(lines is not null && (since || all), "--lines conflicts with --since-last-run and --mutate-all.");
        Conflict(since && all, "--since-last-run conflicts with --mutate-all.");
        if (conflicts.Count > 0) return (null, string.Join(' ', conflicts));
        return (new(target, false, scan, update, reuse, lines, since, all, warning, workers, factor, verbose, project, testProject, root, coverage), null);
    }

    private static (Options? Options, string? Error) ParseStrict(string[] args)
    {
        string? baseRevision = null, report = null, planFingerprint = null;
        var inputs = new List<string>();
        var mutationIds = new List<string>();
        var plan = false;
        var noState = false;
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string Value()
            {
                if (++i >= args.Length) throw new ArgumentException($"Missing value for {argument}.");
                return args[i];
            }
            try
            {
                switch (argument)
                {
                    case "--base":
                        if (baseRevision is not null) return (null, "--base may be specified only once.");
                        baseRevision = Value();
                        break;
                    case "--input": inputs.Add(Value()); break;
                    case "--report":
                        if (report is not null) return (null, "--report may be specified only once.");
                        report = Value();
                        break;
                    case "--plan": plan = true; break;
                    case "--no-state": noState = true; break;
                    case "--mutation-id": mutationIds.Add(Value()); break;
                    case "--plan-fingerprint":
                        if (planFingerprint is not null) return (null, "--plan-fingerprint may be specified only once.");
                        planFingerprint = Value();
                        break;
                    case "--help" or "-h":
                        return (new(null, true, false, false, false, null, false, false,
                            50, 1, 10, false, null, null, null, null), null);
                    default:
                        return (null, $"Option {argument} is not valid in strict check mode.");
                }
            }
            catch (ArgumentException ex) { return (null, ex.Message); }
        }
        if ((baseRevision is null) == (inputs.Count == 0))
            return (null, "Strict check requires exactly one selection mode: --base REV or one or more --input PATH values.");
        if (baseRevision is not null && string.IsNullOrWhiteSpace(baseRevision))
            return (null, "--base requires a non-empty revision.");
        if (inputs.Any(string.IsNullOrWhiteSpace)) return (null, "--input requires a non-empty path.");
        if (report is not null && string.IsNullOrWhiteSpace(report)) return (null, "--report requires a non-empty path.");
        if ((mutationIds.Count == 0) != (planFingerprint is null))
            return (null, "Exact-ID reruns require both --mutation-id and --plan-fingerprint.");
        if (mutationIds.Any(id => !MutationIdentity.IsMutationId(id)) || mutationIds.Distinct(StringComparer.Ordinal).Count() != mutationIds.Count)
            return (null, "--mutation-id values must be distinct canonical mutation:v1 IDs.");
        if (planFingerprint is not null && !EvaluationFingerprint.IsFingerprint(planFingerprint))
            return (null, "--plan-fingerprint requires a canonical sha256 fingerprint.");
        if (plan && mutationIds.Count > 0) return (null, "--plan cannot be combined with an exact-ID rerun.");
        var strict = new StrictCheckOptions(plan, baseRevision, inputs, report, Guid.NewGuid().ToString("N"), noState,
            mutationIds, planFingerprint);
        return (new(null, false, false, false, false, null, false, false,
            50, 1, 10, false, null, null, null, null, strict), null);
    }
}
