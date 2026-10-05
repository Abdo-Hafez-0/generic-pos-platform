using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Platform.Application.Abstractions.Hardware;
using Platform.Core.Results;
using POS.Application.Devices;
using POS.Contracts.Interfaces;
using POS.Contracts.Models;

namespace POS.Infrastructure.Services;

/// <summary>Implements IPOSDevices by wrapping the device handlers. Hardware problems become failed results and log lines, never exceptions.</summary>
internal sealed class POSDevices(
    PrintReceiptCommandHandler printReceiptHandler,
    OpenCashDrawerCommandHandler openDrawerHandler,
    PrintProductLabelCommandHandler printLabelHandler,
    ReadWeightQueryHandler readWeightHandler,
    GetDeviceStatusQueryHandler statusHandler,
    ILogger<POSDevices>? logger = null) : IPOSDevices
{
    private readonly ILogger _logger = logger ?? NullLogger<POSDevices>.Instance;

    public async Task<POSOperationResult> PrintReceiptAsync(Guid cartId, CancellationToken cancellationToken = default)
        => ToOperation("print receipt", await printReceiptHandler.HandleAsync(new PrintReceiptCommand(cartId), cancellationToken));

    public async Task<POSOperationResult> OpenCashDrawerAsync(CancellationToken cancellationToken = default)
        => ToOperation("open cash drawer", await openDrawerHandler.HandleAsync(cancellationToken));

    public async Task<POSOperationResult> PrintProductLabelAsync(string productCode, int copies = 1, CancellationToken cancellationToken = default)
        => ToOperation("print label", await printLabelHandler.HandleAsync(new PrintProductLabelCommand(productCode, copies), cancellationToken));

    public async Task<POSWeightResult> ReadWeightAsync(CancellationToken cancellationToken = default)
    {
        var result = await readWeightHandler.HandleAsync(cancellationToken);
        if (result.IsFailure)
        {
            _logger.LogWarning("Reading the scale failed: {Error}", result.Error);
            return POSWeightResult.Failure(result.Error.Code, result.Error.Description);
        }

        var reading = result.Value;
        return POSWeightResult.Success(reading.Value, reading.Unit.ToString(), reading.IsStable, reading.Kilograms);
    }

    public Task<IReadOnlyList<POSDeviceStatusResult>> GetDeviceStatusAsync(CancellationToken cancellationToken = default)
        => statusHandler.HandleAsync(cancellationToken);

    private POSOperationResult ToOperation(string operation, Result result)
    {
        if (result.IsSuccess)
            return POSOperationResult.Success();

        if (!HardwareErrors.IsNotConfigured(result.Error))
            _logger.LogWarning("Could not {Operation}: {Error}", operation, result.Error);

        return POSOperationResult.Failure(result.Error.Code, result.Error.Description);
    }
}
