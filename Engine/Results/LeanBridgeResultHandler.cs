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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Newtonsoft.Json.Linq;
using QuantConnect;
using QuantConnect.Configuration;
using QuantConnect.Brokerages;
using QuantConnect.Interfaces;
using QuantConnect.Orders;
using QuantConnect.Lean.Engine.TransactionHandlers;

namespace QuantConnect.Lean.Engine.Results
{
    public class LeanBridgeResultHandler : LiveTradingResultHandler
    {
        private LeanBridgeWriter _writer;
        private string _outputDir;
        private DateTime _nextSnapshotUtc;
        private DateTime _nextHeartbeatUtc;
        private DateTime _nextOpenOrdersUtc;
        private DateTime _nextExecutionsUtc;
        private DateTime _nextCommandsUtc;
        private TimeSpan _snapshotPeriod;
        private TimeSpan _heartbeatPeriod;
        private TimeSpan _openOrdersPeriod;
        private TimeSpan _executionsPeriod;
        private TimeSpan _commandsPeriod;
        private bool _commandsEnabled;
        private string _commandsDir;
        private string _commandsDoneDir;
        private string _commandsResultsDir;
        private string _lastError;
        private DateTime? _lastErrorAt;
        private bool _degraded;
        private readonly object _statusLock = new();
        private Timer _heartbeatTimer;
        private readonly object _commandsLock = new();
        private DateTime _executionsSinceUtc;
        private readonly Dictionary<string, DateTime> _seenExecutionIds = new(StringComparer.Ordinal);
        private readonly Dictionary<int, string> _knownOrderTags = new();

        public override void Initialize(ResultHandlerInitializeParameters parameters)
        {
            base.Initialize(parameters);
            var outputDir = Config.Get("lean-bridge-output-dir", Path.Combine(Globals.DataFolder, "lean_bridge"));
            _outputDir = outputDir;
            _snapshotPeriod = TimeSpan.FromSeconds(Config.GetInt("lean-bridge-snapshot-seconds", 2));
            _heartbeatPeriod = TimeSpan.FromSeconds(Config.GetInt("lean-bridge-heartbeat-seconds", 5));
            var openOrdersSeconds = Config.GetInt("lean-bridge-open-orders-seconds", 10);
            _openOrdersPeriod = openOrdersSeconds > 0 ? TimeSpan.FromSeconds(openOrdersSeconds) : TimeSpan.Zero;
            var executionsSeconds = Config.GetInt("lean-bridge-executions-seconds", 0);
            _executionsPeriod = executionsSeconds > 0 ? TimeSpan.FromSeconds(executionsSeconds) : TimeSpan.Zero;
            _writer = new LeanBridgeWriter(outputDir);
            _nextSnapshotUtc = DateTime.MinValue;
            _nextHeartbeatUtc = DateTime.MinValue;
            _nextOpenOrdersUtc = DateTime.MinValue;
            _nextExecutionsUtc = DateTime.MinValue;
            _nextCommandsUtc = DateTime.MinValue;
            _executionsSinceUtc = DateTime.MinValue;

            _commandsEnabled = Config.GetBool("lean-bridge-commands-enabled", false);
            if (_commandsEnabled)
            {
                var commandsSeconds = Config.GetInt("lean-bridge-commands-seconds", 1);
                _commandsPeriod = TimeSpan.FromSeconds(Math.Max(1, commandsSeconds));
                var commandsDir = Config.Get("lean-bridge-commands-dir", string.Empty);
                if (string.IsNullOrWhiteSpace(commandsDir))
                {
                    commandsDir = Path.Combine(outputDir, "commands");
                }
                _commandsDir = commandsDir;
                _commandsDoneDir = Path.Combine(commandsDir, "_done");
                _commandsResultsDir = Path.Combine(outputDir, "command_results");
                Directory.CreateDirectory(_commandsDir);
                Directory.CreateDirectory(_commandsDoneDir);
                Directory.CreateDirectory(_commandsResultsDir);
            }

            TryWriteStatus(DateTime.UtcNow);
            _heartbeatTimer = new Timer(_ => TryWriteStatus(DateTime.UtcNow), null, _heartbeatPeriod, _heartbeatPeriod);
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
            if (_openOrdersPeriod != TimeSpan.Zero && (forceProcess || now >= _nextOpenOrdersUtc))
            {
                _nextOpenOrdersUtc = now.Add(_openOrdersPeriod);
                TryWriteOpenOrders(now);
            }
            if (_executionsPeriod != TimeSpan.Zero && (forceProcess || now >= _nextExecutionsUtc))
            {
                _nextExecutionsUtc = now.Add(_executionsPeriod);
                TryBackfillIbExecutions(now);
            }
            if (_commandsEnabled && (forceProcess || now >= _nextCommandsUtc))
            {
                _nextCommandsUtc = now.Add(_commandsPeriod);
                TryProcessCommands(now);
            }
        }

