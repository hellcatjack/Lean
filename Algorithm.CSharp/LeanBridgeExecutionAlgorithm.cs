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
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Live execution algorithm that reads execution intent and submits orders once.
    /// </summary>
    public class LeanBridgeExecutionAlgorithm : QCAlgorithm
    {
        private bool _executed;
        private List<ExecutionRequest> _requests = new();

        public class IntentItem
        {
            public string Symbol { get; set; }
            public decimal Quantity { get; set; }
            public decimal Weight { get; set; }
        }

        public class ExecutionRequest
        {
            public string Symbol { get; set; }
            public decimal Quantity { get; set; }
            public decimal Weight { get; set; }
            public bool UseQuantity { get; set; }
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

        public static List<ExecutionRequest> BuildRequests(IEnumerable<IntentItem> items)
        {
            var requests = new List<ExecutionRequest>();
            if (items == null)
            {
                return requests;
            }

            foreach (var item in items)
            {
                if (item == null)
                {
                    continue;
                }

                var symbol = item.Symbol?.Trim();
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                if (item.Quantity > 0)
                {
                    requests.Add(new ExecutionRequest
                    {
                        Symbol = symbol,
                        Quantity = item.Quantity,
                        Weight = 0m,
                        UseQuantity = true
                    });
                    continue;
                }

                if (item.Weight > 0)
                {
                    requests.Add(new ExecutionRequest
                    {
                        Symbol = symbol,
                        Quantity = 0m,
                        Weight = item.Weight,
                        UseQuantity = false
                    });
                }
            }

            return requests;
        }

        public override void Initialize()
        {
            SetCash(100000);
            SetBenchmark(x => 0m);

            var intentPath = Config.Get("execution-intent-path", string.Empty);
            var items = LoadIntentItems(intentPath);
            _requests = BuildRequests(items);

            foreach (var request in _requests)
            {
                AddEquity(request.Symbol, Resolution.Minute);
            }
        }

        public override void OnData(Slice data)
        {
            if (_executed || _requests.Count == 0)
            {
                return;
            }

            foreach (var request in _requests)
            {
                if (request.UseQuantity)
                {
                    MarketOrder(request.Symbol, request.Quantity);
                    continue;
                }

                SetHoldings(request.Symbol, request.Weight);
            }

            _executed = true;
            Log("EXECUTED_ONCE");
        }
    }
}
