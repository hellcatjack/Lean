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
using QuantConnect.Lean.Engine.Results;
using QuantConnect.Lean.Engine.TransactionHandlers;
using QuantConnect.Messaging;
using QuantConnect.Packets;
using QuantConnect.Tests.Engine.DataFeeds;

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

            using var messaging = new Messaging();
            var handler = new LeanBridgeResultHandler();
            handler.Initialize(new ResultHandlerInitializeParameters(new LiveNodePacket(), messaging, null, new BacktestingTransactionHandler(), null));

            var algorithm = new AlgorithmStub(createDataManager: false);
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
    }
}