        public override void Exit()
        {
            _heartbeatTimer?.Dispose();
            _heartbeatTimer = null;
            base.Exit();
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

        private void TryWriteOpenOrders(DateTime now)
        {
            try
            {
                _writer.WriteJsonAtomic("open_orders.json", BuildOpenOrders(now));
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = now;
                _degraded = true;
            }
        }

        private void TryProcessCommands(DateTime now)
        {
            try
            {
                ProcessCommands(now);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = now;
                _degraded = true;
            }
        }

        private static string ReadTextSafe(string path)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch
            {
                return null;
            }
        }

        private static string TokenString(JObject obj, params string[] keys)
        {
            if (obj == null || keys == null) return null;
            foreach (var key in keys)
            {
                try
                {
                    var token = obj[key];
                    if (token == null) continue;
                    var text = token.Type == JTokenType.String ? token.Value<string>() : token.ToString();
                    if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
                }
                catch
                {
                    // ignored
                }
            }
            return null;
        }

        private static int? TokenInt(JObject obj, params string[] keys)
        {
            var text = TokenString(obj, keys);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
            return null;
        }

        private static decimal? TokenDecimal(JObject obj, params string[] keys)
        {
            var text = TokenString(obj, keys);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
            return null;
        }

        private static bool TokenBool(JObject obj, bool defaultValue, params string[] keys)
        {
            var text = TokenString(obj, keys);
            if (string.IsNullOrWhiteSpace(text))
            {
                return defaultValue;
            }
            if (bool.TryParse(text, out var parsed))
            {
                return parsed;
            }
            switch (text.Trim().ToLowerInvariant())
            {
                case "1":
                case "yes":
                case "y":
                case "on":
                    return true;
                case "0":
                case "no":
                case "n":
                case "off":
                    return false;
                default:
                    return defaultValue;
            }
        }

        private static string NormalizeBridgeOrderType(string value)
        {
            var text = (value ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(text)) return "MKT";
            if (text == "MARKET" || text == "MARKET_ORDER") return "MKT";
            if (text == "LIMIT" || text == "LIMIT_ORDER") return "LMT";
            if (text == "ADAPTIVE" || text == "ADAPTIVELMT" || text == "ADAPTIVE_LIMIT") return "ADAPTIVE_LMT";
            return text;
        }

