using Fiscal.Application.Commands;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fiscal.Infrastructure.Services;

/// <summary>MISS-11a-3: how often the outbox runs (section "Fiscal:Sender").</summary>
public sealed class FiscalSenderOptions
{
    public const string SectionName = "Fiscal:Sender";

    /// <summary>Seconds between two rounds (the first round waits one interval after the start). MISS-11c-3 (owner's decision): every 5 minutes.</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>MISS-11d-4: how long after a sale the outbox sends a document reported right away (Saudi Arabia).</summary>
    public int RightAwayDelaySeconds { get; set; } = 3;

    /// <summary>False stops the background sender (documents are still made and can be sent with "Send now").</summary>
    public bool Enabled { get; set; } = true;
}

/// <summary>
/// MISS-11a-3: the outbox - every interval, sends what waits and checks what was handed over (SendFiscalDocumentsCommandHandler, in its own
/// scope, without a user). Selling never waits for it; a failed round is logged and tried again at the next one.
/// </summary>
internal sealed class FiscalOutboxService(IServiceScopeFactory scopes, FiscalSenderOptions options, ILogger<FiscalOutboxService>? logger = null,
    Fiscal.Application.Abstractions.FiscalOutboxSignal? signal = null) : BackgroundService
{
    private readonly ILogger _logger = logger ?? NullLogger<FiscalOutboxService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        var interval = TimeSpan.FromSeconds(Math.Max(5, options.IntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // MISS-11d-4: the interval, or a moment after a document a part sends right away (its sale's transaction commits first)
                if (signal is null) await Task.Delay(interval, stoppingToken);
                else if (await signal.WaitAsync(interval, stoppingToken)) await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, options.RightAwayDelaySeconds)), stoppingToken);
                using var scope = scopes.CreateScope();
                var summary = await scope.ServiceProvider.GetRequiredService<SendFiscalDocumentsCommandHandler>().ExecuteAsync(force: false, stoppingToken);
                if (summary.Ran)
                    _logger.LogInformation("Electronic receipts: {Sent} handed over, {Accepted} accepted, {Rejected} refused, {NotSent} not sent ({Reason}).",
                        summary.Sent, summary.Accepted, summary.Rejected, summary.NotSent, summary.LastReason ?? "-");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "A round of the electronic receipt outbox failed; the next round tries again.");
            }
        }
    }
}
