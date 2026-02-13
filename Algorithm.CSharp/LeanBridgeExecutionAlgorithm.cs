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
using System.Threading;
using Newtonsoft.Json.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Live execution algorithm that reads execution intent and submits orders once.
    /// </summary>
    public class LeanBridgeExecutionAlgorithm : QCAlgorithm
    {
        private bool _executed;
        private int _executeInProgress;
        private bool _exitRequested;
        private bool _submissionCompleted;
        private DateTime _submittedAtUtc;
        private DateTime _exitAfterSubmitDeadlineUtc = DateTime.MinValue;
        private readonly HashSet<int> _ackedOrderIds = new();
        private ExecutionParams _executionParams = new();
        private List<ExecutionRequest> _requests = new();
        private readonly Dictionary<int, string> _orderIdToIntent = new();
        private readonly Dictionary<int, string> _orderIdToOrderType = new();
        private readonly Dictionary<int, OrderTicket> _orderTickets = new();
        private readonly Dictionary<int, DateTime> _orderSubmittedAtUtc = new();
        private readonly Dictionary<int, DateTime> _orderLastRepriceAtUtc = new();
        private readonly Dictionary<int, int> _orderRepriceAttempts = new();
        private readonly Dictionary<int, decimal> _orderInitialLimitPrice = new();
        private readonly HashSet<int> _cancelRequestedOrderIds = new();
        private readonly Dictionary<string, HashSet<int>> _intentToOrderIds = new();
        private readonly HashSet<int> _terminalOrderIds = new();
        private readonly HashSet<string> _primedSymbols = new();
        private bool _warmupDeferredLogged;
        private bool _postInitialized;
        private bool _warmupReady;

        public class ExecutionParams
        {
            public int MinQty { get; set; } = 1;
            public int LotSize { get; set; } = 1;
            public decimal CashBufferRatio { get; set; } = 0m;
            // Long-unfilled handling (QuantConnect-style order management).
            // Note: Lean's TimeInForce for equities is end-of-day; for short timeouts we must cancel/update manually.
            public int UnfilledTimeoutSeconds { get; set; } = 0;
            public int UnfilledRepriceIntervalSeconds { get; set; } = 0;
            public int UnfilledMaxReprices { get; set; } = 0;
            public decimal UnfilledMaxPriceDeviationPct { get; set; } = 0m;
        }

        public class IntentItem
        {
            public string OrderIntentId { get; set; }
            public string Symbol { get; set; }
            public decimal Quantity { get; set; }
            public decimal Weight { get; set; }
            public string OrderType { get; set; }
            public decimal LimitPrice { get; set; }
            public decimal PrimePrice { get; set; }
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
            public decimal PrimePrice { get; set; }
            public bool AllowOutsideRth { get; set; }
            public string Session { get; set; }
        }

        public static ExecutionParams LoadExecutionParams(string path)
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

                var unfilledTimeoutSeconds =
                    obj.Value<int?>("unfilled_timeout_seconds")
                    ?? obj.Value<int?>("unfilledTimeoutSeconds")
                    ?? obj.Value<int?>("unfilled_timeout")
                    ?? obj.Value<int?>("unfilledTimeout");
                if (unfilledTimeoutSeconds.HasValue && unfilledTimeoutSeconds.Value > 0)
                {
                    result.UnfilledTimeoutSeconds = unfilledTimeoutSeconds.Value;
                }

                var unfilledRepriceIntervalSeconds =
                    obj.Value<int?>("unfilled_reprice_interval_seconds")
                    ?? obj.Value<int?>("unfilledRepriceIntervalSeconds")
                    ?? obj.Value<int?>("unfilled_reprice_interval")
                    ?? obj.Value<int?>("unfilledRepriceInterval");
                if (unfilledRepriceIntervalSeconds.HasValue && unfilledRepriceIntervalSeconds.Value > 0)
                {
                    result.UnfilledRepriceIntervalSeconds = unfilledRepriceIntervalSeconds.Value;
                }

                var unfilledMaxReprices =
                    obj.Value<int?>("unfilled_max_reprices")
                    ?? obj.Value<int?>("unfilledMaxReprices")
                    ?? obj.Value<int?>("unfilled_max_reprice")
                    ?? obj.Value<int?>("unfilledMaxReprice");
                if (unfilledMaxReprices.HasValue && unfilledMaxReprices.Value > 0)
                {
                    result.UnfilledMaxReprices = unfilledMaxReprices.Value;
                }

                var unfilledMaxDeviation =
                    obj.Value<decimal?>("unfilled_max_price_deviation_pct")
                    ?? obj.Value<decimal?>("unfilledMaxPriceDeviationPct")
                    // Backward-compat: reuse global deviation config if provided.
                    ?? obj.Value<decimal?>("max_price_deviation_pct")
                    ?? obj.Value<decimal?>("maxPriceDeviationPct");
                if (unfilledMaxDeviation.HasValue)
                {
                    var value = unfilledMaxDeviation.Value;
                    if (value < 0m) value = 0m;
                    result.UnfilledMaxPriceDeviationPct = value;
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
                            PrimePrice = obj.Value<decimal?>("prime_price") ?? obj.Value<decimal?>("primePrice") ?? 0m,
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
                        PrimePrice = item.PrimePrice,
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
                        PrimePrice = item.PrimePrice,
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

        public static bool RequiresLimitPrice(string value)
        {
            var orderType = NormalizeOrderType(value);
            return orderType == "LMT" || orderType == "PEG_MID";
        }

        public static bool ShouldUseAsynchronousSubmission(string value)
        {
            var orderType = NormalizeOrderType(value);
            // Adaptive LMT on IBKR should be forwarded quickly and let TWS/IB manage working logic.
            // Synchronous market-order waiting can serialize submissions with multi-second gaps.
            return orderType == "ADAPTIVE_LMT";
        }

        public static bool TryEnterExecutionGate(ref int gate)
        {
            return Interlocked.CompareExchange(ref gate, 1, 0) == 0;
        }

        public static void ExitExecutionGate(ref int gate)
        {
            Volatile.Write(ref gate, 0);
        }

        public static bool ShouldDeferExecutionForWarmup(bool isWarmingUp)
        {
            return isWarmingUp;
        }

        public static bool ShouldDeferExecutionUntilReady(bool postInitialized, bool warmupReady)
        {
            return !postInitialized || !warmupReady;
        }

        private static bool IsLimitLike(string orderType)
        {
            return RequiresLimitPrice(orderType);
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

        private bool IsSecurityExchangeOpen(string symbol)
        {
            try
            {
                var security = Securities[symbol];
                if (security == null || security.Exchange == null)
                {
                    return false;
                }
                return security.Exchange.ExchangeOpen;
            }
            catch
            {
                return false;
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
                var primePrice = request.PrimePrice.ToString(CultureInfo.InvariantCulture);
                var outsideRth = request.AllowOutsideRth.ToString().ToLowerInvariant();
                var session = string.IsNullOrWhiteSpace(request.Session) ? "-" : request.Session.Trim();
                lines.Add($"LEAN_BRIDGE_REQUEST: id={request.OrderIntentId} symbol={request.Symbol} quantity={quantity} weight={weight} useQuantity={request.UseQuantity.ToString().ToLowerInvariant()} orderType={orderType} limitPrice={limitPrice} primePrice={primePrice} outsideRth={outsideRth} session={session}");
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

        public static bool ShouldRequestAllTerminalExit(
            bool exitRequested,
            bool submissionCompleted,
            Dictionary<string, HashSet<int>> intentOrders,
            HashSet<int> terminalOrderIds
        )
        {
            if (exitRequested || !submissionCompleted)
            {
                return false;
            }
            return AreAllIntentOrdersTerminal(intentOrders, terminalOrderIds);
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
                // Second-level subscription shortens the cold-start window before the first
                // tradable price arrives when we cannot prime from intent/fallback price.
                AddEquity(request.Symbol, Resolution.Second, extendedMarketHours: true);
            }

            // Execute even when no data arrives (e.g. extended-hours manual orders). Live schedules
            // are driven by the real-time handler, so they keep firing outside market hours.
            Schedule.On(DateRules.EveryDay(), TimeRules.Every(TimeSpan.FromSeconds(1)), TryExecute);
        }

        public override void PostInitialize()
        {
            base.PostInitialize();
            _postInitialized = true;
            if (!IsWarmingUp)
            {
                _warmupReady = true;
                Log("LEAN_BRIDGE_POST_INITIALIZE_READY");
                return;
            }
            Log("LEAN_BRIDGE_POST_INITIALIZE_WAIT_WARMUP");
        }

        public override void OnWarmupFinished()
        {
            _warmupReady = true;
            Log("LEAN_BRIDGE_WARMUP_FINISHED");
            // Submit as soon as warmup finishes to avoid the extra scheduler tick delay.
            TryExecute();
        }

        public override void OnData(Slice data)
        {
            if (!_warmupReady && !IsWarmingUp)
            {
                _warmupReady = true;
                Log("LEAN_BRIDGE_WARMUP_READY_ONDATA");
            }
            TryExecute();
        }

        private bool EnsureSecurityPrice(string symbol, decimal fallbackPrice, string intentId)
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return false;
            }

            Security security;
            try
            {
                security = Securities[symbol];
            }
            catch
            {
                return false;
            }

            if (security.Price > 0m)
            {
                return true;
            }

            decimal reference = 0m;
            try
            {
                if (security.BidPrice > 0m && security.AskPrice > 0m)
                {
                    reference = (security.BidPrice + security.AskPrice) / 2m;
                }
                else if (security.AskPrice > 0m)
                {
                    reference = security.AskPrice;
                }
                else if (security.BidPrice > 0m)
                {
                    reference = security.BidPrice;
                }
            }
            catch
            {
                reference = 0m;
            }

            if (reference <= 0m && fallbackPrice > 0m)
            {
                reference = fallbackPrice;
            }

            if (reference > 0m)
            {
                try
                {
                    security.SetMarketPrice(new Tick { Value = reference });
                    if (_primedSymbols.Add(symbol))
                    {
                        Log($"LEAN_BRIDGE_PRIME_PRICE: id={intentId} symbol={symbol} price={reference.ToString(CultureInfo.InvariantCulture)}");
                    }
                }
                catch
                {
                    // ignored
                }
            }

            return security.Price > 0m;
        }

        private void TryExecute()
        {
            if (!TryEnterExecutionGate(ref _executeInProgress))
            {
                return;
            }

            try
            {
                if (ShouldDeferExecutionUntilReady(_postInitialized, _warmupReady))
                {
                    if (!_warmupDeferredLogged)
                    {
                        _warmupDeferredLogged = true;
                        Log("LEAN_BRIDGE_WAIT_READY");
                    }
                    return;
                }
                if (ShouldDeferExecutionForWarmup(IsWarmingUp))
                {
                    if (!_warmupDeferredLogged)
                    {
                        _warmupDeferredLogged = true;
                        Log("LEAN_BRIDGE_WAIT_WARMUP");
                    }
                    return;
                }
                _warmupDeferredLogged = false;

                if (_executed)
                {
                    TryExitAfterSubmit();
                    ManageOpenOrders();
                    return;
                }

                if (_requests.Count == 0)
                {
                    _executed = true;
                    _submissionCompleted = true;
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
                var preparedOrders = new List<(string IntentId, ExecutionRequest Request, decimal ComputedQty, string OrderType, decimal LimitPriceValue, IOrderProperties OrderProperties, bool LimitLike)>();

                // Preflight: ensure we can compute all orders without submitting partially.
                foreach (var request in _requests)
                {
                    if (request == null)
                    {
                        continue;
                    }
                    if (string.IsNullOrWhiteSpace(request.OrderIntentId))
                    {
                        Log($"LEAN_BRIDGE_SKIP: missing order_intent_id for {request.Symbol}");
                        continue;
                    }

                    var intentId = request.OrderIntentId.Trim();
                    var orderType = NormalizeOrderType(request.OrderType);
                    var limitLike = IsLimitLike(orderType);
                    var adaptiveFallbackToLmt = false;

                    // Lean will reject orders when Security.Price==0 (SecurityPriceZero).
                    // In live trading this often happens right after startup before the first tick arrives.
                    // Prime with bid/ask or an explicit limit price when available, otherwise wait and retry later.
                    var preflightPrice = request.PrimePrice > 0m ? request.PrimePrice : request.LimitPrice;
                    if (!EnsureSecurityPrice(request.Symbol, preflightPrice, intentId))
                    {
                        Log($"LEAN_BRIDGE_WAIT_PRICE: id={intentId} symbol={request.Symbol}");
                        return;
                    }

                    var weight = request.Weight.ToString(CultureInfo.InvariantCulture);
                    var outsideRth = request.AllowOutsideRth.ToString().ToLowerInvariant();
                    var session = string.IsNullOrWhiteSpace(request.Session) ? "-" : request.Session.Trim();
                    var computedQty = request.Quantity;
                    if (!request.UseQuantity)
                    {
                        decimal price;
                        try
                        {
                            price = Securities[request.Symbol].Price;
                        }
                        catch
                        {
                            price = 0m;
                        }
                        if (price <= 0m)
                        {
                            // Even after priming, we might still not have a usable price. Wait and retry.
                            Log($"LEAN_BRIDGE_WAIT_PRICE: id={intentId} symbol={request.Symbol}");
                            return;
                        }

                        var rawQty = System.Math.Abs(request.Weight) * effectiveValue / price;
                        var sizedQty = ApplyExecutionConstraints(rawQty, _executionParams.LotSize, _executionParams.MinQty);
                        computedQty = request.Weight >= 0m ? sizedQty : -sizedQty;
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

                    // MarketOrder + Adaptive can be auto-converted by Lean into MarketOnOpen when
                    // regular hours are closed, which IB rejects for algo orders (OPG invalid).
                    // Fall back to plain LMT in that window so the order remains broker-acceptable.
                    if (orderType == "ADAPTIVE_LMT" && !request.AllowOutsideRth && !IsSecurityExchangeOpen(request.Symbol))
                    {
                        adaptiveFallbackToLmt = true;
                        orderType = "LMT";
                        limitLike = true;
                        Log($"LEAN_BRIDGE_ADAPTIVE_FALLBACK_LMT: id={intentId} symbol={request.Symbol} reason=market_closed");
                    }

                    var limitPriceValue = request.LimitPrice;
                    if (limitLike && limitPriceValue <= 0m)
                    {
                        if (orderType == "PEG_MID")
                        {
                            limitPriceValue = ResolveMidPrice(request.Symbol);
                        }
                        else if (adaptiveFallbackToLmt)
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

                    // After computing the limit price, prime again if needed so pre-order checks don't reject.
                    var finalPrime = limitPriceValue > 0m ? limitPriceValue : request.PrimePrice;
                    if (!EnsureSecurityPrice(request.Symbol, finalPrime, intentId))
                    {
                        Log($"LEAN_BRIDGE_WAIT_PRICE: id={intentId} symbol={request.Symbol}");
                        return;
                    }

                    var quantity = computedQty.ToString(CultureInfo.InvariantCulture);
                    var limitPrice = limitPriceValue.ToString(CultureInfo.InvariantCulture);
                    Log($"LEAN_BRIDGE_PREPARED: id={intentId} symbol={request.Symbol} quantity={quantity} weight={weight} useQuantity={request.UseQuantity.ToString().ToLowerInvariant()} orderType={orderType} limitPrice={limitPrice} outsideRth={outsideRth} session={session}");
                    if (limitLike && limitPriceValue <= 0m)
                    {
                        Log($"LEAN_BRIDGE_SKIP: invalid limit price for {request.Symbol} (id={intentId})");
                        continue;
                    }

                    preparedOrders.Add((intentId, request, computedQty, orderType, limitPriceValue, orderProperties, limitLike));
                }

                foreach (var prepared in preparedOrders)
                {
                    var intentId = prepared.IntentId;
                    var request = prepared.Request;
                    var orderType = prepared.OrderType;
                    var limitLike = prepared.LimitLike;
                    var computedQty = prepared.ComputedQty;
                    var limitPriceValue = prepared.LimitPriceValue;
                    var orderProperties = prepared.OrderProperties;
                    var weight = request.Weight.ToString(CultureInfo.InvariantCulture);
                    var outsideRth = request.AllowOutsideRth.ToString().ToLowerInvariant();
                    var session = string.IsNullOrWhiteSpace(request.Session) ? "-" : request.Session.Trim();

                    var quantity = computedQty.ToString(CultureInfo.InvariantCulture);
                    var limitPrice = limitPriceValue.ToString(CultureInfo.InvariantCulture);
                    Log($"LEAN_BRIDGE_SUBMIT: id={intentId} symbol={request.Symbol} quantity={quantity} weight={weight} useQuantity={request.UseQuantity.ToString().ToLowerInvariant()} orderType={orderType} limitPrice={limitPrice} outsideRth={outsideRth} session={session}");

                    OrderTicket ticket;
                    if (limitLike)
                    {
                        ticket = LimitOrder(request.Symbol, computedQty, limitPriceValue, tag: intentId, orderProperties: orderProperties);
                    }
                    else
                    {
                        var asynchronous = ShouldUseAsynchronousSubmission(orderType);
                        ticket = MarketOrder(request.Symbol, computedQty, asynchronous: asynchronous, tag: intentId, orderProperties: orderProperties);
                    }

                    if (ticket != null)
                    {
                        RegisterTicket(intentId, ticket, orderType, limitLike ? limitPriceValue : null);
                        submittedOrders += 1;
                        submittedIntents += 1;
                    }
                }

                _submissionCompleted = true;
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
                else if (ShouldRequestAllTerminalExit(_exitRequested, _submissionCompleted, _intentToOrderIds, _terminalOrderIds))
                {
                    Log($"LEAN_BRIDGE_ALL_TERMINAL_POST_SUBMIT: intents={_intentToOrderIds.Count} orders={_orderIdToIntent.Count}");
                    RequestExit("all_terminal_post_submit");
                }
            }
            finally
            {
                ExitExecutionGate(ref _executeInProgress);
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
            Log($"LEAN_BRIDGE_TERMINAL_TRACK: intent={intentId} terminalOrders={_terminalOrderIds.Count} totalOrders={_orderIdToIntent.Count} submissionCompleted={_submissionCompleted.ToString().ToLowerInvariant()}");

            if (ShouldRequestAllTerminalExit(_exitRequested, _submissionCompleted, _intentToOrderIds, _terminalOrderIds))
            {
                Log($"LEAN_BRIDGE_ALL_TERMINAL: intents={_intentToOrderIds.Count} orders={_orderIdToIntent.Count}");
                RequestExit("all_terminal");
            }
            else if (!_submissionCompleted)
            {
                Log($"LEAN_BRIDGE_TERMINAL_DEFER_EXIT: intent={intentId} terminalOrders={_terminalOrderIds.Count} totalOrders={_orderIdToIntent.Count}");
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

        private void RegisterTicket(string intentId, OrderTicket ticket, string orderType = null, decimal? initialLimitPrice = null)
        {
            _orderIdToIntent[ticket.OrderId] = intentId;
            if (!string.IsNullOrWhiteSpace(orderType))
            {
                _orderIdToOrderType[ticket.OrderId] = orderType.Trim();
            }
            _orderTickets[ticket.OrderId] = ticket;
            var nowUtc = DateTime.UtcNow;
            _orderSubmittedAtUtc[ticket.OrderId] = nowUtc;
            if (initialLimitPrice.HasValue && initialLimitPrice.Value > 0m)
            {
                _orderInitialLimitPrice[ticket.OrderId] = initialLimitPrice.Value;
            }
            _orderRepriceAttempts[ticket.OrderId] = 0;
            _orderLastRepriceAtUtc[ticket.OrderId] = nowUtc;
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

        private bool IsUnfilledManagementEnabled()
        {
            if (_executionParams == null)
            {
                return false;
            }
            if (_executionParams.UnfilledTimeoutSeconds > 0)
            {
                return true;
            }
            return _executionParams.UnfilledRepriceIntervalSeconds > 0 && _executionParams.UnfilledMaxReprices > 0;
        }

        private decimal ResolveRepriceReferencePrice(string symbol, decimal quantity, string orderType)
        {
            // PEG_MID uses mid; everything else uses bid/ask side-aware reference.
            if (string.Equals(orderType, "PEG_MID", StringComparison.OrdinalIgnoreCase))
            {
                return ResolveMidPrice(symbol);
            }
            return ResolveAdaptiveLimitPrice(symbol, quantity);
        }

        private void ManageOpenOrders()
        {
            if (_exitRequested)
            {
                return;
            }
            if (Config.GetBool("lean-bridge-exit-on-submit", true))
            {
                return;
            }
            if (!_executed)
            {
                return;
            }
            if (!IsUnfilledManagementEnabled())
            {
                return;
            }
            if (_orderTickets.Count == 0)
            {
                return;
            }

            var nowUtc = DateTime.UtcNow;
            var timeoutSeconds = _executionParams.UnfilledTimeoutSeconds;
            var repriceIntervalSeconds = _executionParams.UnfilledRepriceIntervalSeconds;
            var maxReprices = _executionParams.UnfilledMaxReprices;
            var maxDeviationPct = _executionParams.UnfilledMaxPriceDeviationPct;

            foreach (var entry in _orderTickets)
            {
                var orderId = entry.Key;
                var ticket = entry.Value;
                if (ticket == null)
                {
                    continue;
                }
                if (_terminalOrderIds.Contains(orderId))
                {
                    continue;
                }

                Order order = null;
                try
                {
                    order = Transactions.GetOrderById(orderId);
                }
                catch
                {
                    order = null;
                }
                if (order == null)
                {
                    continue;
                }

                if (IsTerminalStatus(order.Status))
                {
                    _terminalOrderIds.Add(orderId);
                    continue;
                }

                var intentId = _orderIdToIntent.TryGetValue(orderId, out var mappedIntent) ? mappedIntent : "-";
                var symbol = order.Symbol?.Value ?? "-";

                var submittedAt = _orderSubmittedAtUtc.TryGetValue(orderId, out var subAt) ? subAt : nowUtc;
                var ageSeconds = (nowUtc - submittedAt).TotalSeconds;

                // 1) Hard timeout -> cancel
                if (timeoutSeconds > 0 && ageSeconds >= timeoutSeconds)
                {
                    if (_cancelRequestedOrderIds.Add(orderId))
                    {
                        Log($"LEAN_BRIDGE_UNFILLED_TIMEOUT: orderId={orderId} intent={intentId} symbol={symbol} status={order.Status} ageSeconds={(int)ageSeconds}");
                        ticket.Cancel();
                    }
                    continue;
                }

                // 2) Optional repricing for limit orders
                if (repriceIntervalSeconds <= 0 || maxReprices <= 0)
                {
                    continue;
                }

                if (order.Type != OrderType.Limit)
                {
                    continue;
                }

                var attempts = _orderRepriceAttempts.TryGetValue(orderId, out var a) ? a : 0;
                if (attempts >= maxReprices)
                {
                    if (_cancelRequestedOrderIds.Add(orderId))
                    {
                        Log($"LEAN_BRIDGE_UNFILLED_MAX_REPRICES: orderId={orderId} intent={intentId} symbol={symbol} attempts={attempts} max={maxReprices}");
                        ticket.Cancel();
                    }
                    continue;
                }

                var lastRepriceAt = _orderLastRepriceAtUtc.TryGetValue(orderId, out var lr) ? lr : submittedAt;
                var sinceLast = (nowUtc - lastRepriceAt).TotalSeconds;
                if (sinceLast < repriceIntervalSeconds)
                {
                    continue;
                }

                LimitOrder limitOrder = order as LimitOrder;
                if (limitOrder == null)
                {
                    continue;
                }
                var currentLimit = limitOrder.LimitPrice;
                if (currentLimit <= 0m)
                {
                    continue;
                }
                if (!_orderInitialLimitPrice.ContainsKey(orderId))
                {
                    _orderInitialLimitPrice[orderId] = currentLimit;
                }
                var initialLimit = _orderInitialLimitPrice.TryGetValue(orderId, out var init) ? init : currentLimit;

                var orderType = _orderIdToOrderType.TryGetValue(orderId, out var ot) ? ot : "LMT";
                var reference = ResolveRepriceReferencePrice(symbol, order.Quantity, orderType);
                if (reference <= 0m)
                {
                    continue;
                }

                var newLimit = currentLimit;
                if (order.Quantity > 0m)
                {
                    newLimit = System.Math.Max(currentLimit, reference);
                }
                else if (order.Quantity < 0m)
                {
                    newLimit = System.Math.Min(currentLimit, reference);
                }

                if (newLimit <= 0m || newLimit == currentLimit)
                {
                    continue;
                }

                // Deviation guard relative to the initial limit submitted.
                if (maxDeviationPct > 0m && initialLimit > 0m)
                {
                    var deviationPct = System.Math.Abs(newLimit - initialLimit) / initialLimit * 100m;
                    if (deviationPct > maxDeviationPct)
                    {
                        attempts += 1;
                        _orderRepriceAttempts[orderId] = attempts;
                        _orderLastRepriceAtUtc[orderId] = nowUtc;
                        Log($"LEAN_BRIDGE_REPRICE_BLOCKED: orderId={orderId} intent={intentId} symbol={symbol} currentLimit={currentLimit.ToString(CultureInfo.InvariantCulture)} candidate={newLimit.ToString(CultureInfo.InvariantCulture)} initial={initialLimit.ToString(CultureInfo.InvariantCulture)} deviationPct={deviationPct.ToString(CultureInfo.InvariantCulture)} maxDeviationPct={maxDeviationPct.ToString(CultureInfo.InvariantCulture)} attempts={attempts}/{maxReprices}");
                        continue;
                    }
                }

                var response = ticket.Update(new UpdateOrderFields { LimitPrice = newLimit });
                attempts += 1;
                _orderRepriceAttempts[orderId] = attempts;
                _orderLastRepriceAtUtc[orderId] = nowUtc;

                if (response != null && response.IsError)
                {
                    Log($"LEAN_BRIDGE_REPRICE_ERROR: orderId={orderId} intent={intentId} symbol={symbol} from={currentLimit.ToString(CultureInfo.InvariantCulture)} to={newLimit.ToString(CultureInfo.InvariantCulture)} error={response.ErrorCode} msg={response.ErrorMessage}");
                }
                else
                {
                    Log($"LEAN_BRIDGE_REPRICE: orderId={orderId} intent={intentId} symbol={symbol} from={currentLimit.ToString(CultureInfo.InvariantCulture)} to={newLimit.ToString(CultureInfo.InvariantCulture)} attempts={attempts}/{maxReprices}");
                }
            }

            // Defensive: ensure we can exit even if some terminal order events were missed.
            if (ShouldRequestAllTerminalExit(_exitRequested, _submissionCompleted, _intentToOrderIds, _terminalOrderIds))
            {
                Log($"LEAN_BRIDGE_ALL_TERMINAL_CHECK: intents={_intentToOrderIds.Count} orders={_orderIdToIntent.Count}");
                RequestExit("all_terminal_check");
            }
        }
    }
}