        private static DateTime? TokenUtcTime(JObject obj, params string[] keys)
        {
            var text = TokenString(obj, keys);
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt))
            {
                return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }
            return null;
        }

        private void WriteCommandResult(string commandId, Dictionary<string, object> payload)
        {
            if (string.IsNullOrWhiteSpace(commandId) || payload == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_commandsResultsDir);
                _writer.WriteJsonAtomic(Path.Combine("command_results", $"{commandId}.json"), payload);
            }
            catch
            {
                // avoid impacting execution
            }
        }

        private void MarkCommandDone(string path, string commandId)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(_commandsDoneDir);
                var doneName = Path.GetFileName(path);
                var donePath = Path.Combine(_commandsDoneDir, doneName);
                File.Move(path, donePath, true);
            }
            catch
            {
                try
                {
                    // Best-effort: if we can't move, leave it in place for retry; do not delete.
                    if (!string.IsNullOrWhiteSpace(commandId))
                    {
                        WriteCommandResult(commandId, new Dictionary<string, object>
                        {
                            ["command_id"] = commandId,
                            ["type"] = "cancel_order",
                            ["status"] = "done_move_failed",
                            ["processed_at"] = DateTime.UtcNow.ToString("O"),
                        });
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }

        private void ProcessCommands(DateTime now)
        {
            if (!_commandsEnabled)
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(_commandsDir))
            {
                return;
            }

            // Single-thread command loop to avoid overlapping GetOpenOrders/CancelOrder calls.
            lock (_commandsLock)
            {
                if (!Directory.Exists(_commandsDir))
                {
                    return;
                }

                var files = Directory.GetFiles(_commandsDir, "*.json", SearchOption.TopDirectoryOnly);
                if (files == null || files.Length == 0)
                {
                    return;
                }

                if (TransactionHandler is not BrokerageTransactionHandler brokerageTransactionHandler)
                {
                    return;
                }
                var brokerage = brokerageTransactionHandler.Brokerage;
                if (brokerage == null)
                {
                    return;
                }

                // Parse commands first; only query open orders if we have at least one valid cancel request.
                var commands = new List<(string Path, string CommandId, int? OrderId, string Tag, DateTime? ExpiresAtUtc)>();
                var submitCommands = new List<(
                    string Path,
                    string CommandId,
                    int? OrderId,
                    string Tag,
                    string Symbol,
                    decimal Quantity,
                    string OrderType,
                    decimal? LimitPrice,
                    bool OutsideRth,
                    string AdaptivePriority
                )>();
                foreach (var path in files.OrderBy(x => x))
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    if (path.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;

                    var text = ReadTextSafe(path);
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        continue;
                    }

                    JObject obj;
                    try
                    {
                        obj = JObject.Parse(text);
                    }
                    catch (Exception ex)
                    {
                        var typeFromName = Path.GetFileNameWithoutExtension(path)?.StartsWith("submit_order", StringComparison.OrdinalIgnoreCase) == true
                            ? "submit_order"
                            : "cancel_order";
                        var parseId = Path.GetFileNameWithoutExtension(path) ?? "cancel_order";
                        WriteCommandResult(parseId, new Dictionary<string, object>
                        {
                            ["command_id"] = parseId,
                            ["type"] = typeFromName,
                            ["status"] = "parse_error",
                            ["processed_at"] = now.ToString("O"),
                            ["error"] = ex.Message,
                        });
                        MarkCommandDone(path, parseId);
                        continue;
                    }

                    var type = (TokenString(obj, "type", "Type") ?? string.Empty).Trim();
                    if (string.Equals(type, "submit_order", StringComparison.OrdinalIgnoreCase))
                    {
                        var submitCommandId = TokenString(obj, "command_id", "commandId", "id", "command") ?? Path.GetFileNameWithoutExtension(path);
                        if (string.IsNullOrWhiteSpace(submitCommandId))
                        {
                            submitCommandId = Path.GetFileNameWithoutExtension(path);
                        }
                        var submitExpiresAt = TokenUtcTime(obj, "expires_at", "expiresAt");
                        if (submitExpiresAt.HasValue && submitExpiresAt.Value <= now)
                        {
                            WriteCommandResult(submitCommandId, new Dictionary<string, object>
                            {
                                ["command_id"] = submitCommandId,
                                ["type"] = "submit_order",
                                ["status"] = "expired",
                                ["processed_at"] = now.ToString("O"),
                                ["expires_at"] = submitExpiresAt.Value.ToString("O"),
                            });
                            MarkCommandDone(path, submitCommandId);
                            continue;
                        }

                        var symbol = (TokenString(obj, "symbol", "Symbol") ?? string.Empty).Trim().ToUpperInvariant();
                        if (string.IsNullOrWhiteSpace(symbol))
                        {
                            WriteCommandResult(submitCommandId, new Dictionary<string, object>
                            {
                                ["command_id"] = submitCommandId,
                                ["type"] = "submit_order",
                                ["status"] = "symbol_invalid",
                                ["processed_at"] = now.ToString("O"),
                                ["error"] = "symbol_required",
                            });
                            MarkCommandDone(path, submitCommandId);
                            continue;
                        }

                        var quantity = TokenDecimal(obj, "quantity", "Quantity");
                        if (!quantity.HasValue || quantity.Value == 0m)
                        {
                            WriteCommandResult(submitCommandId, new Dictionary<string, object>
                            {
                                ["command_id"] = submitCommandId,
                                ["type"] = "submit_order",
                                ["status"] = "quantity_invalid",
                                ["processed_at"] = now.ToString("O"),
                                ["symbol"] = symbol,
                                ["error"] = "quantity_required",
                            });
                            MarkCommandDone(path, submitCommandId);
                            continue;
                        }

                        var orderType = NormalizeBridgeOrderType(TokenString(obj, "order_type", "orderType", "type_name", "orderTypeName"));
                        if (!(orderType == "MKT" || orderType == "LMT" || orderType == "ADAPTIVE_LMT"))
                        {
                            WriteCommandResult(submitCommandId, new Dictionary<string, object>
                            {
                                ["command_id"] = submitCommandId,
                                ["type"] = "submit_order",
                                ["status"] = "unsupported_order_type",
                                ["processed_at"] = now.ToString("O"),
                                ["symbol"] = symbol,
                                ["order_type"] = orderType,
                            });
                            MarkCommandDone(path, submitCommandId);
                            continue;
                        }

                        var limitPrice = TokenDecimal(obj, "limit_price", "limitPrice");
                        if (orderType == "LMT" && (!limitPrice.HasValue || limitPrice.Value <= 0m))
                        {
                            WriteCommandResult(submitCommandId, new Dictionary<string, object>
                            {
                                ["command_id"] = submitCommandId,
                                ["type"] = "submit_order",
                                ["status"] = "limit_price_invalid",
                                ["processed_at"] = now.ToString("O"),
                                ["symbol"] = symbol,
                                ["order_type"] = orderType,
                            });
                            MarkCommandDone(path, submitCommandId);
                            continue;
                        }

                        var submitTag = TokenString(obj, "tag", "Tag", "order_tag", "orderTag");
                        if (string.IsNullOrWhiteSpace(submitTag))
                        {
                            submitTag = submitCommandId;
                        }
                        var submitOrderId = TokenInt(obj, "order_id", "orderId");
                        var outsideRth = TokenBool(obj, false, "outside_rth", "outsideRth");
                        var adaptivePriority = TokenString(obj, "adaptive_priority", "adaptivePriority");
                        submitCommands.Add((path, submitCommandId, submitOrderId, submitTag, symbol, quantity.Value, orderType, limitPrice, outsideRth, adaptivePriority));
                        continue;
                    }

                    if (!string.Equals(type, "cancel_order", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var commandId = TokenString(obj, "command_id", "commandId", "id", "command") ?? Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrWhiteSpace(commandId))
                    {
                        commandId = Path.GetFileNameWithoutExtension(path);
                    }
                    var tag = TokenString(obj, "tag", "Tag", "order_tag", "orderTag");
                    var orderId = TokenInt(obj, "order_id", "orderId");
                    var expiresAt = TokenUtcTime(obj, "expires_at", "expiresAt");

                    if (expiresAt.HasValue && expiresAt.Value <= now)
                    {
                        WriteCommandResult(commandId, new Dictionary<string, object>
                        {
                            ["command_id"] = commandId,
                            ["type"] = "cancel_order",
                            ["status"] = "expired",
                            ["processed_at"] = now.ToString("O"),
                            ["order_id"] = orderId,
                            ["tag"] = tag,
                            ["expires_at"] = expiresAt.Value.ToString("O"),
                        });
                        MarkCommandDone(path, commandId);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        WriteCommandResult(commandId, new Dictionary<string, object>
                        {
                            ["command_id"] = commandId,
                            ["type"] = "cancel_order",
                            ["status"] = "invalid",
                            ["processed_at"] = now.ToString("O"),
                            ["order_id"] = orderId,
                            ["error"] = "tag_missing",
                        });
                        MarkCommandDone(path, commandId);
                        continue;
                    }

                    commands.Add((path, commandId, orderId, tag, expiresAt));
                }

                if (commands.Count == 0 && submitCommands.Count == 0)
                {
                    return;
                }

                List<Order> openOrders = new List<Order>();
                if (commands.Count > 0)
                {
                    try
                    {
                        openOrders = brokerage.GetOpenOrders() ?? new List<Order>();
                    }
                    catch (Exception ex)
                    {
                        _lastError = ex.Message;
                        _lastErrorAt = now;
                        _degraded = true;
                        openOrders = new List<Order>();
                    }
                }

                foreach (var cmd in commands)
                {
                    var path = cmd.Path;
                    var commandId = cmd.CommandId;
                    var orderId = cmd.OrderId;
                    var tag = cmd.Tag;

                    var matches = openOrders
                        .Where(order => order != null && string.Equals(order.Tag, tag, StringComparison.Ordinal))
                        .ToList();

                    var sent = 0;
                    var brokerageIds = new List<string>();
                    var symbols = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var order in matches)
                    {
                        if (order?.Symbol != null && !string.IsNullOrWhiteSpace(order.Symbol.Value))
                        {
                            symbols.Add(order.Symbol.Value);
                        }
                        if (order?.BrokerId != null && order.BrokerId.Count > 0)
                        {
                            brokerageIds.AddRange(order.BrokerId);
                        }
                        try
                        {
                            if (brokerage.CancelOrder(order))
                            {
                                sent += 1;
                            }
                        }
                        catch (Exception ex)
                        {
                            _lastError = ex.Message;
                            _lastErrorAt = now;
                            _degraded = true;
                        }
                    }

                    var status = matches.Count > 0 ? "cancel_sent" : "not_found";
                    WriteCommandResult(commandId, new Dictionary<string, object>
                    {
                        ["command_id"] = commandId,
                        ["type"] = "cancel_order",
                        ["status"] = status,
                        ["processed_at"] = now.ToString("O"),
                        ["order_id"] = orderId,
                        ["tag"] = tag,
                        ["found"] = matches.Count,
                        ["sent"] = sent,
                        ["symbols"] = symbols.OrderBy(x => x).ToList(),
                        ["brokerage_ids"] = brokerageIds.Distinct().OrderBy(x => x).ToList(),
                        ["source"] = "lean_bridge",
                        ["source_detail"] = "leader_cancel",
                    });
                    MarkCommandDone(path, commandId);
                }

                foreach (var cmd in submitCommands)
                {
                    var status = "place_failed";
                    var err = string.Empty;
                    var brokerIds = new List<string>();
                    int? leanOrderId = null;

                    try
                    {
                        if (!brokerage.IsConnected)
                        {
                            status = "not_connected";
                        }
                        else
                        {
                            var symbol = Symbol.Create(cmd.Symbol, QuantConnect.SecurityType.Equity, Market.USA);
                            InteractiveBrokersOrderProperties ibProps = null;
                            if (cmd.OutsideRth || cmd.OrderType == "ADAPTIVE_LMT")
                            {
                                ibProps = new InteractiveBrokersOrderProperties();
                                if (cmd.OutsideRth)
                                {
                                    ibProps.OutsideRegularTradingHours = true;
                                }
                            }
                            if (cmd.OrderType == "ADAPTIVE_LMT")
                            {
                                ibProps ??= new InteractiveBrokersOrderProperties();
                                ibProps.AlgoStrategy = "Adaptive";
                                var priority = string.IsNullOrWhiteSpace(cmd.AdaptivePriority) ? "Normal" : cmd.AdaptivePriority.Trim();
                                ibProps.AlgoParams = new Dictionary<string, string>
                                {
                                    { "adaptivePriority", priority }
                                };
                            }

                            Order order = cmd.OrderType == "LMT"
                                ? new LimitOrder(symbol, cmd.Quantity, cmd.LimitPrice ?? 0m, now, cmd.Tag, ibProps)
                                : new MarketOrder(symbol, cmd.Quantity, now, cmd.Tag, ibProps);

                            if (Algorithm?.Transactions != null)
                            {
                                order.Id = Algorithm.Transactions.GetIncrementOrderId();
                            }
                            if (order.Id <= 0)
                            {
                                order.Id = Math.Abs((int)(DateTime.UtcNow.Ticks % int.MaxValue));
                            }
                            leanOrderId = order.Id;

                            if (brokerage.PlaceOrder(order))
                            {
                                status = "submitted";
                                _knownOrderTags[order.Id] = cmd.Tag;
                                if (order.BrokerId != null && order.BrokerId.Count > 0)
                                {
                                    brokerIds.AddRange(order.BrokerId);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        status = "place_failed";
                        err = ex.Message;
                        _lastError = ex.Message;
                        _lastErrorAt = now;
                        _degraded = true;
                    }

                    WriteCommandResult(cmd.CommandId, new Dictionary<string, object>
                    {
                        ["command_id"] = cmd.CommandId,
                        ["type"] = "submit_order",
                        ["status"] = status,
                        ["processed_at"] = now.ToString("O"),
                        ["order_id"] = cmd.OrderId,
                        ["lean_order_id"] = leanOrderId,
                        ["symbol"] = cmd.Symbol,
                        ["quantity"] = cmd.Quantity,
                        ["order_type"] = cmd.OrderType,
                        ["tag"] = cmd.Tag,
                        ["outside_rth"] = cmd.OutsideRth,
                        ["brokerage_ids"] = brokerIds.Distinct().OrderBy(x => x).ToList(),
                        ["source"] = "lean_bridge",
                        ["source_detail"] = "leader_submit",
                        ["error"] = err,
                    });
                    MarkCommandDone(cmd.Path, cmd.CommandId);
                }
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
                lock (_statusLock)
                {
                    _writer.WriteJsonAtomic("lean_bridge_status.json", payload);
                }
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
                string tag = null;
                if (TransactionHandler?.Orders != null
                    && TransactionHandler.Orders.TryGetValue(newEvent.OrderId, out var order))
                {
                    tag = order.Tag;
                }
                if (!string.IsNullOrWhiteSpace(tag))
                {
                    _knownOrderTags[newEvent.OrderId] = tag;
                }
                _writer.AppendJsonLine("execution_events.jsonl", new Dictionary<string, object>
                {
                    ["order_id"] = newEvent.OrderId,
                    ["symbol"] = newEvent.Symbol?.Value,
                    ["status"] = newEvent.Status.ToString(),
                    ["filled"] = newEvent.FillQuantity,
                    ["fill_price"] = newEvent.FillPrice,
                    ["direction"] = newEvent.Direction.ToString(),
                    ["time"] = newEvent.UtcTime.ToString("O"),
                    ["tag"] = tag,
                    ["message"] = newEvent.Message
                });
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = DateTime.UtcNow;
                _degraded = true;
            }
        }

        private void TryBackfillIbExecutions(DateTime now)
        {
            try
            {
                if (TransactionHandler is not BrokerageTransactionHandler brokerageTransactionHandler)
                {
                    return;
                }

                var brokerage = brokerageTransactionHandler.Brokerage;
                if (brokerage == null)
                {
                    return;
                }

                // Use reflection to avoid hard coupling to a specific brokerage assembly.
                var getExecutionsAllClients = brokerage.GetType().GetMethod(
                    "GetExecutions",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(string), typeof(string), typeof(string), typeof(DateTime?), typeof(string), typeof(int?) },
                    modifiers: null
                );
                var getExecutions = getExecutionsAllClients ?? brokerage.GetType().GetMethod(
                    "GetExecutions",
                    BindingFlags.Instance | BindingFlags.Public,
                    binder: null,
                    types: new[] { typeof(string), typeof(string), typeof(string), typeof(DateTime?), typeof(string) },
                    modifiers: null
                );
                if (getExecutions == null)
                {
                    return;
                }

                RefreshKnownOrderTags(brokerage);
                var sinceUtc = _executionsSinceUtc == DateTime.MinValue ? now.AddHours(-24) : _executionsSinceUtc.AddSeconds(-2);
                var detailsObj = getExecutionsAllClients != null
                    ? getExecutionsAllClients.Invoke(brokerage, new object[] { null, null, null, sinceUtc, null, 0 })
                    : getExecutions.Invoke(brokerage, new object[] { null, null, null, sinceUtc, null });
                var details = detailsObj as System.Collections.IEnumerable;
                if (details == null)
                {
                    _executionsSinceUtc = now;
                    return;
                }

                var maxExecutionUtc = sinceUtc;
                var wroteAny = false;
                foreach (var detail in details)
                {
                    if (detail == null)
                    {
                        continue;
                    }

                    var execution = GetObjectProperty(detail, "Execution");
                    if (execution == null)
                    {
                        continue;
                    }

                    var execId = GetStringProperty(execution, "ExecId");
                    if (string.IsNullOrWhiteSpace(execId))
                    {
                        continue;
                    }
                    if (_seenExecutionIds.ContainsKey(execId))
                    {
                        continue;
                    }

                    var orderId = GetIntProperty(execution, "OrderId");
                    var quantity = GetDecimalProperty(execution, "Shares");
                    if (!orderId.HasValue || !quantity.HasValue || quantity.Value == 0m)
                    {
                        continue;
                    }

                    var direction = NormalizeExecutionDirection(GetStringProperty(execution, "Side"));
                    var signedQuantity = Math.Abs(quantity.Value);
                    if (string.Equals(direction, "Sell", StringComparison.OrdinalIgnoreCase))
                    {
                        signedQuantity = -signedQuantity;
                    }
                    var fillPrice = GetDecimalProperty(execution, "Price") ?? 0m;

                    var eventUtc = ParseIbExecutionTime(GetStringProperty(execution, "Time"), now);
                    var tag = GetStringProperty(execution, "OrderRef");
                    if (string.IsNullOrWhiteSpace(tag) && _knownOrderTags.TryGetValue(orderId.Value, out var knownTag))
                    {
                        tag = knownTag;
                    }

                    _seenExecutionIds[execId] = eventUtc;
                    if (eventUtc > maxExecutionUtc)
                    {
                        maxExecutionUtc = eventUtc;
                    }

                    if (string.IsNullOrWhiteSpace(tag))
                    {
                        continue;
                    }

                    _knownOrderTags[orderId.Value] = tag;
                    var contract = GetObjectProperty(detail, "Contract");
                    var symbol = GetStringProperty(contract, "Symbol");
                    _writer.AppendJsonLine("execution_events.jsonl", new Dictionary<string, object>
                    {
                        ["order_id"] = orderId.Value,
                        ["symbol"] = symbol,
                        ["status"] = "Filled",
                        ["filled"] = signedQuantity,
                        ["fill_price"] = fillPrice,
                        ["direction"] = direction,
                        ["time"] = eventUtc.ToString("O"),
                        ["tag"] = tag,
                        ["exec_id"] = execId,
                        ["source"] = "lean_bridge",
                        ["source_detail"] = "ib_executions_poll",
                    });
                    wroteAny = true;
                }

                _executionsSinceUtc = wroteAny ? maxExecutionUtc : now;
                PruneSeenExecutionIds(now);
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = now;
                _degraded = true;
            }
        }

        private void RefreshKnownOrderTags(IBrokerage brokerage)
        {
            try
            {
                var openOrders = brokerage.GetOpenOrders();
                if (openOrders == null || openOrders.Count == 0)
                {
                    return;
                }

                foreach (var order in openOrders)
                {
                    if (order == null || string.IsNullOrWhiteSpace(order.Tag))
                    {
                        continue;
                    }

                    if (order.Id > 0)
                    {
                        _knownOrderTags[order.Id] = order.Tag;
                    }
                    if (order.BrokerId == null)
                    {
                        continue;
                    }
                    foreach (var brokerId in order.BrokerId)
                    {
                        if (!int.TryParse(brokerId, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                        {
                            continue;
                        }
                        _knownOrderTags[parsed] = order.Tag;
                    }
                }
            }
            catch
            {
                // Best effort only
            }
        }

        private void PruneSeenExecutionIds(DateTime now)
        {
            if (_seenExecutionIds.Count < 5000)
            {
                return;
            }

            var cutoff = now.AddDays(-2);
            var staleKeys = _seenExecutionIds
                .Where(item => item.Value < cutoff)
                .Select(item => item.Key)
                .ToList();
            foreach (var key in staleKeys)
            {
                _seenExecutionIds.Remove(key);
            }
        }

        private static object GetObjectProperty(object target, string propertyName)
        {
            if (target == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return null;
            }

            try
            {
                return target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public)?.GetValue(target);
            }
            catch
            {
                return null;
            }
        }

        private static string GetStringProperty(object target, string propertyName)
        {
            var value = GetObjectProperty(target, propertyName);
            return value?.ToString();
        }

        private static int? GetIntProperty(object target, string propertyName)
        {
            var value = GetObjectProperty(target, propertyName);
            if (value == null)
            {
                return null;
            }

            try
            {
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static decimal? GetDecimalProperty(object target, string propertyName)
        {
            var value = GetObjectProperty(target, propertyName);
            if (value == null)
            {
                return null;
            }

            switch (value)
            {
                case decimal decimalValue:
                    return decimalValue;
                case double doubleValue:
                    return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
                case float floatValue:
                    return Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
                case int intValue:
                    return intValue;
                case long longValue:
                    return longValue;
                case string text when decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed):
                    return parsed;
            }

            try
            {
                return Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            }
            catch
            {
                return null;
            }
        }

        private static string NormalizeExecutionDirection(string side)
        {
            var value = (side ?? string.Empty).Trim().ToUpperInvariant();
            if (value == "SLD" || value == "SELL")
            {
                return "Sell";
            }
            if (value == "BOT" || value == "BUY")
            {
                return "Buy";
            }
            return string.IsNullOrWhiteSpace(value) ? "Buy" : value;
        }

        private static DateTime ParseIbExecutionTime(string raw, DateTime fallbackUtc)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return fallbackUtc;
            }

            var normalized = raw.Trim();
            var candidates = new List<string> { normalized };
            if (normalized.Length >= 17)
            {
                candidates.Add(normalized.Substring(0, 17));
            }

            var formats = new[]
            {
                "yyyyMMdd  HH:mm:ss",
                "yyyyMMdd HH:mm:ss",
                "yyyy-MM-dd HH:mm:ss",
            };

            foreach (var candidate in candidates)
            {
                foreach (var format in formats)
                {
                    if (!DateTime.TryParseExact(
                        candidate,
                        format,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                        out var parsed))
                    {
                        continue;
                    }
                    return parsed.ToUniversalTime();
                }

                if (DateTime.TryParse(
                    candidate,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces,
                    out var parsedLoose))
                {
                    return parsedLoose.ToUniversalTime();
                }
            }

            return fallbackUtc;
        }

        private Dictionary<string, object> BuildAccountSummary(DateTime now)
        {
            var items = TryBuildIbAccountSummary();
            var stale = items.Count == 0;
            return new Dictionary<string, object>
            {
                ["items"] = items,
                ["refreshed_at"] = now.ToString("O"),
                ["source"] = "lean_bridge",
                ["source_detail"] = items.Count == 0 ? "ib_account_empty" : "ib_account_merge",
                ["stale"] = stale
            };
        }

        private Dictionary<string, object> TryBuildIbAccountSummary()
        {
            if (TransactionHandler is not BrokerageTransactionHandler brokerageTransactionHandler)
            {
                return new Dictionary<string, object>();
            }

            if (brokerageTransactionHandler.Brokerage is not IAccountSummaryProvider accountSummaryProvider)
            {
                return new Dictionary<string, object>();
            }

            var snapshot = accountSummaryProvider.GetAccountSummarySnapshot();
            if (snapshot == null || snapshot.Count == 0)
            {
                return new Dictionary<string, object>();
            }

            var items = new Dictionary<string, object>();
            foreach (var tag in new[]
            {
                "NetLiquidation",
                "TotalCashValue",
                "AvailableFunds",
                "BuyingPower",
                "UnrealizedPnL",
                "TotalHoldingsValue",
                "CashBalance",
                "EquityWithLoanValue",
                "GrossPositionValue",
                "InitMarginReq",
                "MaintMarginReq"
            })
            {
                if (TryGetSnapshotValue(snapshot, "BASE", tag, out var value)
                    || TryGetSnapshotValueAnyCurrency(snapshot, tag, out value))
                {
                    items[tag] = ParseSnapshotValue(value);
                }
            }
            return items;
        }

        private static object ParseSnapshotValue(string value)
        {
            if (decimal.TryParse(value, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }
            return value;
        }

        private static bool TryGetSnapshotValue(
            Dictionary<string, string> snapshot,
            string currency,
            string tag,
            out string value)
        {
            if (snapshot.TryGetValue($"{currency}:{tag}", out value))
            {
                return !string.IsNullOrEmpty(value);
            }
            value = null;
            return false;
        }

        private static bool TryGetSnapshotValueAnyCurrency(Dictionary<string, string> snapshot, string tag, out string value)
        {
            foreach (var entry in snapshot)
            {
                if (entry.Key.EndsWith($":{tag}", StringComparison.Ordinal))
                {
                    value = entry.Value;
                    return !string.IsNullOrEmpty(value);
                }
            }
            value = null;
            return false;
        }

        private static bool TryGetSummaryDecimal(Dictionary<string, object> summary, string key, out decimal value)
        {
            value = 0m;
            if (summary == null || !summary.TryGetValue(key, out var raw) || raw == null)
            {
                return false;
            }

            switch (raw)
            {
                case decimal parsed:
                    value = parsed;
                    return true;
                case int intValue:
                    value = intValue;
                    return true;
                case long longValue:
                    value = longValue;
                    return true;
                case double doubleValue:
                    value = Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
                    return true;
                case float floatValue:
                    value = Convert.ToDecimal(floatValue, CultureInfo.InvariantCulture);
                    return true;
            }

            return decimal.TryParse(raw.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out value);
        }

        private Dictionary<string, object> BuildPositions(DateTime now)
        {
            var list = new List<Dictionary<string, object>>();
            var sourceDetail = "ib_holdings_empty";
            var accountSummary = TryBuildIbAccountSummary();
            var hasHoldingsSummary =
                (TryGetSummaryDecimal(accountSummary, "GrossPositionValue", out var grossPositionValue)
                 && grossPositionValue > 0m)
                || (TryGetSummaryDecimal(accountSummary, "TotalHoldingsValue", out var totalHoldingsValue)
                    && totalHoldingsValue > 0m);

            if (TransactionHandler is BrokerageTransactionHandler brokerageTransactionHandler)
            {
                var brokerage = brokerageTransactionHandler.Brokerage;
                var brokerageHoldings = brokerage?.GetAccountHoldings();
                if ((brokerageHoldings == null || brokerageHoldings.Count == 0) && hasHoldingsSummary)
                {
                    if (brokerage is IAccountHoldingsRefresher refresher && refresher.RefreshAccountHoldings())
                    {
                        brokerageHoldings = brokerage?.GetAccountHoldings();
                    }

                    if (brokerageHoldings == null || brokerageHoldings.Count == 0)
                    {
                        brokerage?.Disconnect();
                        brokerage?.Connect();
                        brokerageHoldings = brokerage?.GetAccountHoldings();
                    }
                }
                if (brokerageHoldings != null && brokerageHoldings.Count > 0)
                {
                    sourceDetail = "ib_holdings";
                    foreach (var holding in brokerageHoldings)
                    {
                        list.Add(new Dictionary<string, object>
                        {
                            ["symbol"] = holding.Symbol.Value,
                            ["quantity"] = holding.Quantity,
                            ["avg_cost"] = holding.AveragePrice,
                            ["market_value"] = holding.MarketValue,
                            ["unrealized_pnl"] = holding.UnrealizedPnL,
                            ["currency"] = holding.CurrencySymbol
                        });
                    }
                }
            }
            var stale = list.Count == 0 && hasHoldingsSummary;
            return new Dictionary<string, object>
            {
                ["items"] = list,
                ["refreshed_at"] = now.ToString("O"),
                ["source"] = "lean_bridge",
                ["source_detail"] = sourceDetail,
                ["stale"] = stale
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
                    ["timestamp"] = Algorithm.UtcTime.ToString("O")
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

        private Dictionary<string, object> BuildOpenOrders(DateTime now)
        {
            var list = new List<Dictionary<string, object>>();
            var stale = false;
            var sourceDetail = "brokerage_unavailable";

            try
            {
                if (TransactionHandler is BrokerageTransactionHandler brokerageTransactionHandler)
                {
                    var brokerage = brokerageTransactionHandler.Brokerage;
                    if (brokerage != null)
                    {
                        sourceDetail = "ib_open_orders_empty";
                        var openOrders = brokerage.GetOpenOrders();
                        if (openOrders != null && openOrders.Count > 0)
                        {
                            sourceDetail = "ib_open_orders";
                            foreach (var order in openOrders)
                            {
                                if (order == null) continue;
                                var record = new Dictionary<string, object>
                                {
                                    ["id"] = order.Id,
                                    ["symbol"] = order.Symbol?.Value,
                                    ["quantity"] = order.Quantity,
                                    ["direction"] = order.Direction.ToString(),
                                    ["type"] = order.Type.ToString(),
                                    ["status"] = order.Status.ToString(),
                                    ["tag"] = order.Tag,
                                    ["time"] = order.Time.ToString("O"),
                                };
                                if (order.BrokerId != null && order.BrokerId.Count > 0)
                                {
                                    record["brokerage_ids"] = order.BrokerId;
                                }
                                if (order is LimitOrder limit)
                                {
                                    record["limit_price"] = limit.LimitPrice;
                                }
                                list.Add(record);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                _lastErrorAt = now;
                _degraded = true;
                stale = true;
                sourceDetail = "ib_open_orders_error";
            }

            return new Dictionary<string, object>
            {
                ["items"] = list,
                ["refreshed_at"] = now.ToString("O"),
                ["source"] = "lean_bridge",
                ["source_detail"] = sourceDetail,
                ["stale"] = stale
            };
        }
    }
}
