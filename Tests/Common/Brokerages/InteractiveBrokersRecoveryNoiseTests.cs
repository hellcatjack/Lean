using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Common.Brokerages
{
    [TestFixture]
    public class InteractiveBrokersRecoveryNoiseTests
    {
        [TestCase(2104)]
        [TestCase(2106)]
        [TestCase(2107)]
        [TestCase(2108)]
        [TestCase(2158)]
        public void InformationalFarmStatusMessagesAreClassifiedAsInformational(int code)
        {
            Assert.IsTrue(InteractiveBrokersBrokerage.IsInformationalIbErrorCode(code));
        }

        [TestCase(2103)]
        [TestCase(2105)]
        [TestCase(1100)]
        public void NonInformationalRecoveryTriggerCodesRemainActionable(int code)
        {
            Assert.IsFalse(InteractiveBrokersBrokerage.IsInformationalIbErrorCode(code));
        }

        [Test]
        public void RestoreSubscriptionBatchingSplitsIntoStableChunks()
        {
            var symbols = new List<Symbol>
            {
                Symbols.AAPL,
                Symbols.MSFT,
                Symbols.GOOG,
                Symbols.SPY,
                Symbols.IBM
            };

            var batches = InteractiveBrokersBrokerage.CreateSubscriptionRestoreBatches(symbols, 2)
                .Select(batch => batch.Select(symbol => symbol.Value).ToList())
                .ToList();

            CollectionAssert.AreEqual(new[] { "AAPL", "MSFT" }, batches[0]);
            CollectionAssert.AreEqual(new[] { "GOOG", "SPY" }, batches[1]);
            CollectionAssert.AreEqual(new[] { "IBM" }, batches[2]);
        }
    }
}
