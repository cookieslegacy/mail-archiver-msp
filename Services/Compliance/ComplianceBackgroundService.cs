using System.Threading.Channels;
using MailArchiver.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace MailArchiver.Services.Compliance
{
    /// <summary>Queue for integrity checks requested from the web interface.</summary>
    public class IntegrityCheckQueue
    {
        private readonly Channel<(int OrganizationId, string RequestedBy)> _channel =
            Channel.CreateBounded<(int, string)>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropWrite });

        public bool Enqueue(int organizationId, string requestedBy) => _channel.Writer.TryWrite((organizationId, requestedBy));

        public ChannelReader<(int OrganizationId, string RequestedBy)> Reader => _channel.Reader;
    }

    /// <summary>
    /// Runs the compliance housekeeping: retention backfill and legacy sealing of existing data,
    /// periodic anchoring of the chain heads, periodic full integrity verification, purging of
    /// expired emails, and integrity checks requested by users.
    /// </summary>
    public class ComplianceBackgroundService : BackgroundService
    {
        private static readonly TimeSpan Tick = TimeSpan.FromMinutes(5);

        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IntegrityCheckQueue _queue;
        private readonly ComplianceOptions _options;
        private readonly IWormStore _worm;
        private readonly ILogger<ComplianceBackgroundService> _logger;
        private readonly Dictionary<int, DateTime> _lastPurge = new();
        private bool _backfillDone;
        // Highest email id checked for a missing original; new emails are checked as they come.
        private int _sealCursor;

        public ComplianceBackgroundService(IServiceScopeFactory scopeFactory, IntegrityCheckQueue queue,
            IOptions<ComplianceOptions> options, IWormStore worm, ILogger<ComplianceBackgroundService> logger)
        {
            _scopeFactory = scopeFactory;
            _queue = queue;
            _options = options.Value;
            _worm = worm;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Let the application finish starting (migrations run during startup).
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

            if (_worm.IsEnabled)
            {
                var health = await _worm.CheckAsync(stoppingToken);
                if (health.Ok)
                    _logger.LogInformation("WORM storage: {Message}", health.Message);
                else
                    _logger.LogError("WORM storage NOT usable, originals cannot be archived: {Message}", health.Message);
            }

            var requests = ProcessRequestsAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunHousekeepingAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Compliance housekeeping failed");
                }

                try
                {
                    await Task.Delay(Tick, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            await requests;
        }

        private async Task ProcessRequestsAsync(CancellationToken stoppingToken)
        {
            try
            {
                await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var verifier = scope.ServiceProvider.GetRequiredService<IntegrityVerificationService>();
                        await verifier.VerifyAsync(request.OrganizationId, request.RequestedBy, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Requested integrity check of organization {OrganizationId} failed", request.OrganizationId);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task RunHousekeepingAsync(CancellationToken ct)
        {
            using (var scope = _scopeFactory.CreateScope())
            {
                var retention = scope.ServiceProvider.GetRequiredService<RetentionService>();

                if (!_backfillDone)
                {
                    int updated;
                    do
                    {
                        updated = await retention.BackfillRetentionAsync(ct);
                    } while (updated > 0 && !ct.IsCancellationRequested);
                    _backfillDone = true;
                }

                if (_options.SealLegacyEmails)
                {
                    // Emails are normally archived together with their original. Older emails,
                    // and emails written by a path that keeps none, are sealed here.
                    var sealedNow = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        var (count, lastId) = await retention.SealLegacyEmailsAsync(_sealCursor, ct);
                        if (lastId == null)
                            break;
                        _sealCursor = lastId.Value;
                        sealedNow += count;
                    }
                    if (sealedNow > 0)
                        _logger.LogInformation("Sealed {Count} emails without original up to id {Id}", sealedNow, _sealCursor);
                }
            }

            List<(int Id, bool AutoPurge)> organizations;
            using (var scope = _scopeFactory.CreateScope())
            {
                var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();
                organizations = (await context.Organizations.AsNoTracking()
                        .Select(o => new { o.Id, o.AutoPurgeExpired })
                        .ToListAsync(ct))
                    .Select(o => (o.Id, o.AutoPurgeExpired)).ToList();
            }

            foreach (var (organizationId, autoPurge) in organizations)
            {
                ct.ThrowIfCancellationRequested();
                using var scope = _scopeFactory.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<MailArchiverDbContext>();

                if (_options.AnchorIntervalHours > 0)
                {
                    var lastAnchor = await context.IntegrityAnchors
                        .Where(a => a.OrganizationId == organizationId)
                        .MaxAsync(a => (DateTime?)a.CreatedAt, ct);
                    if (lastAnchor == null || lastAnchor.Value.ToUniversalTime() < DateTime.UtcNow.AddHours(-_options.AnchorIntervalHours))
                    {
                        try
                        {
                            await scope.ServiceProvider.GetRequiredService<AnchorService>().AnchorAsync(organizationId, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            _logger.LogError(ex, "Anchoring the chains of organization {OrganizationId} failed", organizationId);
                        }
                    }
                }

                if (_options.VerifyIntervalHours > 0)
                {
                    var lastRun = await context.IntegrityCheckRuns
                        .Where(r => r.OrganizationId == organizationId)
                        .MaxAsync(r => (DateTime?)r.StartedAt, ct);
                    if (lastRun == null || lastRun.Value.ToUniversalTime() < DateTime.UtcNow.AddHours(-_options.VerifyIntervalHours))
                    {
                        await scope.ServiceProvider.GetRequiredService<IntegrityVerificationService>()
                            .VerifyAsync(organizationId, "System", ct);
                    }
                }

                if (autoPurge && _options.PurgeIntervalHours > 0
                    && (!_lastPurge.TryGetValue(organizationId, out var last) || last < DateTime.UtcNow.AddHours(-_options.PurgeIntervalHours)))
                {
                    _lastPurge[organizationId] = DateTime.UtcNow;
                    await scope.ServiceProvider.GetRequiredService<RetentionService>()
                        .PurgeExpiredAsync(organizationId, "System", ct);
                }
            }
        }
    }
}
