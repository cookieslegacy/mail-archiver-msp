using System.Security.Cryptography.Pkcs;
using System.Text.Json;
using MailArchiver.Data;
using MailArchiver.Models;
using Microsoft.EntityFrameworkCore;

namespace MailArchiver.Services.Compliance
{
    /// <summary>
    /// Verifies the archive of one organization end to end:
    ///   - every link of the originals chain (sequence, previous hash, recomputed chain hash),
    ///   - the content of every original against its SHA-256 (database copy and WORM object),
    ///   - the seals of emails archived before originals were kept,
    ///   - every link of the audit log chain,
    ///   - every anchor against the chains, against its WORM copy and its RFC 3161 time stamp.
    /// The result is stored as an <see cref="IntegrityCheckRun"/> and in the audit log.
    /// </summary>
    public class IntegrityVerificationService
    {
        private const int BatchSize = 200;
        private const int MaxProblems = 200;

        private readonly MailArchiverDbContext _context;
        private readonly IOriginalArchiveService _originals;
        private readonly IWormStore _worm;
        private readonly ILogger<IntegrityVerificationService> _logger;

        public IntegrityVerificationService(MailArchiverDbContext context, IOriginalArchiveService originals,
            IWormStore worm, ILogger<IntegrityVerificationService> logger)
        {
            _context = context;
            _originals = originals;
            _worm = worm;
            _logger = logger;
        }

        private sealed class Report
        {
            public List<object> Problems { get; } = new();
            public void Add(string chain, long? seq, string problem, object? detail = null)
            {
                if (Problems.Count < MaxProblems)
                    Problems.Add(new { chain, seq, problem, detail });
            }
        }

        public async Task<IntegrityCheckRun> VerifyAsync(int organizationId, string triggeredBy, CancellationToken cancellationToken = default)
        {
            var run = new IntegrityCheckRun
            {
                OrganizationId = organizationId,
                StartedAt = DateTime.UtcNow,
                TriggeredBy = triggeredBy,
                Status = IntegrityCheckStatus.Running
            };
            _context.IntegrityCheckRuns.Add(run);
            await _context.SaveChangesAsync(cancellationToken);

            var report = new Report();
            try
            {
                await VerifyOriginalsAsync(organizationId, run, report, cancellationToken);
                await VerifyAuditLogAsync(organizationId, run, report, cancellationToken);
                await VerifyAnchorsAsync(organizationId, run, report, cancellationToken);

                run.Status = run.OriginalsFailed + run.AuditEntriesFailed + run.AnchorsFailed == 0
                    ? IntegrityCheckStatus.Ok
                    : IntegrityCheckStatus.Failed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Integrity check of organization {OrganizationId} broke off", organizationId);
                report.Add("run", null, "The check could not be completed: " + ex.Message);
                run.Status = IntegrityCheckStatus.Error;
            }

            run.FinishedAt = DateTime.UtcNow;
            run.Problems = report.Problems.Count == 0 ? null : JsonSerializer.Serialize(report.Problems);
            _context.AccessLogs.Add(new AccessLog
            {
                Username = triggeredBy,
                Type = AccessLogType.IntegrityCheck,
                Timestamp = DateTime.UtcNow,
                OrganizationId = organizationId,
                SearchParameters = $"Integrity check {run.Status}: originals {run.OriginalsChecked} checked / {run.OriginalsFailed} failed, " +
                    $"audit entries {run.AuditEntriesChecked} / {run.AuditEntriesFailed}, anchors {run.AnchorsChecked} / {run.AnchorsFailed}, " +
                    $"chain head #{run.OriginalsHeadSeq} {run.OriginalsHeadHash}"
            });
            await _context.SaveChangesAsync(CancellationToken.None);

            _logger.LogInformation("Integrity check of organization {OrganizationId}: {Status} ({Checked} originals, {Failed} failed)",
                organizationId, run.Status, run.OriginalsChecked, run.OriginalsFailed);
            return run;
        }

