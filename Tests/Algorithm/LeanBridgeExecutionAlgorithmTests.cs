using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using QuantConnect.Algorithm.CSharp;

namespace QuantConnect.Tests.Algorithm
{
    [TestFixture]
    public class LeanBridgeExecutionAlgorithmTests
    {
        [Test]
        public void ParsesQuantityAndWeight()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(path, "[{\"order_intent_id\":\"oi_1_1\",\"symbol\":\"AAPL\",\"quantity\":1},{\"order_intent_id\":\"oi_1_2\",\"symbol\":\"MSFT\",\"weight\":0.2}]");

            var items = LeanBridgeExecutionAlgorithm.LoadIntentItems(path);

            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("oi_1_1", items[0].OrderIntentId);
            Assert.AreEqual("AAPL", items[0].Symbol);
            Assert.AreEqual(1m, items[0].Quantity);
            Assert.AreEqual(0m, items[0].Weight);
            Assert.AreEqual("oi_1_2", items[1].OrderIntentId);
            Assert.AreEqual("MSFT", items[1].Symbol);
            Assert.AreEqual(0m, items[1].Quantity);
            Assert.AreEqual(0.2m, items[1].Weight);
        }

        [Test]
        public void QuantityTakesPriorityOverWeight()
        {
            var items = new[]
            {
                new LeanBridgeExecutionAlgorithm.IntentItem
                {
                    OrderIntentId = "oi_2_1",
                    Symbol = "AAPL",
                    Quantity = 1m,
                    Weight = 0.5m
                }
            };

            var requests = LeanBridgeExecutionAlgorithm.BuildRequests(items);

            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual("oi_2_1", requests[0].OrderIntentId);
            Assert.AreEqual("AAPL", requests[0].Symbol);
            Assert.AreEqual(1m, requests[0].Quantity);
            Assert.AreEqual(0m, requests[0].Weight);
            Assert.IsTrue(requests[0].UseQuantity);
        }

        [Test]
        public void QuantitySupportsNegativeSell()
        {
            var items = new[]
            {
                new LeanBridgeExecutionAlgorithm.IntentItem
                {
                    OrderIntentId = "oi_3_1",
                    Symbol = "AAPL",
                    Quantity = -2m,
                    Weight = 0m
                }
            };

            var requests = LeanBridgeExecutionAlgorithm.BuildRequests(items);

            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual("oi_3_1", requests[0].OrderIntentId);
            Assert.AreEqual("AAPL", requests[0].Symbol);
            Assert.AreEqual(-2m, requests[0].Quantity);
            Assert.AreEqual(0m, requests[0].Weight);
            Assert.IsTrue(requests[0].UseQuantity);
        }

        [Test]
        public void WeightSupportsNegativeSell()
        {
            var items = new[]
            {
                new LeanBridgeExecutionAlgorithm.IntentItem
                {
                    OrderIntentId = "oi_4_1",
                    Symbol = "AAPL",
                    Quantity = 0m,
                    Weight = -0.25m
                }
            };

            var requests = LeanBridgeExecutionAlgorithm.BuildRequests(items);

            Assert.AreEqual(1, requests.Count);
            Assert.AreEqual("oi_4_1", requests[0].OrderIntentId);
            Assert.AreEqual("AAPL", requests[0].Symbol);
            Assert.AreEqual(0m, requests[0].Quantity);
            Assert.AreEqual(-0.25m, requests[0].Weight);
            Assert.IsFalse(requests[0].UseQuantity);
        }

        [Test]
        public void BuildsExecutionLogLines()
        {
            var requests = new List<LeanBridgeExecutionAlgorithm.ExecutionRequest>
            {
                new LeanBridgeExecutionAlgorithm.ExecutionRequest
                {
                    OrderIntentId = "direct:1",
                    Symbol = "AAPL",
                    Quantity = -1m,
                    Weight = 0m,
                    UseQuantity = true
                }
            };

            var lines = LeanBridgeExecutionAlgorithm.BuildExecutionLogLines("/tmp/intent.json", requests);

            Assert.IsNotEmpty(lines);
            Assert.That(lines[0], Does.Contain("LEAN_BRIDGE_INTENT"));
            Assert.That(lines[0], Does.Contain("/tmp/intent.json"));
            Assert.That(lines[0], Does.Contain("requests=1"));
            Assert.That(lines[1], Does.Contain("direct:1"));
            Assert.That(lines[1], Does.Contain("AAPL"));
            Assert.That(lines[1], Does.Contain("quantity=-1"));
        }

        [TestCase(null, "MKT")]
        [TestCase("", "MKT")]
        [TestCase("MKT", "MKT")]
        [TestCase("market", "MKT")]
        [TestCase("LIMIT", "LMT")]
        [TestCase("lmt", "LMT")]
        [TestCase("Adaptive LMT(IBKR)", "ADAPTIVE_LMT")]
        [TestCase("adaptive_limit", "ADAPTIVE_LMT")]
        [TestCase("PEG MID", "PEG_MID")]
        [TestCase("peGmid", "PEG_MID")]
        public void NormalizesOrderTypes(string input, string expected)
        {
            Assert.AreEqual(expected, LeanBridgeExecutionAlgorithm.NormalizeOrderType(input));
        }

        [Test]
        public void DetectsAllIntentOrdersFilled()
        {
            var intentOrders = new Dictionary<string, HashSet<int>>
            {
                { "direct:1", new HashSet<int> { 10, 11 } },
                { "direct:2", new HashSet<int> { 12 } }
            };
            var filledOrderIds = new HashSet<int> { 10, 11, 12 };

            Assert.IsTrue(LeanBridgeExecutionAlgorithm.AreAllIntentOrdersFilled(intentOrders, filledOrderIds));

            filledOrderIds.Remove(11);
            Assert.IsFalse(LeanBridgeExecutionAlgorithm.AreAllIntentOrdersFilled(intentOrders, filledOrderIds));
        }
    }
}
