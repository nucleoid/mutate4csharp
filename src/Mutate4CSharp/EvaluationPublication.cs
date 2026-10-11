using System.Security.Cryptography;

namespace Mutate4CSharp;

internal enum EvaluationPublicationPhase { Report, Discovery, Proven, Eligibility }

internal static class EvaluationPublication
{
    internal static EvaluationReport Publish(string path, IReadOnlyList<string> inputs,
        EvaluationReport report, EvaluationFacts facts, SidecarStore? store,
        string? snapshotId, EvaluationFingerprintMaterial? material, MutationSelectionPlan? plan,
        IReadOnlyList<CoverageProvenance> coverage, Action<EvaluationPublicationPhase>? beforePublication = null)
    {
        ReportWriter.ValidateDestination(path, inputs);
        string? fingerprint = null;
        ProvenEvaluationSidecar? proven = null;
        var phase = EvaluationPublicationPhase.Report;
        try
        {
            // No positive artifact is written until proof eligibility is checked.
            if (store is not null && material is not null)
                fingerprint = EvaluationFingerprint.Compute(material);
            var bytes = ReportWriter.Serialize(report);
            if (report.Outcome == EvaluationOutcome.Pass)
            {
                phase = EvaluationPublicationPhase.Eligibility;
                if (snapshotId is null || material is null || plan is null)
                    throw new EvaluationContractException("PASS requires captured provenance and its issuing selection plan.");
                var eligibleFingerprint = EvaluationFingerprint.ComputeForProven(material);
                if (fingerprint is not null && fingerprint != eligibleFingerprint)
                    throw new EvaluationContractException("Proof eligibility changed the captured evaluation identity.");
                fingerprint = eligibleFingerprint;
                proven = new("2", SidecarRecordKind.Proven, report.RunId, report.GeneratedAtUtc,
                    fingerprint, snapshotId, Hash(bytes), bytes.LongLength, true, coverage, report.Counts);
                SidecarStore.ValidateProvenPublication(proven, report, material, plan);
            }
            if (store is not null)
            {
                if (snapshotId is null || material is null)
                    throw new EvaluationContractException("State publication requires captured provenance.");
                phase = EvaluationPublicationPhase.Discovery;
                fingerprint ??= EvaluationFingerprint.Compute(material);
                store.PrepareForPublication();
                store.InvalidateProven(fingerprint);
            }
            phase = EvaluationPublicationPhase.Report;
            beforePublication?.Invoke(phase);
            ReportWriter.Write(path, report, inputs);
            if (store is null) return report;
            phase = EvaluationPublicationPhase.Discovery;
            beforePublication?.Invoke(phase);
            store.PublishDiscovery(Discovery(report));
            if (proven is not null)
            {
                phase = EvaluationPublicationPhase.Proven;
                beforePublication?.Invoke(phase);
                store.PublishProven(proven, report, material!, plan!);
            }
            return report;
        }
        catch (Exception failure) when (failure is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            var code = phase == EvaluationPublicationPhase.Eligibility ? "PROOF_ELIGIBILITY_FAILED" :
                phase == EvaluationPublicationPhase.Report ? "REPORT_PUBLICATION_FAILED" : "SIDECAR_WRITE_FAILED";
            var condition = new EvaluationReason(code, phase == EvaluationPublicationPhase.Eligibility
                ? "Evaluation did not satisfy complete proof eligibility."
                : phase == EvaluationPublicationPhase.Report
                ? "The current report could not be published safely."
                : "Evaluation state could not be published safely.");
            var conditions = report.IncompleteConditions.Append(condition).Distinct().ToArray();
            var decision = EvaluationReducer.Reduce(facts with { IncompleteConditions = conditions });
            report = report with
            {
                IncompleteConditions = conditions, Reasons = decision.Reasons, Counts = decision.Counts,
                Outcome = decision.Outcome, ExitCode = decision.ExitCode,
                Evidence = report.Evidence.Append(new EvaluationEvidence("PUBLICATION_FAILURE",
                    EvaluationTextBounds.Prefix($"{phase}: {failure.GetType().Name}: {failure.Message}",
                        EvaluationReason.MaxMessageLength))).ToArray()
            };
            Exception? revocationFailure = null;
            if (store is not null && fingerprint is not null)
            {
                try { store.InvalidateProven(fingerprint); }
                catch (Exception revocation) when (revocation is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    revocationFailure = revocation;
                    report = report with { Evidence = report.Evidence.Append(new EvaluationEvidence(
                        "PROVEN_REVOCATION_FAILED", EvaluationTextBounds.Prefix(revocation.Message,
                            EvaluationReason.MaxMessageLength))).ToArray() };
                }
            }
            try { ReportWriter.Write(path, report, inputs); }
            catch (Exception rewrite) when (rewrite is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
            {
                try { ReportWriter.InvalidateCurrentRun(path, report.RunId); }
                catch (Exception invalidation) when (invalidation is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    throw new IOException("Publication failed and the current artifact could not be invalidated.",
                        new AggregateException(failure, rewrite, invalidation));
                }
                throw new IOException("Publication failed; no success-capable current report remains.",
                    new AggregateException(failure, rewrite));
            }
            if (store is not null && fingerprint is not null && snapshotId is not null)
            {
                try { store.ReplaceDiscovery(Discovery(report)); }
                catch (Exception recovery) when (recovery is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
                {
                    // Discovery is never proof. Its old hash cannot match the revised report.
                    report = report with { Evidence = report.Evidence.Append(new EvaluationEvidence(
                        "DISCOVERY_RECOVERY_FAILED", EvaluationTextBounds.Prefix(recovery.Message,
                            EvaluationReason.MaxMessageLength))).ToArray() };
                    ReportWriter.Write(path, report, inputs);
                }
            }
            if (revocationFailure is not null)
                throw new IOException("Publication failed and prior proof could not be revoked; no state from this invocation is usable.",
                    new AggregateException(failure, revocationFailure));
            return report;
        }

        DiscoverySidecar Discovery(EvaluationReport current)
        {
            var bytes = ReportWriter.Serialize(current);
            return new("2", SidecarRecordKind.Discovery, current.RunId, current.GeneratedAtUtc,
                fingerprint!, snapshotId!, current.Outcome, current.ScopePlan.IsComplete,
                current.ScopePlan.Exclusions.Select(item => $"{item.Path}:{item.ReasonCode}").ToArray(),
                Hash(bytes), bytes.LongLength);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
