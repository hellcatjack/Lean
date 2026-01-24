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
using QuantConnect.Lean.Engine.Results;

namespace QuantConnect.Tests.Engine.Results
{
    [TestFixture]
    public class LeanBridgeWriterTests
    {
        [Test]
        public void WritesJsonAtomically()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var writer = new LeanBridgeWriter(dir);
            writer.WriteJsonAtomic("account_summary.json", new { source = "lean_bridge", items = new { NetLiquidation = 100m } });

            var path = Path.Combine(dir, "account_summary.json");
            Assert.IsTrue(File.Exists(path));
            var json = JObject.Parse(File.ReadAllText(path));
            Assert.AreEqual("lean_bridge", (string)json["source"]);
        }

        [Test]
        public void AppendsJsonLines()
        {
            var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            var writer = new LeanBridgeWriter(dir);
            writer.AppendJsonLine("execution_events.jsonl", new { orderId = 1, status = "FILLED" });
            writer.AppendJsonLine("execution_events.jsonl", new { orderId = 2, status = "NEW" });

            var path = Path.Combine(dir, "execution_events.jsonl");
            var lines = File.ReadAllLines(path);
            Assert.AreEqual(2, lines.Length);
        }
    }
}
