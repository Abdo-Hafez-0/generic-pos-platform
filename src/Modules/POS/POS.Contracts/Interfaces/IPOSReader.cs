using POS.Contracts.Models;

namespace POS.Contracts.Interfaces;

/// <summary>
/// Read-only POS API (POS read models for the UI and other modules).
/// Implemented by POS.Infrastructure.Services.POSReader.
/// </summary>
public interface IPOSReader
{
    Task<POSSessionResult?> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken = default);

    Task<POSCartResult?> GetCartAsync(Guid cartId, CancellationToken cancellationToken = default);

    /// <summary>The session's open cart, or null when none has been started.</summary>
    Task<POSCartResult?> GetCurrentCartAsync(Guid sessionId, CancellationToken cancellationToken = default);
}
