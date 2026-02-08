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
using System.Globalization;
using System.IO;
using Newtonsoft.Json.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Interfaces;
using QuantConnect.Orders;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Live execution algorithm that reads execution intent and submits orders once.
    /// </summary>
    public class LeanBridgeExecutionAlgorithm : QCAlgorithm
    {
        private bool _executed;
        private bool _exitRequested;
        private DateTime _submittedAtUtc;
        private DateTime _exitAfterSubmitDeadlineUtc = DateTime.MinValue;
        private readonly HashSet<int> _ackedOrderIds = new();
        private ExecutionParams _executionParams = new();
        private List<ExecutionRequest> _requests = new();
        private readonly Dictionary<int, string> _orderIdToIntent = new();
        private readonly Dictionary<string, HashSet<int>> _intentToOrderIds = new();
        private readonly HashSet<int> _terminalOrderIds = new();

        private class ExecutionParams
        {
            public int MinQty { get; set; } = 1;
            public int LotSize { get; set; } = 1;
            public decimal CashBufferRatio { get; set; } = 0m;
        }

        public class IntentItem
        {
            public string OrderIntentId { get; set; }
            public string Symbol { get; set; }
            public decimal Quantity { get; set; }
            public decimal Weight { get; set; }
            public string OrderType { get; set; }
            public decimal LimitPrice { get; set; }
            public bool AllowOutsideRth { get; set; }
            public string Session { get; set; }
        }

        public class ExecutionRequest
        {
            public string OrderIntentId { get; set; }
            public string Symbol { get; set; }
            public decimal Quantity { get; set; }
            public decimal Weight { get; set; }
            public bool UseQuantity { get; set; }
            public string OrderType { get; set; }
            public decimal LimitPrice { get; set; }
            public bool AllowOutsideRth { get; set; }
            public string Session { get; set; }
        }

        private static ExecutionParams LoadExecutionParams(string path)
        {
            var result = new ExecutionParams();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return result;
            }

            try
            {
                var token = JToken.Parse(File.ReadAllText(path));
                if (token is not JObject obj)
                {
                    return result;
                }

                var minQty = obj.Value<int?>("min_qty") ?? obj.Value<int?>("minQty");
                if (minQty.HasValue && minQty.Value > 0)
                {
                    result.MinQty = minQty.Value;
                }

                var lotSize = obj.Value<int?>("lot_size") ?? obj.Value<int?>("lotSize");
                if (lotSize.HasValue && lotSize.Value > 0)
                {
                    result.LotSize = lotSize.Value;
                }

                var cashBufferRatio = obj.Value<decimal?>("cash_buffer_ratio") ?? obj.Value<decimal?>("cashBufferRatio");
                if (cashBufferRatio.HasValue)
                {
                    var value = cashBufferRatio.Value;
                    if (value < 0m) value = 0m;
                    if (value > 1m) value = 1m;
                    result.CashBufferRatio = value;
                }
            }
            catch
            {
                return result;
            }

            return result;
        }

        private static int ApplyExecutionConstraints(decimal rawQty, int lotSize, int minQty)
        {
            var lot = lotSize > 0 ? lotSize : 1;
            var minQtyValue = minQty > 0 ? minQty : 1;
            if (minQtyValue % lot != 0)
            {
                minQtyValue = (int)System.Math.Ceiling((decimal)minQtyValue / lot) * lot;
            }

            var qty = (int)System.Math.Ceiling(rawQty / lot) * lot;
            if (qty < minQtyValue)
            {
                qty = minQtyValue;
            }

            return qty < 0 ? 0 : qty;
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
                            OrderIntentId = obj.Value<string>("order_intent_id"),
                            Symbol = obj.Value<string>("symbol"),
                            Quantity = obj.Value<decimal?>("quantity") ?? 0m,
                            Weight = obj.Value<decimal?>("weight") ?? 0m,
                            OrderType = obj.Value<string>("order_type") ?? obj.Value<string>("orderType") ?? string.Empty,
                            LimitPrice = obj.Value<decimal?>("limit_price") ?? obj.Value<decimal?>("limitPrice") ?? 0m,
                            AllowOutsideRth =
                                obj.Value<bool?>("outside_rth")
                                ?? obj.Value<bool?>("allow_outside_rth")
                                ?? obj.Value<bool?>("outside_regular_trading_hours")
                                ?? obj.Value<bool?>("outsideRegularTradingHours")
                                ?? false,
                            Session =
                                obj.Value<string>("session")
                                ?? obj.Value<string>("trading_session")
                                ?? obj.Value<string>("execution_session")
                                ?? string.Empty
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

                if (item.Quantity != 0)
                {
                    requests.Add(new ExecutionRequest
                    {
                        OrderIntentId = item.OrderIntentId,
                        Symbol = symbol,
                        Quantity = item.Quantity,
                        Weight = 0m,
                        UseQuantity = true,
                        OrderType = item.OrderType,
                        LimitPrice = item.LimitPrice,
                        AllowOutsideRth = item.AllowOutsideRth,
                        Session = item.Session
                    });
                    continue;
                }

                if (item.Weight != 0m)
                {
                    requests.Add(new ExecutionRequest
                    {
                        OrderIntentId = item.OrderIntentId,
                        Symbol = symbol,
                        Quantity = 0m,
                        Weight = item.Weight,
                        UseQuantity = false,
                        OrderType = item.OrderType,
                        LimitPrice = item.LimitPrice,
                        AllowOutsideRth = item.AllowOutsideRth,
                        Session = item.Session
                    });
                }
            }

            return requests;
        }

        public static string NormalizeOrderType(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "MKT" : value.Trim().ToUpperInvariant();
            var parenIndex = text.IndexOf('(');
            if (parenIndex >= 0)
            {
                text = text.Substring(0, parenIndex).Trim();
            }

            text = text.Replace("-", "_").Replace(" ", "_");
            while (text.Contains("__"))
            {
                text = text.Replace("__", "_");
            }
            text = text.Trim('_');

            if (text == "MARKET" || text == "MARKET_ORDER")
            {
                return "MKT";
            }
            if (text == "LIMIT" || text == "LIMIT_ORDER")
            {
                return "LMT";
            }
            if (text == "ADAPTIVE" || text == "ADAPTIVELMT" || text == "ADAPTIVE_LIMIT" || text == "ADAPTIVE_LMT")
            {
                return "ADAPTIVE_LMT";
            }
            if (text == "PEG_MID" || text == "PEGMID" || text == "MIDPOINT" || text == "PEG_MIDPOINT")
            {
                return "PEG_MID";
            }

            return string.IsNullOrWhiteSpace(text) ? "MKT" : text;
        }

        private static bool IsLimitLike(string orderType)
        {
            return orderType == "LMT" || orderType == "ADAPTIVE_LMT" || orderType == "PEG_MID";
        }

        private decimal ResolveMidPrice(string symbol)
        {
            try
            {
                var security = Securities[symbol];
                if (security.BidPrice > 0m && security.AskPrice > 0m)
                {
                    return (security.BidPrice + security.AskPrice) / 2m;
                }
            }
            catch
            {
                // ignore
            }
            try
            {
                return Securities[symbol].Price;
            }
            catch
            {
                return 0m;
            }
        }

        private decimal ResolveAdaptiveLimitPrice(string symbol, decimal quantity)
        {
            try
            {
                var security = Securities[symbol];
                if (quantity > 0m)
                {
                    if (security.AskPrice > 0m) return security.AskPrice;
                    if (security.Price > 0m) return security.Price;
                    if (security.BidPrice > 0m) return security.BidPrice;
                }
                if (quantity < 0m)
                {
                    if (security.BidPrice > 0m) return security.BidPrice;
                    if (security.Price > 0m) return security.Price;
                    if (security.AskPrice > 0m) return security.AskPrice;
                }
                return security.Price;
            }
            catch
            {
                return 0m;
            }
        }

        public static List<string> BuildExecutionLogLines(string intentPath, List<ExecutionRequest> requests)
        {
            var lines = new List<string>();
            var safePath = string.IsNullOrWhiteSpace(intentPath) ? "<empty>" : intentPath.Trim();
            var requestCount = requests?.Count ?? 0;
            lines.Add($"LEAN_BRIDGE_INTENT: path={safePath} requests={requestCount}");

            if (requests == null)
            {
                return lines;
            }

            foreach (var request in requests)
            {
                if (request == null)
                {
                    continue;
                }

                var quantity = request.Quantity.ToString(CultureInfo.InvariantCulture);
                var weight = request.Weight.ToString(CultureInfo.InvariantCulture);
                var orderType = string.IsNullOrWhiteSpace(request.OrderType) ? "MKT" : request.OrderType.Trim();
                var limitPrice = request.LimitPrice.ToString(CultureInfo.InvariantCulture);
                var outsideRth = request.AllowOutsideRth.ToString().ToLowerInvariant();
                var session = string.IsNullOrWhiteSpace(request.Session) ? "-" : request.Session.Trim();
                lines.Add($"LEAN_BRIDGE_REQUEST: id={request.OrderIntentId} symbol={request.Symbol} quantity={quantity} weight={weight} useQuantity={request.UseQuantity.ToString().ToLowerInvariant()} orderType={orderType} limitPrice={limitPrice} outsideRth={outsideRth} session={session}");
            }

            return lines;
        }

        public static bool AreAllIntentOrdersTerminal(Dictionary<string, HashSet<int>> intentOrders, HashSet<int> terminalOrderIds)
        {
            if (intentOrders == null || intentOrders.Count == 0)
            {
                return true;
            }

            foreach (var orders in intentOrders.Values)
            {
                if (orders == null)
                {
                    continue;
                }

                foreach (var orderId in orders)
                {
                    if (terminalOrderIds == null || !terminalOrderIds.Contains(orderId))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        public override void Initialize()
        {
            SetCash(100000);
            SetBenchmark(x => 0m);

            var intentPath = Config.Get("execution-intent-path", string.Empty);
            var paramsPath = Config.Get("execution-params-path", string.Empty);
            var items = LoadIntentItems(intentPath);
            _requests = BuildRequests(items);
            _executionParams = LoadExecutionParams(paramsPath);

            foreach (var line in BuildExecutionLogLines(intentPath, _requests))
            {
                Log(line);
            }

            foreach (var request in _requests)
            {
                AddEquity(request.Symbol, Resolution.Minute, extendedMarketHours: true);
            }

            // Execute even when no data arrives (e.g. extended-hours manual orders). Live schedules
            // are driven by the real-time handler, so they keep firing outside market hours.
            Schedule.On(DateRules.EveryDay(), TimeRules.Every(TimeSpan.FromSeconds(1)), TryExecute);
        }

        public override void OnData(Slice data)
        {
            TryExecute();
        }

        private void TryExecute()
        {
            if (_executed)
            {
                TryExitAfterSubmit();
                return;
            }

            if (_requests.Count == 0)
            {
                _executed = true;
                Log("LEAN_BRIDGE_NO_REQUESTS");
                RequestExit("no_requests");
                return;
            }

            var requiresPortfolio = false;
            foreach (var request in _requests)
            {
                if (request == null) continue;
                if (!request.UseQuantity)
                {
                    requiresPortfolio = true;
                    break;
                }
            }

            // Quantity-based execution doesn't need portfolio value, and must work when no market data is flowing.
            var effectiveValue = 0m;
            if (requiresPortfolio)
            {
                var portfolioValue = Portfolio?.TotalPortfolioValue ?? 0m;
                if (portfolioValue <= 0m)
                {
                    Log("LEAN_BRIDGE_WAIT_PORTFOLIO");
                    return;
                }

                effectiveValue = portfolioValue * (1m - _executionParams.CashBufferRatio);
            }

            var submittedOrders = 0;
            var submittedIntents = 0;
            foreach (var request in _requests)
            {
                if (string.IsNullOrWhiteSpace(request.OrderIntentId))
                {
                    Log($"LEAN_BRIDGE_SKIP: missing order_intent_id for {request.Symbol}");
                    continue;
                }
                var intentId = request.OrderIntentId.Trim();
                var weight = request.Weight.ToString(CultureInfo.InvariantCulture);
                var orderType = NormalizeOrderType(request.OrderType);
                var limitPriceValue = request.LimitPrice;
                var limitLike = IsLimitLike(orderType);
                var outsideRth = request.AllowOutsideRth.ToString().ToLowerInvariant();
                var session = string.IsNullOrWhiteSpace(request.Session) ? "-" : request.Session.Trim();
                var computedQty = request.Quantity;
                if (!request.UseQuantity)
                {
                    try
                    {
                        var price = Securities[request.Symbol].Price;
                        if (price <= 0m)
                        {
                            var fallbackPrice = request.LimitPrice;
                            if (fallbackPrice > 0m)
                            {
                                price = fallbackPrice;
                                Log($"LEAN_BRIDGE_FALLBACK_PRICE: id={intentId} symbol={request.Symbol} price={price.ToString(CultureInfo.InvariantCulture)} source=limitPrice");
                            }
                            else
                            {
                                Log($"LEAN_BRIDGE_WAIT_PRICE: id={intentId} symbol={request.Symbol}");
                                return;
                            }
                        }

                        var rawQty = System.Math.Abs(request.Weight) * effectiveValue / price;
                        var sizedQty = ApplyExecutionConstraints(rawQty, _executionParams.LotSize, _executionParams.MinQty);
                        computedQty = request.Weight >= 0m ? sizedQty : -sizedQty;
                    }
                    catch
                    {
                        var fallbackPrice = request.LimitPrice;
                        if (fallbackPrice <= 0m)
                        {
                            Log($"LEAN_BRIDGE_SKIP: price unavailable for {request.Symbol} (id={intentId})");
                            continue;
                        }

                        var rawQty = System.Math.Abs(request.Weight) * effectiveValue / fallbackPrice;
                        var sizedQty = ApplyExecutionConstraints(rawQty, _executionParams.LotSize, _executionParams.MinQty);
                        computedQty = request.Weight >= 0m ? sizedQty : -sizedQty;
                        Log($"LEAN_BRIDGE_FALLBACK_PRICE: id={intentId} symbol={request.Symbol} price={fallbackPrice.ToString(CultureInfo.InvariantCulture)} source=limitPrice");
                    }
                }
                InteractiveBrokersOrderProperties ibOrderProperties = null;
                if (request.AllowOutsideRth)
                {
                    ibOrderProperties = new InteractiveBrokersOrderProperties { OutsideRegularTradingHours = true };
                }
                if (orderType == "ADAPTIVE_LMT")
                {
                    ibOrderProperties ??= new InteractiveBrokersOrderProperties();
                    ibOrderProperties.AlgoStrategy = "Adaptive";
                    var priority = Config.Get("lean-bridge-adaptive-priority", "Normal");
                    if (string.IsNullOrWhiteSpace(priority)) priority = "Normal";
                    ibOrderProperties.AlgoParams = new Dictionary<string, string>
                    {
                        { "adaptivePriority", priority.Trim() }
                    };
                }
                IOrderProperties orderProperties = ibOrderProperties;
                if (computedQty == 0m)
                {
                    Log($"LEAN_BRIDGE_SKIP: computed quantity=0 for {request.Symbol} (id={intentId})");
                    continue;
                }

                OrderTicket ticket = null;
                if (limitLike && limitPriceValue <= 0m)
                {
                    if (orderType == "PEG_MID")
                    {
                        limitPriceValue = ResolveMidPrice(request.Symbol);
                    }
                    else if (orderType == "ADAPTIVE_LMT")
                    {
                        limitPriceValue = ResolveAdaptiveLimitPrice(request.Symbol, computedQty);
                    }
                    else
                    {
                        try
                        {
                            limitPriceValue = Securities[request.Symbol].Price;
                        }
                        catch
                        {
                            limitPriceValue = 0m;
                        }
                    }
                }
                var quantity = computedQty.ToString(CultureInfo.InvariantCulture);
                var limitPrice = limitPriceValue.ToString(CultureInfo.InvariantCulture);
                Log($"LEAN_BRIDGE_SUBMIT: id={intentId} symbol={request.Symbol} quantity={quantity} weight={weight} useQuantity={request.UseQuantity.ToString().ToLowerInvariant()} orderType={orderType} limitPrice={limitPrice} outsideRth={outsideRth} session={session}");
                if (limitLike)
                {
                    if (limitPriceValue <= 0m)
                    {
                        Log($"LEAN_BRIDGE_SKIP: invalid limit price for {request.Symbol} (id={intentId})");
                        continue;
                    }
                    ticket = LimitOrder(request.Symbol, computedQty, limitPriceValue, tag: intentId, orderProperties: orderProperties);
                }
                else
                {
                    ticket = MarketOrder(request.Symbol, computedQty, tag: intentId, orderProperties: orderProperties);
                }

                if (ticket != null)
                {
                    RegisterTicket(intentId, ticket);
                    submittedOrders += 1;
                    submittedIntents += 1;
                }
            }

            _executed = true;
            Log($"LEAN_BRIDGE_SUBMITTED: intents={submittedIntents} orders={submittedOrders}");

            if (submittedOrders == 0)
            {
                Log("LEAN_BRIDGE_NO_ORDERS_SUBMITTED");
                RequestExit("no_orders_submitted");
            }
            else if (Config.GetBool("lean-bridge-exit-on-submit", true))
            {
                // Default behavior: submit once and exit shortly after. We keep the engine alive
                // briefly to capture initial order status events (Submitted/Invalid), so the backend
                // can sync statuses even when some symbols are rejected outside RTH.
                _submittedAtUtc = DateTime.UtcNow;
                var ackTimeoutSeconds = Config.GetInt("lean-bridge-exit-ack-timeout-seconds", 30);
                if (ackTimeoutSeconds < 0) ackTimeoutSeconds = 0;
                _exitAfterSubmitDeadlineUtc = _submittedAtUtc.AddSeconds(ackTimeoutSeconds);
                Log($"LEAN_BRIDGE_EXIT_ARM: orders={submittedOrders} ackTimeoutSeconds={ackTimeoutSeconds}");
                TryExitAfterSubmit();
            }
        }

        public override void OnOrderEvent(OrderEvent orderEvent)
        {
            if (orderEvent == null)
            {
                return;
            }

            Log($"LEAN_BRIDGE_ORDER_EVENT: orderId={orderEvent.OrderId} status={orderEvent.Status} fillQuantity={orderEvent.FillQuantity.ToString(CultureInfo.InvariantCulture)} symbol={orderEvent.Symbol}");

            if (Config.GetBool("lean-bridge-exit-on-submit", true))
            {
                if (_orderIdToIntent.ContainsKey(orderEvent.OrderId))
                {
                    _ackedOrderIds.Add(orderEvent.OrderId);
                }
                TryExitAfterSubmit();
                return;
            }

            if (!IsTerminalStatus(orderEvent.Status))
            {
                return;
            }

            if (!_orderIdToIntent.TryGetValue(orderEvent.OrderId, out var intentId))
            {
                return;
            }

            _terminalOrderIds.Add(orderEvent.OrderId);
            Log($"LEAN_BRIDGE_TERMINAL_TRACK: intent={intentId} terminalOrders={_terminalOrderIds.Count} totalOrders={_orderIdToIntent.Count}");

            if (!_exitRequested && AreAllIntentOrdersTerminal(_intentToOrderIds, _terminalOrderIds))
            {
                Log($"LEAN_BRIDGE_ALL_TERMINAL: intents={_intentToOrderIds.Count} orders={_orderIdToIntent.Count}");
                RequestExit("all_terminal");
            }
        }

        private static bool IsTerminalStatus(OrderStatus status)
        {
            return status == OrderStatus.Filled || status == OrderStatus.Canceled || status == OrderStatus.Invalid;
        }

        private int RegisterTickets(string intentId, IEnumerable<OrderTicket> tickets)
        {
            var count = 0;
            if (tickets == null)
            {
                return count;
            }

            foreach (var ticket in tickets)
            {
                if (ticket == null)
                {
                    continue;
                }

                RegisterTicket(intentId, ticket);
                count += 1;
            }

            return count;
        }

        private void RegisterTicket(string intentId, OrderTicket ticket)
        {
            _orderIdToIntent[ticket.OrderId] = intentId;
            if (!_intentToOrderIds.TryGetValue(intentId, out var orderIds))
            {
                orderIds = new HashSet<int>();
                _intentToOrderIds[intentId] = orderIds;
            }

            orderIds.Add(ticket.OrderId);
        }

        private void RequestExit(string reason)
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            Quit(reason);
        }

        private void TryExitAfterSubmit()
        {
            if (_exitRequested)
            {
                return;
            }
            if (!Config.GetBool("lean-bridge-exit-on-submit", true))
            {
                return;
            }
            if (!_executed)
            {
                return;
            }

            var total = _orderIdToIntent.Count;
            var acked = _ackedOrderIds.Count;
            if (total > 0 && acked >= total)
            {
                Log($"LEAN_BRIDGE_EXIT_ACKED: acked={acked} total={total}");
                RequestExit("submitted_ack");
                return;
            }

            if (_exitAfterSubmitDeadlineUtc != DateTime.MinValue && DateTime.UtcNow >= _exitAfterSubmitDeadlineUtc)
            {
                Log($"LEAN_BRIDGE_EXIT_TIMEOUT: acked={acked} total={total}");
                RequestExit("submitted_timeout");
            }
        }
    }
}
