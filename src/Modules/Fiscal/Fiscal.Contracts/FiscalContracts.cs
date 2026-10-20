namespace Fiscal.Contracts.Models
{
    /// <summary>The tax authority whose electronic receipts the installation issues (MISS-11). None = electronic receipts are off.</summary>
    public enum FiscalCountryContract
    {
        None = 0,
        Egypt = 1,
        SaudiArabia = 2,
    }
}

namespace Fiscal.Contracts.Interfaces
{
    using Fiscal.Contracts.Models;

    /// <summary>
    /// MISS-11a: what other modules may know of the fiscal setup. Implemented by Fiscal.Infrastructure; the Fiscal module is optional, so
    /// consumers resolve it as optional.
    /// </summary>
    public interface IFiscalSetupReader
    {
        /// <summary>The chosen country (None when electronic receipts are off or nothing was set up).</summary>
        Task<FiscalCountryContract> GetCountryAsync(CancellationToken cancellationToken = default);
    }
}
