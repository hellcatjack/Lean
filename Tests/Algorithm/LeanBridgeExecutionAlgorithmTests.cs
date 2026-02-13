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
            File.WriteAllText(path, "[{\"order_intent_id\":\"oi_1_1\",\"symbol\":\"AAPL\",\"quantity\":1,\"prime_price\":189.25},{\"order_intent_id\":\"oi_1_2\",\"symbol\":\"MSFT\",\"weight\":0.2}]");

            var items = LeanBridgeExecutionAlgorithm.LoadIntentItems(path);

            Assert.AreEqual(2, items.Count);
            Assert.AreEqual("oi_1_1", items[0].OrderIntentId);
            Assert.AreEqual("AAPL", items[0].Symbol);
            Assert.AreEqual(1m, items[0].Quantity);
            Assert.AreEqual(0m, items[0].Weight);
            Assert.AreEqual(189.25m, items[0].PrimePrice);
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

        [TestCase("MKT", false)]
        [TestCase("LMT", true)]
        [TestCase("PEG_MID", true)]
        [TestCase("ADAPTIVE_LMT", false)]
        [TestCase("Adaptive LMT(IBKR)", false)]
        public void RequiresLimitPriceMatchesOrderType(string input, bool expected)
        {
            Assert.AreEqual(expected, LeanBridgeExecutionAlgorithm.RequiresLimitPrice(input));
        }

        [TestCase("MKT", false)]
        [TestCase("LMT", false)]
        [TestCase("PEG_MID", false)]
        [TestCase("ADAPTIVE_LMT", true)]
        [TestCase("Adaptive LMT(IBKR)", true)]
        public void AdaptiveLmtUsesAsynchronousSubmission(string input, bool expected)
        {
            Assert.AreEqual(expected, LeanBridgeExecutionAlgorithm.ShouldUseAsynchronousSubmission(input));
        }

        [Test]
        public void ExecutionGateAllowsSingleEntryUntilReleased()
        {
            var gate = 0;

            Assert.IsTrue(LeanBridgeExecutionAlgorithm.TryEnterExecutionGate(ref gate));
            Assert.IsFalse(LeanBridgeExecutionAlgorithm.TryEnterExecutionGate(ref gate));

            LeanBridgeExecutionAlgorithm.ExitExecutionGate(ref gate);

            Assert.IsTrue(LeanBridgeExecutionAlgorithm.TryEnterExecutionGate(ref gate));
        }

        [TestCase(true, true)]
        [TestCase(false, false)]
        public void WarmupGateDefersExecutionUntilWarmupFinishes(bool isWarmingUp, bool expected)
        {
            Assert.AreEqual(expected, LeanBridgeExecutionAlgorithm.ShouldDeferExecutionForWarmup(isWarmingUp));
        }

        [TestCase(false, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, false, true)]
        [TestCase(true, true, false)]
        public void ReadinessGateRequiresPostInitializeAndWarmupReady(
            bool postInitialized,
            bool warmupReady,
            bool expected
        )
        {
            Assert.AreEqual(
                expected,
                LeanBridgeExecutionAlgorithm.ShouldDeferExecutionUntilReady(postInitialized, warmupReady)
            );
        }

        [Test]
        public void DetectsAllIntentOrdersTerminal()
        {
            var intentOrders = new Dictionary<string, HashSet<int>>
            {
                { "direct:1", new HashSet<int> { 10, 11 } },
                { "direct:2", new HashSet<int> { 12 } }
            };
            var terminalOrderIds = new HashSet<int> { 10, 11, 12 };

            Assert.IsTrue(LeanBridgeExecutionAlgorithm.AreAllIntentOrdersTerminal(intentOrders, terminalOrderIds));

            terminalOrderIds.Remove(11);
            Assert.IsFalse(LeanBridgeExecutionAlgorithm.AreAllIntentOrdersTerminal(intentOrders, terminalOrderIds));
        }

        [Test]
        public void ShouldNotRequestAllTerminalExitBeforeSubmissionCompletes()
        {
            var intentOrders = new Dictionary<string, HashSet<int>>
            {
                { "oi_1_1", new HashSet<int> { 101 } }
            };
            var terminalOrderIds = new HashSet<int> { 101 };

            Assert.IsFalse(
                LeanBridgeExecutionAlgorithm.ShouldRequestAllTerminalExit(
                    exitRequested: false,
                    submissionCompleted: false,
                    intentOrders: intentOrders,
                    terminalOrderIds: terminalOrderIds
                )
            );
        }

        [Test]
        public void ShouldRequestAllTerminalExitAfterSubmissionCompletes()
        {
            var intentOrders = new Dictionary<string, HashSet<int>>
            {
                { "oi_1_1", new HashSet<int> { 101 } }
            };
            var terminalOrderIds = new HashSet<int> { 101 };

            Assert.IsTrue(
                LeanBridgeExecutionAlgorithm.ShouldRequestAllTerminalExit(
                    exitRequested: false,
                    submissionCompleted: true,
                    intentOrders: intentOrders,
                    terminalOrderIds: terminalOrderIds
                )
            );
            Assert.IsFalse(
                LeanBridgeExecutionAlgorithm.ShouldRequestAllTerminalExit(
                    exitRequested: true,
                    submissionCompleted: true,
                    intentOrders: intentOrders,
                    terminalOrderIds: terminalOrderIds
                )
            );
        }

        [Test]
        public void ParsesUnfilledHandlingParams()
        {
            var path = Path.GetTempFileName();
            File.WriteAllText(
                path,
                "{\"unfilled_timeout_seconds\":600,\"unfilled_reprice_interval_seconds\":30,\"unfilled_max_reprices\":5,\"unfilled_max_price_deviation_pct\":1.5}"
            );

            var p = LeanBridgeExecutionAlgorithm.LoadExecutionParams(path);

            Assert.AreEqual(600, p.UnfilledTimeoutSeconds);
            Assert.AreEqual(30, p.UnfilledRepriceIntervalSeconds);
            Assert.AreEqual(5, p.UnfilledMaxReprices);
            Assert.AreEqual(1.5m, p.UnfilledMaxPriceDeviationPct);
        }
    }
}
