using System.Collections.Generic;

namespace QuantConnect.Brokerages
{
    /// <summary>
    /// Provides a snapshot of brokerage account summary values.
    /// </summary>
    public interface IAccountSummaryProvider
    {
        /// <summary>
        /// Returns a snapshot copy of the current account summary values.
        /// </summary>
        Dictionary<string, string> GetAccountSummarySnapshot();
    }
}
