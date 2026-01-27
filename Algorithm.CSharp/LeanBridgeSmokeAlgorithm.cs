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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Minimal live algorithm used to validate bridge outputs without data subscriptions.
    /// </summary>
    public class LeanBridgeSmokeAlgorithm : QCAlgorithm
    {
        private readonly HashSet<string> _subscribed = new(StringComparer.Ordinal);
        private string _watchlistPath = string.Empty;
        private TimeSpan _watchlistRefreshPeriod = TimeSpan.FromSeconds(5);

        public override void Initialize()
        {
            SetCash(100000);
            SetStartDate(2020, 1, 1);
            SetBenchmark(x => 0m);

            _watchlistPath = Config.Get("lean-bridge-watchlist-path", string.Empty);
            var refreshSeconds = Config.GetInt("lean-bridge-watchlist-refresh-seconds", 5);
            _watchlistRefreshPeriod = TimeSpan.FromSeconds(Math.Max(1, refreshSeconds));

            if (!string.IsNullOrWhiteSpace(_watchlistPath))
            {
                RefreshWatchlist();
                Schedule.On(DateRules.EveryDay(), TimeRules.Every(_watchlistRefreshPeriod), RefreshWatchlist);
            }
        }

        public static List<string> LoadWatchlistSymbols(string path)
        {
            var set = new SortedSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return set.ToList();
            }

            try
            {
                var token = JToken.Parse(File.ReadAllText(path));
                if (token is JObject obj)
                {
                    token = obj["symbols"] ?? obj["Symbols"] ?? token;
                }

                if (token is JArray array)
                {
                    foreach (var entry in array)
                    {
                        string symbol = null;
                        if (entry is JObject entryObj)
                        {
                            symbol = entryObj.Value<string>("symbol");
                        }
                        else if (entry.Type == JTokenType.String)
                        {
                            symbol = entry.Value<string>();
                        }

                        symbol = (symbol ?? string.Empty).Trim().ToUpperInvariant();
                        if (!string.IsNullOrWhiteSpace(symbol))
                        {
                            set.Add(symbol);
                        }
                    }
                }
            }
            catch
            {
                return set.ToList();
            }

            return set.ToList();
        }

        private void RefreshWatchlist()
        {
            var symbols = LoadWatchlistSymbols(_watchlistPath);
            foreach (var symbol in symbols)
            {
                if (_subscribed.Add(symbol))
                {
                    AddEquity(symbol, Resolution.Minute);
                }
            }
        }
    }
}
