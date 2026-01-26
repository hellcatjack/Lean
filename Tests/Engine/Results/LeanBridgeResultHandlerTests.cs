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
using System.IO;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using QuantConnect.Configuration;
using QuantConnect.Brokerages.InteractiveBrokers;
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Messaging;
using QuantConnect.Orders;
using QuantConnect.Packets;
using QuantConnect.Tests.Engine.DataFeeds;
using QuantConnect.Util;

namespace QuantConnect.Tests.Engine.Results
{
    [TestFixture]
    public class LeanBridgeResultHandlerTests
    {
        [Test]
        public void WritesBridgeFilesOnProcess()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Config.Set("lean-bridge-output-dir", dir);
            Config.Set("lean-bridge-snapshot-seconds", "0");
            Config.Set("lean-bridge-heartbeat-seconds", "0");

            using var messaging = new QuantConnect.Messaging.Messaging();
            var api = new QuantConnect.Api.Api();
            var handler = new LeanBridgeResultHandler();
            handler.Initialize(new ResultHandlerInitializeParameters(new LiveNodePacket(), messaging, api, new BacktestingTransactionHandler(), null));

            var algorithm = new AlgorithmStub();
            algorithm.SetFinishedWarmingUp();
            algorithm.AddEquity("SPY").Holdings.SetHoldings(1, 10);
            handler.SetAlgorithm(algorithm, 100000);

            handler.ProcessSynchronousEvents(true);

            Assert.IsTrue(File.Exists(Path.Combine(dir, "account_summary.json")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "positions.json")));
            Assert.IsTrue(File.Exists(Path.Combine(dir, "lean_bridge_status.json")));

            var json = JObject.Parse(File.ReadAllText(Path.Combine(dir, "account_summary.json")));
            Assert.AreEqual("lean_bridge", (string)json["source"]);
        }

        [Test]
        public void BuildAccountSummaryUsesBrokerageSnapshot()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Config.Set("lean-bridge-output-dir", dir);
            Config.Set("lean-bridge-snapshot-seconds", "0");
            Config.Set("lean-bridge-heartbeat-seconds", "0");

            using var messaging = new QuantConnect.Messaging.Messaging();
            var api = new QuantConnect.Api.Api();
            var transactionHandler = new TestBrokerageTransactionHandler();
            var handler = new LeanBridgeResultHandler();
            handler.Initialize(new ResultHandlerInitializeParameters(new LiveNodePacket(), messaging, api, transactionHandler, null));

            var algorithm = new AlgorithmStub();
            algorithm.SetFinishedWarmingUp();
            handler.SetAlgorithm(algorithm, 100000);

            var brokerage = new InteractiveBrokersBrokerage();
            brokerage.SetAccountSummaryValueForTesting("BASE", "NetLiquidation", "123456.78");
            brokerage.SetAccountSummaryValueForTesting("BASE", "TotalCashValue", "90000.00");
            transactionHandler.Initialize(algorithm, brokerage, handler);

            handler.ProcessSynchronousEvents(true);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(dir, "account_summary.json")));
            Assert.AreEqual(123456.78m, json["items"]["NetLiquidation"].Value<decimal>());
            Assert.AreEqual(90000.00m, json["items"]["TotalCashValue"].Value<decimal>());
            Assert.AreEqual("lean_bridge", (string)json["source"]);
        }

        [Test]
        public void BuildAccountSummaryMergesBaseFirstAndAccountUpdates()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Config.Set("lean-bridge-output-dir", dir);
            Config.Set("lean-bridge-snapshot-seconds", "0");
            Config.Set("lean-bridge-heartbeat-seconds", "0");

            using var messaging = new QuantConnect.Messaging.Messaging();
            var api = new QuantConnect.Api.Api();
            var transactionHandler = new TestBrokerageTransactionHandler();
            var handler = new LeanBridgeResultHandler();
            handler.Initialize(new ResultHandlerInitializeParameters(new LiveNodePacket(), messaging, api, transactionHandler, null));

            var algorithm = new AlgorithmStub();
            algorithm.SetFinishedWarmingUp();
            handler.SetAlgorithm(algorithm, 100000);

            var brokerage = new InteractiveBrokersBrokerage();
            brokerage.SetAccountSummaryValueForTesting("USD", "NetLiquidation", "100");
            brokerage.SetAccountSummaryValueForTesting("BASE", "NetLiquidation", "200");
            brokerage.SetAccountSummaryValueForTesting("BASE", "TotalCashValue", "150");
            brokerage.SetAccountValueForTesting("BASE", "TotalCashValue", "175");
            transactionHandler.Initialize(algorithm, brokerage, handler);

            handler.ProcessSynchronousEvents(true);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(dir, "account_summary.json")));
            Assert.AreEqual(200m, json["items"]["NetLiquidation"].Value<decimal>());
            Assert.AreEqual(175m, json["items"]["TotalCashValue"].Value<decimal>());
        }

        [Test]
        public void BuildAccountSummaryMarksStaleWhenEmpty()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Config.Set("lean-bridge-output-dir", dir);
            Config.Set("lean-bridge-snapshot-seconds", "0");
            Config.Set("lean-bridge-heartbeat-seconds", "0");

            using var messaging = new QuantConnect.Messaging.Messaging();
            var api = new QuantConnect.Api.Api();
            var transactionHandler = new TestBrokerageTransactionHandler();
            var handler = new LeanBridgeResultHandler();
            handler.Initialize(new ResultHandlerInitializeParameters(new LiveNodePacket(), messaging, api, transactionHandler, null));

            var algorithm = new AlgorithmStub();
            algorithm.SetFinishedWarmingUp();
            handler.SetAlgorithm(algorithm, 100000);

            var brokerage = new InteractiveBrokersBrokerage();
            transactionHandler.Initialize(algorithm, brokerage, handler);

            handler.ProcessSynchronousEvents(true);

            var json = JObject.Parse(File.ReadAllText(Path.Combine(dir, "account_summary.json")));
            Assert.IsTrue(json["items"].HasValues == false);
            Assert.IsTrue(json["stale"].Value<bool>());
            Assert.AreEqual("ib_account_empty", (string)json["source_detail"]);
        }

        private class TestBrokerageTransactionHandler : BrokerageTransactionHandler
        {
            protected override void InitializeTransactionThread()
            {
                _orderRequestQueues = new() { new BusyCollection<OrderRequest>() };
            }
        }
    }
}
