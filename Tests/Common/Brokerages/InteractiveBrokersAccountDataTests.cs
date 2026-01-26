using NUnit.Framework;
using QuantConnect.Brokerages.InteractiveBrokers;

namespace QuantConnect.Tests.Common.Brokerages
{
    [TestFixture]
    public class InteractiveBrokersAccountDataTests
    {
        [Test]
        public void GetAccountSummarySnapshotReturnsCopy()
        {
            var data = new InteractiveBrokersAccountData();
            data.AccountProperties["BASE:NetLiquidation"] = "123";

            var snapshot = data.GetAccountSummarySnapshot();
            snapshot["BASE:NetLiquidation"] = "999";

            Assert.AreEqual("123", data.AccountProperties["BASE:NetLiquidation"]);
        }
    }
}
