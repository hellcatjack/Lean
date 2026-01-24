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
using QuantConnect.Configuration;
using QuantConnect.Orders;

namespace QuantConnect.Lean.Engine.Results
{
    public class LeanBridgeResultHandler : LiveTradingResultHandler
    {
        private LeanBridgeWriter _writer;
        private DateTime _nextSnapshotUtc;
        private DateTime _nextHeartbeatUtc;
        private TimeSpan _snapshotPeriod;
        private TimeSpan _heartbeatPeriod;
        private string _lastError;
        private DateTime? _lastErrorAt;
        private bool _degraded;

        public override void Initialize(ResultHandlerInitializeParameters parameters)
        {
            base.Initialize(parameters);
            var outputDir = Config.Get("lean-bridge-output-dir", Path.Combine(Globals.DataFolder, "lean_bridge"));
            _snapshotPeriod = TimeSpan.FromSeconds(Config.GetInt("lean-bridge-snapshot-seconds", 2));
            _heartbeatPeriod = TimeSpan.FromSeconds(Config.GetInt("lean-bridge-heartbeat-seconds", 5));
            _writer = new LeanBridgeWriter(outputDir);
            _nextSnapshotUtc = DateTime.MinValue;
            _nextHeartbeatUtc = DateTime.MinValue;
        }

        public override void ProcessSynchronousEvents(bool forceProcess = false)
        {
            base.ProcessSynchronousEvents(forceProcess);
            var now = DateTime.UtcNow;
            if (forceProcess || now >= _nextSnapshotUtc)
            {
                _nextSnapshotUtc = now.Add(_snapshotPeriod);
                TryWriteSnapshots(now);
            }
            if (forceProcess || now >= _nextHeartbeatUtc)
            {
                _nextHeartbeatUtc = now.Add(_heartbeatPeriod);
                TryWriteStatus(now);
            }
        }

        public override void OrderEvent(OrderEvent newEvent)
        {
            base.OrderEvent(newEvent);
            TryAppendExecutionEvent(newEvent);
        }

        private void TryWriteSnapshots(DateTime now)
        {
            try
            {
                _writer.WriteJsonAtomic("account_summary.json", BuildAccountSummary(now));
                _writer.WriteJsonAtomic("positions.json", BuildPositions(now));
                _writer.WriteJsonAtomic("quotes.json", BuildQuotes(now));
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = now;
                _degraded = true;
            }
        }

        private void TryWriteStatus(DateTime now)
        {
            var payload = new Dictionary<string, object>
            {
                ["status"] = _degraded ? "degraded" : "ok",
                ["last_heartbeat"] = now.ToString("O"),
                ["last_error"] = _lastError,
                ["last_error_at"] = _lastErrorAt?.ToString("O"),
                ["source"] = "lean_bridge",
                ["stale"] = false
            };
            try
            {
                _writer.WriteJsonAtomic("lean_bridge_status.json", payload);
            }
            catch
            {
                // avoid impacting execution
            }
        }

        private void TryAppendExecutionEvent(OrderEvent newEvent)
        {
            try
            {
                _writer.AppendJsonLine("execution_events.jsonl", new Dictionary<string, object>
                {
                    ["order_id"] = newEvent.OrderId,
                    ["symbol"] = newEvent.Symbol?.Value,
                    ["status"] = newEvent.Status.ToString(),
                    ["filled"] = newEvent.FillQuantity,
                    ["fill_price"] = newEvent.FillPrice,
                    ["direction"] = newEvent.Direction.ToString(),
                    ["time"] = newEvent.UtcTime.ToString("O")
                });
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = DateTime.UtcNow;
                _degraded = true;
            }
        }

        private Dictionary<string, object> BuildAccountSummary(DateTime now)
        {
            var items = new Dictionary<string, object>
            {
                ["NetLiquidation"] = Algorithm.Portfolio.TotalPortfolioValue,
                ["TotalCashValue"] = Algorithm.Portfolio.Cash,
                ["BuyingPower"] = Algorithm.Portfolio.MarginRemaining,
                ["UnrealizedPnL"] = Algorithm.Portfolio.TotalUnrealizedProfit,
                ["TotalHoldingsValue"] = Algorithm.Portfolio.TotalHoldingsValue
            };
            return new Dictionary<string, object>
            {
                ["items"] = items,
                ["refreshed_at"] = now.ToString("O"),
                ["source"] = "lean_bridge",
                ["stale"] = false
            };
        }

        private Dictionary<string, object> BuildPositions(DateTime now)
        {
            var holdings = GetHoldings(Algorithm.Securities.Values, Algorithm.SubscriptionManager.SubscriptionDataConfigService, onlyInvested: true);
            var list = new List<Dictionary<string, object>>();
            foreach (var entry in holdings)
            {
                var holding = entry.Value;
                list.Add(new Dictionary<string, object>
                {
                    ["symbol"] = holding.Symbol.Value,
                    ["quantity"] = holding.Quantity,
                    ["avg_cost"] = holding.AveragePrice,
                    ["market_value"] = holding.MarketValue,
                    ["unrealized_pnl"] = holding.UnrealizedProfit,
                    ["currency"] = holding.CurrencySymbol
                });
            }
            return new Dictionary<string, object>
            {
                ["items"] = list,
                ["refreshed_at"] = now.ToString("O"),
                ["source"] = "lean_bridge",
                ["stale"] = false
            };
        }

        private Dictionary<string, object> BuildQuotes(DateTime now)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var security in Algorithm.Securities.Values)
            {
                if (!security.IsTradable || security.Symbol.IsCanonical()) continue;
                list.Add(new Dictionary<string, object>
                {
                    ["symbol"] = security.Symbol.Value,
                    ["bid"] = security.BidPrice,
                    ["ask"] = security.AskPrice,
                    ["last"] = security.Price,
                    ["timestamp"] = security.Time.ToUniversalTime().ToString("O")
                });
            }
            return new Dictionary<string, object>
            {
                ["items"] = list,
                ["refreshed_at"] = now.ToString("O"),
                ["source"] = "lean_bridge",
                ["stale"] = false
            };
        }
    }
}