        private async Task VerifyOriginalsAsync(int organizationId, IntegrityCheckRun run, Report report, CancellationToken ct)
        {
            long expectedSeq = 1;
            var previousHash = IntegrityChain.GenesisHash;
            long lastSeq = 0;

            while (true)
            {
                var batch = await _context.ArchivedEmailOriginals.AsNoTracking()
                    .Where(o => o.OrganizationId == organizationId && o.ChainSeq > lastSeq)
                    .OrderBy(o => o.ChainSeq)
                    .Take(BatchSize)
                    .ToListAsync(ct);
                if (batch.Count == 0)
                    break;

                foreach (var original in batch)
                {
                    ct.ThrowIfCancellationRequested();
                    run.OriginalsChecked++;
                    var ok = true;

                    if (original.ChainSeq != expectedSeq)
                    {
                        report.Add("originals", original.ChainSeq, $"Sequence gap: expected #{expectedSeq}");
                        ok = false;
                    }
                    if (original.PrevChainHash != previousHash)
                    {
                        report.Add("originals", original.ChainSeq, "Link to the previous entry is broken");
                        ok = false;
                    }
                    if (IntegrityChain.OriginalChainHash(original) != original.ChainHash)
                    {
                        report.Add("originals", original.ChainSeq, "Chain hash does not match the entry", new { original.ArchivedEmailId });
                        ok = false;
                    }

                    if (original.PurgedAt == null)
                        ok &= await VerifyContentAsync(original, report, ct);

                    if (!ok)
                        run.OriginalsFailed++;

                    expectedSeq = original.ChainSeq + 1;
                    previousHash = original.ChainHash;
                    lastSeq = original.ChainSeq;
                    run.OriginalsHeadSeq = original.ChainSeq;
                    run.OriginalsHeadHash = original.ChainHash;
                }
            }
        }

        private async Task<bool> VerifyContentAsync(ArchivedEmailOriginal original, Report report, CancellationToken ct)
        {
            if (original.Source == OriginalSources.LegacySeal)
            {
                var email = await _context.ArchivedEmails.AsNoTracking()
                    .FirstOrDefaultAsync(e => e.Id == original.ArchivedEmailId, ct);
                if (email == null)
                {
                    report.Add("originals", original.ChainSeq, "Sealed email no longer exists although it was not purged",
                        new { original.ArchivedEmailId });
                    return false;
                }
                var seal = IntegrityChain.LegacySeal(email, await LegacySealing.AttachmentHashesAsync(_context, email.Id, ct));
                if (seal != original.ContentSha256)
                {
                    report.Add("originals", original.ChainSeq, "Stored fields of the email changed after sealing",
                        new { original.ArchivedEmailId });
                    return false;
                }
                return true;
            }

            var ok = true;
            if (original.RawMime != null)
            {
                if (IntegrityChain.Sha256Hex(original.RawMime) != original.ContentSha256 || original.RawMime.LongLength != original.SizeBytes)
                {
                    report.Add("originals", original.ChainSeq, "Original in the database does not match its hash",
                        new { original.ArchivedEmailId });
                    ok = false;
                }
            }

            if (!string.IsNullOrEmpty(original.StorageKey))
            {
                if (!_worm.IsEnabled)
                {
                    report.Add("originals", original.ChainSeq, "Original is in WORM storage, which is not configured",
                        new { original.ArchivedEmailId, original.StorageKey });
                    return false;
                }
                try
                {
                    var bytes = await _worm.GetAsync(original.StorageKey, original.StorageVersionId, ct);
                    if (IntegrityChain.Sha256Hex(bytes) != original.ContentSha256)
                    {
                        report.Add("originals", original.ChainSeq, "Original in WORM storage does not match its hash",
                            new { original.ArchivedEmailId, original.StorageKey });
                        ok = false;
                    }
                }
                catch (Exception ex)
                {
                    report.Add("originals", original.ChainSeq, "Original in WORM storage cannot be read: " + ex.Message,
                        new { original.ArchivedEmailId, original.StorageKey });
                    ok = false;
                }
            }
            else if (original.RawMime == null)
            {
                report.Add("originals", original.ChainSeq, "Original has neither bytes nor a storage location",
                    new { original.ArchivedEmailId });
                ok = false;
            }
            return ok;
        }

