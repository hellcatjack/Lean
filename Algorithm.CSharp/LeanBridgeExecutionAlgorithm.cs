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
*/

using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using QuantConnect.Algorithm;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Live execution algorithm that reads execution intent and submits orders once.
    /// </summary>
    public class LeanBridgeExecutionAlgorithm : QCAlgorithm
    {
        public class IntentItem
        {
            public string Symbol { get; set; }
            public decimal Quantity { get; set; }
            public decimal Weight { get; set; }
        }

        public static List<IntentItem> LoadIntentItems(string path)
        {
            var items = new List<IntentItem>();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return items;
            }

            try
            {
                var token = JToken.Parse(File.ReadAllText(path));
                if (token is JArray array)
                {
                    foreach (var entry in array)
                    {
                        if (entry is not JObject obj)
                        {
                            continue;
                        }

                        items.Add(new IntentItem
                        {
                            Symbol = obj.Value<string>("symbol"),
                            Quantity = obj.Value<decimal?>("quantity") ?? 0m,
                            Weight = obj.Value<decimal?>("weight") ?? 0m
                        });
                    }
                }
            }
            catch
            {
                return items;
            }

            return items;
        }

        public override void Initialize()
        {
            SetCash(100000);
            SetBenchmark(x => 0m);
        }
    }
}
