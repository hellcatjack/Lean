using System;

namespace QuantConnect.Brokerages
{
    /// <summary>
    /// Provides a hook to refresh brokerage holdings when snapshots are stale.
    /// </summary>
    public interface IAccountHoldingsRefresher
    {
        /// <summary>
        /// Triggers a refresh of account holdings.
        /// </summary>
        /// <returns>True if a refresh was initiated.</returns>
        bool RefreshAccountHoldings();
    }
}