        private async Task VerifyAuditLogAsync(int organizationId, IntegrityCheckRun run, Report report, CancellationToken ct)
        {
            long expectedSeq = 1;
            var previousHash = IntegrityChain.GenesisHash;
            long lastSeq = 0;

            // Entries pruned after their retention: the chain continues from the recorded cut.
            var prune = await _context.AuditLogPrunes.AsNoTracking()
                .Where(p => p.OrganizationId == organizationId)
                .OrderByDescending(p => p.PrunedThroughSeq)
                .FirstOrDefaultAsync(ct);
            if (prune != null)
            {
                expectedSeq = prune.PrunedThroughSeq + 1;
                previousHash = prune.PrunedThroughHash;
                lastSeq = prune.PrunedThroughSeq;
                report.Add("audit", prune.PrunedThroughSeq,
                    $"{prune.EntriesRemoved} entries up to #{prune.PrunedThroughSeq} were pruned on {prune.PrunedAt:u} (older than {prune.OlderThan:d})");
            }

            while (true)
            {
                var batch = await _context.AccessLogs.AsNoTracking()
                    .Where(l => l.OrganizationId == organizationId && l.ChainSeq > lastSeq)
                    .OrderBy(l => l.ChainSeq)
                    .Take(1000)
                    .ToListAsync(ct);
                if (batch.Count == 0)
                    break;

                foreach (var entry in batch)
                {
                    run.AuditEntriesChecked++;
                    var ok = entry.ChainSeq == expectedSeq
                             && entry.PrevChainHash == previousHash
                             && IntegrityChain.AuditChainHash(entry) == entry.ChainHash;
                    if (!ok)
                    {
                        run.AuditEntriesFailed++;
                        report.Add("audit", entry.ChainSeq, entry.ChainSeq != expectedSeq
                            ? $"Sequence gap: expected #{expectedSeq}"
                            : "Audit entry does not match its chain hash", new { entry.Id });
                    }
                    expectedSeq = entry.ChainSeq + 1;
                    previousHash = entry.ChainHash ?? string.Empty;
                    lastSeq = entry.ChainSeq;
                }
            }
        }

        private async Task VerifyAnchorsAsync(int organizationId, IntegrityCheckRun run, Report report, CancellationToken ct)
        {
            var anchors = await _context.IntegrityAnchors.AsNoTracking()
                .Where(a => a.OrganizationId == organizationId)
                .OrderBy(a => a.Id)
                .ToListAsync(ct);
            var prunedThrough = await _context.AuditLogPrunes.AsNoTracking()
                .Where(p => p.OrganizationId == organizationId)
                .OrderByDescending(p => p.PrunedThroughSeq)
                .FirstOrDefaultAsync(ct);

            foreach (var anchor in anchors)
            {
                run.AnchorsChecked++;
                var ok = true;

                // Anchors of pruned audit entries can only be checked against the prune record.
                if (anchor.ChainKind == IntegrityChainKinds.Audit && prunedThrough != null && anchor.ChainSeq <= prunedThrough.PrunedThroughSeq)
                {
                    if (anchor.ChainSeq == prunedThrough.PrunedThroughSeq && anchor.ChainHash != prunedThrough.PrunedThroughHash)
                    {
                        report.Add("anchor", anchor.ChainSeq, $"The prune record does not match the audit anchor of {anchor.CreatedAt:u}");
                        run.AnchorsFailed++;
                    }
                    continue;
                }

                string? chainHash = anchor.ChainKind == IntegrityChainKinds.Audit
                    ? await _context.AccessLogs.AsNoTracking()
                        .Where(l => l.OrganizationId == organizationId && l.ChainSeq == anchor.ChainSeq)
                        .Select(l => l.ChainHash).FirstOrDefaultAsync(ct)
                    : await _context.ArchivedEmailOriginals.AsNoTracking()
                        .Where(o => o.OrganizationId == organizationId && o.ChainSeq == anchor.ChainSeq)
                        .Select(o => o.ChainHash).FirstOrDefaultAsync(ct);

                if (chainHash != anchor.ChainHash)
                {
                    report.Add("anchor", anchor.ChainSeq, $"The {anchor.ChainKind} chain no longer matches the anchor of {anchor.CreatedAt:u}");
                    ok = false;
                }

                var document = AnchorDocument.Build(anchor);
                if (!string.IsNullOrEmpty(anchor.StorageKey))
                {
                    if (!_worm.IsEnabled)
                    {
                        report.Add("anchor", anchor.ChainSeq, "Anchor is in WORM storage, which is not configured");
                        ok = false;
                    }
                    else
                    {
                        try
                        {
                            var stored = await _worm.GetAsync(anchor.StorageKey, anchor.StorageVersionId, ct);
                            if (!stored.AsSpan().SequenceEqual(document))
                            {
                                report.Add("anchor", anchor.ChainSeq, "Anchor in the database differs from its WORM copy");
                                ok = false;
                            }
                        }
                        catch (Exception ex)
                        {
                            report.Add("anchor", anchor.ChainSeq, "Anchor in WORM storage cannot be read: " + ex.Message);
                            ok = false;
                        }
                    }
                }

                if (anchor.TimestampToken != null)
                {
                    if (!Rfc3161TimestampToken.TryDecode(anchor.TimestampToken, out var token, out _)
                        || !token.VerifySignatureForData(document, out _))
                    {
                        report.Add("anchor", anchor.ChainSeq, "RFC 3161 time stamp does not match the anchor");
                        ok = false;
                    }
                }

                if (!ok)
                    run.AnchorsFailed++;
            }
        }
    }
}
