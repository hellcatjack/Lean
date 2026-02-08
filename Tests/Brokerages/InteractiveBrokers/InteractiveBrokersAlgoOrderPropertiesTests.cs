using System.Collections.Generic;
using System;
using System.Collections;
using System.Reflection;
using NUnit.Framework;
using QuantConnect;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Tests.Brokerages.InteractiveBrokers
{
    [TestFixture]
    public class InteractiveBrokersAlgoOrderPropertiesTests
    {
        [Test]
        public void ApplyAlgoOrderPropertiesSetsAdaptiveAlgoStrategy()
        {
            var symbol = Symbol.Create("LUV", SecurityType.Equity, Market.USA);
            var properties = new InteractiveBrokersOrderProperties
            {
                AlgoStrategy = "Adaptive",
                AlgoParams = new Dictionary<string, string>
                {
                    { "adaptivePriority", "Normal" }
                }
            };
            var order = new LimitOrder(symbol, 4, 54.17m, DateTime.UtcNow, "direct:1491", properties);

            // QuantConnect.Tests doesn't directly reference CSharpAPI.dll, so we use reflection to
            // validate that IBApi.Order is populated as expected.
            var ibOrderType = Type.GetType("IBApi.Order, CSharpAPI");
            Assert.IsNotNull(ibOrderType, "IBApi.Order type not found (CSharpAPI.dll missing?)");
            var ibOrder = Activator.CreateInstance(ibOrderType);

            var apply = typeof(InteractiveBrokersBrokerage).GetMethod(
                "ApplyAlgoOrderProperties",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic
            );
            Assert.IsNotNull(apply, "InteractiveBrokersBrokerage.ApplyAlgoOrderProperties not found");
            apply.Invoke(null, new[] { (object)order, ibOrder });

            var algoStrategy = ibOrderType.GetProperty("AlgoStrategy")?.GetValue(ibOrder) as string;
            Assert.AreEqual("Adaptive", algoStrategy);

            var algoParamsObj = ibOrderType.GetProperty("AlgoParams")?.GetValue(ibOrder);
            Assert.IsNotNull(algoParamsObj);
            var algoParams = algoParamsObj as IList;
            Assert.IsNotNull(algoParams);
            Assert.AreEqual(1, algoParams.Count);

            var first = algoParams[0];
            var tag = first?.GetType().GetProperty("Tag")?.GetValue(first) as string;
            var value = first?.GetType().GetProperty("Value")?.GetValue(first) as string;
            Assert.AreEqual("adaptivePriority", tag);
            Assert.AreEqual("Normal", value);
        }
    }
}
