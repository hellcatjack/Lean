/*
 * QUANTCONNECT.COM - Democratizing Finance, Empowering Individuals.
 * Lean Algorithmic Trading Engine v2.0. Copyright 2014 QuantConnect Corporation.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using NUnit.Framework;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Tests.Engine;
using QuantConnect.Tests.Engine.DataFeeds;

namespace QuantConnect.Tests.Engine.Results
{
    [TestFixture]
    [NonParallelizable]
    public class LeanBridgeResultHandlerTests
    {
        private MethodInfo _buildPositionsMethod;

        [SetUp]
        public void SetUp()
        {
            _buildPositionsMethod = typeof(LeanBridgeResultHandler).GetMethod(
                "BuildPositions",
                BindingFlags.NonPublic | BindingFlags.Instance
            );
            Assert.IsNotNull(_buildPositionsMethod, "BuildPositions should be accessible via reflection");
        }

        [Test]
        public void BuildPositionsDoesNotFallbackToAlgorithmHoldingsWhenBrokerageEmpty()
        {
            var algorithm = new AlgorithmStub();
            algorithm.AddSecurities(Resolution.Minute, equities: new List<string> { "AAPL" });
            var symbol = SymbolCache.GetSymbol("AAPL");
            algorithm.Securities[symbol].Holdings.SetHoldings(100m, 10);

            using var brokerage = new EmptyHoldingsBrokerage();
            using var messaging = new QuantConnect.Messaging.Messaging();
            using var api = new QuantConnect.Api.Api();
            var transactionHandler = new BrokerageTransactionHandler();
            var resultHandler = new TestResultHandler();
            transactionHandler.Initialize(algorithm, brokerage, resultHandler);
            algorithm.Transactions.SetOrderProcessor(transactionHandler);

            var bridgeHandler = new LeanBridgeResultHandler();
            var job = new LiveNodePacket();
            bridgeHandler.Initialize(new ResultHandlerInitializeParameters(job, messaging, api, transactionHandler, null));
            bridgeHandler.SetAlgorithm(algorithm, 100000m);

            var payload = (Dictionary<string, object>)_buildPositionsMethod.Invoke(
                bridgeHandler,
                new object[] { DateTime.UtcNow }
            );
            var items = (List<Dictionary<string, object>>)payload["items"];
            Assert.AreEqual(0, items.Count, "Algorithm holdings should not be used as a fallback.");
        }

        [Test]
        public void BuildPositionsMarksStaleWhenSummaryShowsHoldingsButEmpty()
        {
            var algorithm = new AlgorithmStub();
            algorithm.AddSecurities(Resolution.Minute, equities: new List<string> { "AAPL" });

            using var brokerage = new SummaryHoldingsBrokerage();
            using var messaging = new QuantConnect.Messaging.Messaging();
            using var api = new QuantConnect.Api.Api();
            var transactionHandler = new BrokerageTransactionHandler();
            var resultHandler = new TestResultHandler();
            transactionHandler.Initialize(algorithm, brokerage, resultHandler);
            algorithm.Transactions.SetOrderProcessor(transactionHandler);

            var bridgeHandler = new LeanBridgeResultHandler();
            var job = new LiveNodePacket();
            bridgeHandler.Initialize(new ResultHandlerInitializeParameters(job, messaging, api, transactionHandler, null));
            bridgeHandler.SetAlgorithm(algorithm, 100000m);

            var payload = (Dictionary<string, object>)_buildPositionsMethod.Invoke(
                bridgeHandler,
                new object[] { DateTime.UtcNow }
            );
            Assert.AreEqual(true, payload["stale"], "Empty IB holdings with positive summary should be marked stale.");
        }

        [Test]
        public void BuildPositionsReconnectsWhenSummaryShowsHoldings()
        {
            var algorithm = new AlgorithmStub();
            algorithm.AddSecurities(Resolution.Minute, equities: new List<string> { "AAPL" });

            using var brokerage = new ReconnectHoldingsBrokerage();
            using var messaging = new QuantConnect.Messaging.Messaging();
            using var api = new QuantConnect.Api.Api();
            var transactionHandler = new BrokerageTransactionHandler();
            var resultHandler = new TestResultHandler();
            transactionHandler.Initialize(algorithm, brokerage, resultHandler);
            algorithm.Transactions.SetOrderProcessor(transactionHandler);

            var bridgeHandler = new LeanBridgeResultHandler();
            var job = new LiveNodePacket();
            bridgeHandler.Initialize(new ResultHandlerInitializeParameters(job, messaging, api, transactionHandler, null));
            bridgeHandler.SetAlgorithm(algorithm, 100000m);

            var payload = (Dictionary<string, object>)_buildPositionsMethod.Invoke(
                bridgeHandler,
                new object[] { DateTime.UtcNow }
            );
            var items = (List<Dictionary<string, object>>)payload["items"];

            Assert.AreEqual(1, brokerage.ConnectCalls, "Expected a reconnect attempt for empty holdings.");
            Assert.AreEqual(1, items.Count, "Holdings should be populated after reconnect.");
            Assert.AreEqual(false, payload["stale"], "Recovered holdings should not be stale.");
        }

        [Test]
        public void BuildPositionsRefreshesHoldingsWhenSupported()
        {
            var algorithm = new AlgorithmStub();
            algorithm.AddSecurities(Resolution.Minute, equities: new List<string> { "AAPL" });

            using var brokerage = new RefreshableHoldingsBrokerage();
            using var messaging = new QuantConnect.Messaging.Messaging();
            using var api = new QuantConnect.Api.Api();
            var transactionHandler = new BrokerageTransactionHandler();
            var resultHandler = new TestResultHandler();
            transactionHandler.Initialize(algorithm, brokerage, resultHandler);
            algorithm.Transactions.SetOrderProcessor(transactionHandler);

            var bridgeHandler = new LeanBridgeResultHandler();
            var job = new LiveNodePacket();
            bridgeHandler.Initialize(new ResultHandlerInitializeParameters(job, messaging, api, transactionHandler, null));
            bridgeHandler.SetAlgorithm(algorithm, 100000m);

            var payload = (Dictionary<string, object>)_buildPositionsMethod.Invoke(
                bridgeHandler,
                new object[] { DateTime.UtcNow }
            );
            var items = (List<Dictionary<string, object>>)payload["items"];

            Assert.AreEqual(1, brokerage.RefreshCalls, "Expected a holdings refresh attempt.");
            Assert.AreEqual(1, items.Count, "Holdings should be populated after refresh.");
            Assert.AreEqual(false, payload["stale"], "Recovered holdings should not be stale.");
        }

        [Test]
        public void WritesHeartbeatStatusFile()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "lean-bridge-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var statusPath = Path.Combine(tempDir, "lean_bridge_status.json");

            var originalOutputDir = Config.Get("lean-bridge-output-dir", string.Empty);
            var originalHeartbeatSeconds = Config.Get("lean-bridge-heartbeat-seconds", string.Empty);

            LeanBridgeResultHandler handler = null;
            using var messaging = new QuantConnect.Messaging.Messaging();
            using var api = new QuantConnect.Api.Api();
            try
            {
                Config.Set("lean-bridge-output-dir", tempDir);
                Config.Set("lean-bridge-heartbeat-seconds", "1");

                handler = new LeanBridgeResultHandler();
                var job = new LiveNodePacket
                {
                    DeployId = "test",
                    UserId = 1,
                    ProjectId = 1
                };
                var transactionHandler = new BacktestingTransactionHandler();

                handler.Initialize(new ResultHandlerInitializeParameters(job, messaging, api, transactionHandler, null));

                var deadline = DateTime.UtcNow.AddSeconds(3);
                while (DateTime.UtcNow < deadline && !File.Exists(statusPath))
                {
                    Thread.Sleep(50);
                }

                Assert.That(File.Exists(statusPath), Is.True, "Heartbeat status file should be written");
            }
            finally
            {
                handler?.Exit();
                Config.Set("lean-bridge-output-dir", originalOutputDir);
                Config.Set("lean-bridge-heartbeat-seconds", originalHeartbeatSeconds);
            }
        }

        private class EmptyHoldingsBrokerage : Brokerage
        {
            public override bool IsConnected => true;

            public EmptyHoldingsBrokerage() : base("Test")
            {
            }

            public override List<Order> GetOpenOrders()
            {
                return new List<Order>();
            }

            public override List<Holding> GetAccountHoldings()
            {
                return new List<Holding>();
            }

            public override List<CashAmount> GetCashBalance()
            {
                return new List<CashAmount>();
            }

            public override void Connect()
            {
            }

            public override void Disconnect()
            {
            }

            public override bool PlaceOrder(Order order)
            {
                return true;
            }

            public override bool UpdateOrder(Order order)
            {
                return true;
            }

            public override bool CancelOrder(Order order)
            {
                return true;
            }
        }

        private class SummaryHoldingsBrokerage : EmptyHoldingsBrokerage, IAccountSummaryProvider
        {
            public Dictionary<string, string> GetAccountSummarySnapshot()
            {
                return new Dictionary<string, string>
                {
                    ["BASE:GrossPositionValue"] = "100"
                };
            }
        }

        private class ReconnectHoldingsBrokerage : SummaryHoldingsBrokerage
        {
            private bool _returnHoldings;
            public int ConnectCalls { get; private set; }

            public override void Connect()
            {
                ConnectCalls += 1;
                _returnHoldings = true;
            }

            public override List<Holding> GetAccountHoldings()
            {
                if (!_returnHoldings)
                {
                    return new List<Holding>();
                }

                return new List<Holding>
                {
                    new Holding
                    {
                        Symbol = Symbols.SPY,
                        Quantity = 5,
                        AveragePrice = 100,
                        MarketValue = 500,
                        MarketPrice = 100,
                        CurrencySymbol = "$"
                    }
                };
            }
        }

        private class RefreshableHoldingsBrokerage : SummaryHoldingsBrokerage, IAccountHoldingsRefresher
        {
            private bool _refreshed;
            public int RefreshCalls { get; private set; }

            public bool RefreshAccountHoldings()
            {
                RefreshCalls += 1;
                _refreshed = true;
                return true;
            }

            public override List<Holding> GetAccountHoldings()
            {
                if (!_refreshed)
                {
                    return new List<Holding>();
                }

                return new List<Holding>
                {
                    new Holding
                    {
                        Symbol = Symbols.SPY,
                        Quantity = 2,
                        AveragePrice = 200,
                        MarketValue = 400,
                        MarketPrice = 200,
                        CurrencySymbol = "$"
                    }
                };
            }
        }
    }
}
