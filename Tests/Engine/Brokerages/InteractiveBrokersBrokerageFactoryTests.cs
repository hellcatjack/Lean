using System.Linq;
using NUnit.Framework;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Interfaces;
using QuantConnect.Util;

namespace QuantConnect.Tests.Engine.Brokerages
{
    [TestFixture]
    public class InteractiveBrokersBrokerageFactoryTests
    {
        [Test]
        public void ComposerFindsInteractiveBrokersFactory()
        {
            var factories = Composer.Instance.GetExportedValues<IBrokerageFactory>();
            Assert.IsTrue(factories.Any(f => f.BrokerageType == typeof(InteractiveBrokersBrokerage)));
        }
    }
}
