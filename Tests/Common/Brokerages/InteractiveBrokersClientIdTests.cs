using NUnit.Framework;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Configuration;

namespace QuantConnect.Tests.Common.Brokerages
{
    [TestFixture]
    public class InteractiveBrokersClientIdTests
    {
        [Test]
        public void ReadsClientIdFromConfig()
        {
            Config.Set("ib-client-id", "1234");
            Assert.AreEqual(1234, InteractiveBrokersBrokerage.ResolveClientId());
        }
    }
}
