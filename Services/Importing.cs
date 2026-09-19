using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class ImportService
{
    private static readonly Regex SierraTimeframePattern = new(@"(?<!\d)(?<minutes>\d+)\s+min\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly TradeFoundryDb _database;

    public ImportService(TradeFoundryDb database) => _database = database;

    public async Task<ImportResult> ImportAsync(Guid journalId, string fileName, Stream content, string requestedType, string groupingPolicy, string interval, CancellationToken cancellationToken = default, string benchmarkSymbol = "SPY", string barSymbol = "", string? timeZone = null)
    {
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);
        var text = Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
        return ImportText(journalId, fileName, text, requestedType, groupingPolicy, interval, benchmarkSymbol, barSymbol, timeZone);
    }

    public ImportResult ImportText(Guid journalId, string fileName, string text, string requestedType, string groupingPolicy, string interval, string benchmarkSymbol = "SPY", string barSymbol = "", string? timeZone = null)
    {
        text = (text ?? string.Empty).TrimStart('\uFEFF');
        var journal = _database.GetJournal(journalId);
        var journalTimeZone = TimeZoneCatalog.CanonicalId(journal?.TimeZone ?? "UTC");
        var requestedTimeZone = string.IsNullOrWhiteSpace(timeZone) ? TimeZoneCatalog.Auto : timeZone;
        var importTimeZone = ResolveSourceTimeZone(text, requestedType, requestedTimeZone, journalTimeZone);
        var parsed = Parse(text, requestedType, interval, importTimeZone, benchmarkSymbol, _database.GetInstrumentConfiguration(), barSymbol);
        return _database.CommitImport(journalId, fileName, parsed, groupingPolicy, interval, importTimeZone);
    }

    public ParsedImport Parse(string text, string requestedType = "auto", string interval = "source", string timeZone = "UTC", string benchmarkSymbol = "SPY", InstrumentConfiguration? configuration = null, string barSymbol = "")
    {
        configuration ??= InstrumentConfiguration.Empty;
        var result = new ParsedImport();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var headerLine = lines.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            result.Warnings.Add("The file is empty.");
            return result;
        }

        var delimiter = ChooseDelimiter(headerLine);
        var headers = ParseDelimitedLine(headerLine, delimiter).Select(NormalizeHeader).ToArray();
        var source = DetectSource(headers, requestedType);
        result = source switch
        {
            ImportSource.Sierra => ParseSierra(lines, headerLine, headers, delimiter, timeZone, configuration),
            ImportSource.TradingViewAccount => ParseTradingViewAccount(lines, headerLine, headers, delimiter, timeZone, configuration),
            ImportSource.TradingViewStrategy => ParseTradingViewStrategy(lines, headerLine, headers, delimiter, timeZone, configuration),
            ImportSource.Benchmark => ParseBenchmark(lines, headerLine, headers, delimiter, timeZone, benchmarkSymbol),
            ImportSource.SierraBars => ParseBars(lines, headerLine, headers, delimiter, interval, timeZone, barSymbol, TradeFoundryConstants.SierraOhlcBars),
            _ => ParseBars(lines, headerLine, headers, delimiter, interval, timeZone, barSymbol, TradeFoundryConstants.OhlcvBars)
        };
        return result;
    }

    private static ParsedImport ParseSierra(string[] lines, string headerLine, string[] headers, char delimiter, string timeZone, InstrumentConfiguration configuration)
    {
        var result = NewResult(TradeFoundryConstants.SierraFills);
        foreach (var (row, rowNumber) in Rows(lines, headerLine, headers, delimiter))
        {
            var payload = JsonSerializer.Serialize(row);
            var activity = Value(row, headers, "activitytype", "activity");
            var serviceId = Value(row, headers, "fillexecutionserviceid");
            // A fill execution id can also appear on an Orders row. Use the complete
            // row fingerprint so the fill and every lifecycle update remain distinct
            // while identical duplicate rows still deduplicate cleanly.
            var sourceKey = Fingerprint(result.SourceType, row);
            FillDraft? fill = null;
            OrderEventDraft? orderEvent = null;
            AccountBalanceDraft? accountBalance = null;
            var eventText = Value(row, headers, "datetime", "transdatetime", "timestamp", "date");
            var hasEventTime = TryDate(eventText, timeZone, out var eventUtc);
            var transactionText = Value(row, headers, "transdatetime", "transactiondatetime", "transactiontime");
            var transactionUtc = TryTransactionDate(transactionText, timeZone, out var transactionValue) ? transactionValue : (DateTimeOffset?)null;
            var symbol = CleanSymbol(Value(row, headers, "symbol", "ticker", "instrument"));
            var orderActionSource = Value(row, headers, "orderactionsource");
            var sourceTimeframe = ExtractSierraTimeframe(orderActionSource);
            var rawPrice = DecimalOrNull(Value(row, headers, "fillprice", "price"));
            var scale = SierraPriceNormalizer.DetermineScale(rawPrice ?? 0m, symbol, orderActionSource);
            var resolution = configuration.Resolve(TradeFoundryConstants.SierraChart, symbol);

            if (activity.Equals("fills", StringComparison.OrdinalIgnoreCase) || activity.Contains("fill", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasEventTime)
                {
                    result.Warnings.Add($"Sierra row {rowNumber}: could not parse DateTime.");
                }
                else
                {
                    var quantityText = Value(row, headers, "quantity");
                    var priceText = Value(row, headers, "fillprice", "price");
                    if (!TryInt(quantityText, out var quantity) || quantity <= 0)
                        result.Warnings.Add($"Sierra row {rowNumber}: Quantity is missing or not positive; the raw row was retained.");
                    else if (!TryDecimal(priceText, out var price))
                        result.Warnings.Add($"Sierra row {rowNumber}: FillPrice is missing or invalid; the raw row was retained.");
                    else
                    {
                        var side = NormalizeSide(Value(row, headers, "buysell", "side", "action"));
                        if (string.IsNullOrWhiteSpace(side))
                        {
                            result.Warnings.Add($"Sierra row {rowNumber}: BuySell is missing; the raw row was retained.");
                        }
                        else
                        {
                            var high = DecimalOrNull(Value(row, headers, "highduringposition", "high"));
                            var low = DecimalOrNull(Value(row, headers, "lowduringposition", "low"));
                            fill = new FillDraft
                            {
                                SourceType = result.SourceType, SourceKey = sourceKey, ActivityType = activity, OrderActionSource = orderActionSource, EventUtc = eventUtc, TransactionUtc = transactionUtc, SourceTimeText = eventText,
                                SourceTimeframe = sourceTimeframe, Symbol = symbol, Instrument = resolution.InstrumentCode, PointValue = resolution.PointValue, TickSize = resolution.TickSize, Account = Value(row, headers, "tradeaccount", "account"),
                                Side = side, Quantity = quantity, Price = price / scale, Price2 = ScaleNullable(DecimalOrNull(Value(row, headers, "price2")), scale),
                                FilledQuantity = IntOrNull(Value(row, headers, "filledquantity")), OpenClose = Value(row, headers, "openclose"),
                                OrderType = Value(row, headers, "ordertype"), OrderStatus = Value(row, headers, "orderstatus"), ParentOrderId = Value(row, headers, "parentinternalorderid", "parentorderid"),
                                High = ScaleNullable(high, scale), Low = ScaleNullable(low, scale), Note = Value(row, headers, "note"),
                                PositionQuantity = IntOrNull(Value(row, headers, "positionquantity")), OrderId = Value(row, headers, "internalorderid", "orderid"),
                                ServiceOrderId = Value(row, headers, "serviceorderid"), ExchangeOrderId = Value(row, headers, "exchangeorderid"),
                                FillExecutionId = serviceId, ClientOrderId = Value(row, headers, "clientorderid"), TimeInForce = Value(row, headers, "timeinforce"),
                                 Username = Value(row, headers, "username"), IsAutomated = BoolOrNull(Value(row, headers, "isautomated", "automated")),
                                 AccountBalance = DecimalOrNull(Value(row, headers, "accountbalance")), Fees = ResolveFees(resolution, DecimalOrZero(Value(row, headers, "fees", "commission")), quantity), ExchangeFeePerContract = resolution.ExchangeFeePerContract, NfaFeePerContract = resolution.NfaFeePerContract, ClearingFeePerContract = resolution.ClearingFeePerContract, RowNumber = rowNumber
                            };
                        }
                    }
                }
            }
            else if (activity.Equals("orders", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasEventTime)
                {
                    result.Warnings.Add($"Sierra row {rowNumber}: could not parse order DateTime.");
                }
                else
                {
                    orderEvent = new OrderEventDraft
                    {
                         SourceType = result.SourceType, SourceKey = sourceKey, OrderActionSource = orderActionSource, EventUtc = eventUtc, TransactionUtc = transactionUtc, SourceTimeText = eventText,
                          Symbol = symbol, Instrument = resolution.InstrumentCode, Account = Value(row, headers, "tradeaccount", "account"), InternalOrderId = Value(row, headers, "internalorderid", "orderid"), ServiceOrderId = Value(row, headers, "serviceorderid"),
                        ParentOrderId = Value(row, headers, "parentinternalorderid", "parentorderid"), ExchangeOrderId = Value(row, headers, "exchangeorderid"),
                        FillExecutionId = serviceId, OrderType = Value(row, headers, "ordertype"), OrderStatus = Value(row, headers, "orderstatus"),
                        Side = NormalizeSide(Value(row, headers, "buysell", "side", "action")), OpenClose = Value(row, headers, "openclose"),
                        Price = ScaleNullable(DecimalOrNull(Value(row, headers, "price")), scale), Price2 = ScaleNullable(DecimalOrNull(Value(row, headers, "price2")), scale),
                        Quantity = IntOrNull(Value(row, headers, "quantity")), FilledQuantity = IntOrNull(Value(row, headers, "filledquantity")),
                        FillPrice = ScaleNullable(DecimalOrNull(Value(row, headers, "fillprice")), scale), PositionQuantity = IntOrNull(Value(row, headers, "positionquantity")),
                        Note = Value(row, headers, "note"), ClientOrderId = Value(row, headers, "clientorderid"), TimeInForce = Value(row, headers, "timeinforce"),
                        Username = Value(row, headers, "username"), IsAutomated = BoolOrNull(Value(row, headers, "isautomated", "automated")),
                        Fees = DecimalOrZero(Value(row, headers, "fees", "commission")), RowNumber = rowNumber
                    };
                }
            }
            else if (activity.Contains("account balance", StringComparison.OrdinalIgnoreCase) || activity.Equals("accountbalance", StringComparison.OrdinalIgnoreCase))
            {
                if (!hasEventTime)
                {
                    result.Warnings.Add($"Sierra row {rowNumber}: could not parse account-balance DateTime.");
                }
                else
                {
                    var balance = DecimalOrNull(Value(row, headers, "accountbalance", "balance"));
                    if (!balance.HasValue)
                        result.Warnings.Add($"Sierra row {rowNumber}: AccountBalance is missing or invalid.");
                    else
                        accountBalance = new AccountBalanceDraft
                        {
                            SourceType = result.SourceType, SourceKey = sourceKey, EventUtc = eventUtc, TransactionUtc = transactionUtc, SourceTimeText = eventText,
                            Account = Value(row, headers, "tradeaccount", "account"), Balance = balance, Note = Value(row, headers, "note"), RowNumber = rowNumber
                        };
                }
            }
            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = sourceKey, RowNumber = rowNumber, PayloadJson = payload, Fill = fill, OrderEvent = orderEvent, AccountBalance = accountBalance });
        }
        return result;
    }

    private static ParsedImport ParseTradingViewAccount(string[] lines, string headerLine, string[] headers, char delimiter, string timeZone, InstrumentConfiguration configuration)
    {
        var result = NewResult(TradeFoundryConstants.TradingViewAccount);
        foreach (var (row, rowNumber) in Rows(lines, headerLine, headers, delimiter))
        {
            var payload = JsonSerializer.Serialize(row);
            var sourceKey = Value(row, headers, "id", "transactionid", "executionid", "fillid", "orderid", "tradeid");
            if (string.IsNullOrWhiteSpace(sourceKey)) sourceKey = Fingerprint(result.SourceType, row);
            FillDraft? fill = null;
            var status = Value(row, headers, "status", "orderstatus", "executionstatus");
            if (!string.IsNullOrWhiteSpace(status) && !IsFilledStatus(status))
            {
                result.Warnings.Add($"TradingView row {rowNumber}: status '{status}' was retained but not treated as an execution.");
            }
            else
            {
                var dateText = Value(row, headers, "timestamp", "datetime", "filledat", "executedat", "date", "time");
                if (!TryDate(dateText, timeZone, out var eventUtc))
                    result.Warnings.Add($"TradingView row {rowNumber}: could not parse execution time.");
                else if (!TryInt(Value(row, headers, "quantity", "qty", "contracts", "filledquantity"), out var quantity) || quantity <= 0)
                    result.Warnings.Add($"TradingView row {rowNumber}: quantity is missing or not positive.");
                else if (!TryDecimal(Value(row, headers, "price", "fillprice", "filledprice", "executionprice"), out var price))
                    result.Warnings.Add($"TradingView row {rowNumber}: execution price is missing or invalid.");
                else
                {
                    var side = NormalizeSide(Value(row, headers, "side", "action", "buysell", "direction", "type"));
                    if (string.IsNullOrWhiteSpace(side)) result.Warnings.Add($"TradingView row {rowNumber}: side is missing; the raw row was retained.");
                    else
                    {
                        var symbol = CleanSymbol(Value(row, headers, "symbol", "ticker", "instrument"));
                        var resolution = configuration.Resolve(TradeFoundryConstants.TradingView, symbol);
                        fill = new FillDraft
                        {
                            SourceType = result.SourceType, SourceKey = sourceKey.Trim(), ActivityType = "Fills", EventUtc = eventUtc,
                            TransactionUtc = TryTransactionDate(Value(row, headers, "transdatetime", "transactiondatetime"), timeZone, out var transactionUtc) ? transactionUtc : (DateTimeOffset?)null,
                            SourceTimeText = dateText,
                             Symbol = symbol, Instrument = resolution.InstrumentCode, PointValue = resolution.PointValue, TickSize = resolution.TickSize, Account = Value(row, headers, "account", "broker", "tradeaccount"),
                             Side = side, Quantity = quantity, Price = price, Price2 = DecimalOrNull(Value(row, headers, "price2")), OrderActionSource = Value(row, headers, "orderactionsource"),
                            FilledQuantity = IntOrNull(Value(row, headers, "filledquantity")), OpenClose = Value(row, headers, "openclose", "positioneffect"),
                            OrderType = Value(row, headers, "ordertype", "type"), OrderStatus = status,
                            ParentOrderId = Value(row, headers, "parentorderid", "parentinternalorderid"),
                             Note = Value(row, headers, "note", "comment"), RowNumber = rowNumber,
                            OrderId = Value(row, headers, "orderid"), ServiceOrderId = Value(row, headers, "serviceorderid"),
                            ExchangeOrderId = Value(row, headers, "exchangeorderid"), FillExecutionId = Value(row, headers, "fillexecutionserviceid", "executionid"),
                            ClientOrderId = Value(row, headers, "clientorderid"), TimeInForce = Value(row, headers, "timeinforce"), Username = Value(row, headers, "username"),
                             IsAutomated = BoolOrNull(Value(row, headers, "isautomated", "automated")), AccountBalance = DecimalOrNull(Value(row, headers, "accountbalance")),
                             Fees = ResolveFees(resolution, DecimalOrZero(Value(row, headers, "fees", "commission")), quantity), ExchangeFeePerContract = resolution.ExchangeFeePerContract, NfaFeePerContract = resolution.NfaFeePerContract, ClearingFeePerContract = resolution.ClearingFeePerContract
                         };
                    }
                }
            }
            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = sourceKey.Trim(), RowNumber = rowNumber, PayloadJson = payload, Fill = fill });
        }
        return result;
    }

    private static ParsedImport ParseTradingViewStrategy(string[] lines, string headerLine, string[] headers, char delimiter, string timeZone, InstrumentConfiguration configuration)
    {
        var result = NewResult(TradeFoundryConstants.TradingViewStrategy);
        var grouped = new Dictionary<string, List<(Dictionary<string, string> Row, int Number)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Rows(lines, headerLine, headers, delimiter))
        {
            var payload = JsonSerializer.Serialize(item.Row);
            var rowKey = Fingerprint(result.SourceType, item.Row);
            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = rowKey, RowNumber = item.Number, PayloadJson = payload });
            var number = Value(item.Row, headers, "trade", "tradenumber", "traden", "tradeid", "id");
            if (string.IsNullOrWhiteSpace(number)) number = rowKey;
            if (!grouped.TryGetValue(number.Trim(), out var list)) grouped[number.Trim()] = list = new();
            list.Add(item);
        }

        foreach (var group in grouped)
        {
            var rows = group.Value;
            var entryRow = rows.FirstOrDefault(x => IsEntryRow(x.Row, headers));
            var exitRow = rows.LastOrDefault(x => IsExitRow(x.Row, headers));
            var first = rows[0].Row;
            var last = rows[^1].Row;
            var symbol = CleanSymbol(Value(first, headers, "symbol", "ticker", "instrument"));
            var resolution = configuration.Resolve(TradeFoundryConstants.TradingView, symbol);
            var direction = NormalizeDirection(Value(entryRow.Row ?? first, headers, "direction", "side", "action", "type"));
            var entryPrice = DecimalOrZero(Value(entryRow.Row ?? first, headers, "entryprice", "entry", "price", "fillprice"));
            var exitPrice = DecimalOrNull(Value(exitRow.Row ?? last, headers, "exitprice", "exit", "price", "fillprice"));
            var entryText = Value(entryRow.Row ?? first, headers, "entrydatetime", "entrytime", "entrydate", "datetime", "timestamp", "date");
            var exitText = Value(exitRow.Row ?? last, headers, "exitdatetime", "exittime", "exitdate", "datetime", "timestamp", "date");
            if (!TryDate(entryText, timeZone, out var entryUtc))
            {
                result.Warnings.Add($"Strategy trade {group.Key}: entry time could not be parsed.");
                continue;
            }
            DateTimeOffset? exitUtc = TryDate(exitText, timeZone, out var exitValue) ? exitValue : null;
            var quantity = rows.Select(x => IntOrNull(Value(x.Row, headers, "quantity", "tradequantity", "qty", "contracts", "size")) ?? 0).FirstOrDefault(x => x > 0);
            if (quantity <= 0) quantity = 1;
            var reportedPnl = DecimalOrNull(Value(last, headers, "netpnl", "netprofit", "profit", "profitlossp", "profitlossc", "pnl", "pl"));
            var grossPoints = exitPrice.HasValue ? (direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? exitPrice.Value - entryPrice : entryPrice - exitPrice.Value) * quantity : 0m;
            var pointValue = DecimalOrNull(Value(last, headers, "pointvalue", "dollarperpoint")) ?? resolution.PointValue;
            var stopPrice = DecimalOrNull(Value(entryRow.Row ?? first, headers, "initialstopprice", "stopprice", "stop"));
            var targetPrice = DecimalOrNull(Value(entryRow.Row ?? first, headers, "initialtargetprice", "targetprice", "target"));
            var initialRiskPoints = stopPrice.HasValue ? Math.Abs(entryPrice - stopPrice.Value) : (decimal?)null;
            var initialRiskCurrency = initialRiskPoints.HasValue ? initialRiskPoints.Value * quantity * pointValue : (decimal?)null;
            var grossPnl = reportedPnl ?? grossPoints * pointValue;
            var reportedFees = DecimalOrZero(Value(last, headers, "fees", "commission", "commissionc"));
            var feeQuantity = rows
                .Where(item => IsEntryRow(item.Row, headers) || IsExitRow(item.Row, headers))
                .Sum(item => IntOrNull(Value(item.Row, headers, "quantity", "tradequantity", "qty", "contracts", "size")) ?? 0);
            if (feeQuantity <= 0) feeQuantity = quantity * 2;
            var fees = ResolveTradeFees(resolution, reportedFees, feeQuantity);
            var rMultiple = initialRiskCurrency is > 0m ? grossPnl / initialRiskCurrency.Value : (decimal?)null;
            var exitType = NormalizeExitType(Value(exitRow.Row ?? last, headers, "exittype", "exitreason", "closetype"));
            result.Trades.Add(new ImportedTradeDraft
            {
                SourceKey = $"trade:{group.Key}", Symbol = symbol, Account = Value(first, headers, "account", "broker") is { Length: > 0 } account ? account : "TradingView",
                Direction = direction, EntryUtc = entryUtc, ExitUtc = exitUtc, EntryPrice = entryPrice, ExitPrice = exitPrice, Quantity = quantity,
                GrossPoints = grossPoints, GrossPnl = grossPnl, ExchangeFees = resolution.HasFeeBreakdown ? (resolution.ExchangeFeePerContract ?? 0m) * feeQuantity : 0m, NfaFees = resolution.HasFeeBreakdown ? (resolution.NfaFeePerContract ?? 0m) * feeQuantity : 0m, ClearingFees = resolution.HasFeeBreakdown ? (resolution.ClearingFeePerContract ?? 0m) * feeQuantity : 0m, Fees = fees, NetPnl = resolution.CommissionPerContract.HasValue || resolution.HasFeeBreakdown ? grossPnl - fees : reportedPnl ?? grossPnl - fees,
                Instrument = resolution.InstrumentCode, PointValue = pointValue, TickSize = resolution.TickSize,
                InitialStopPrice = stopPrice, InitialTargetPrice = targetPrice, InitialRiskPoints = initialRiskPoints,
                InitialRiskCurrency = initialRiskCurrency, RMultiple = rMultiple, ExitType = exitType,
                Note = Value(last, headers, "note", "comment")
            });
        }
        return result;
    }

    private static ParsedImport ParseBenchmark(string[] lines, string headerLine, string[] headers, char delimiter, string timeZone, string defaultSymbol)
    {
        var result = NewResult(TradeFoundryConstants.BenchmarkSeries);
        defaultSymbol = string.IsNullOrWhiteSpace(defaultSymbol) ? "SPY" : CleanSymbol(defaultSymbol);
        foreach (var (row, rowNumber) in Rows(lines, headerLine, headers, delimiter))
        {
            var payload = JsonSerializer.Serialize(row);
            var symbol = CleanSymbol(Value(row, headers, "symbol", "ticker", "instrument"));
            if (string.IsNullOrWhiteSpace(symbol)) symbol = defaultSymbol;
            var sourceKey = Fingerprint(result.SourceType, row);
            var dateText = Value(row, headers, "datetime", "timestamp", "date", "time");
            BenchmarkPointDraft? benchmark = null;
            if (!TryDate(dateText, timeZone, out var eventUtc))
            {
                result.Warnings.Add($"Benchmark row {rowNumber}: timestamp could not be parsed.");
            }
            else if (!TryDecimal(Value(row, headers, "totalreturn", "totalreturnvalue", "adjustedclose", "adjclose", "close", "value", "price"), out var value))
            {
                result.Warnings.Add($"Benchmark row {rowNumber}: close or total-return value is missing or invalid.");
            }
            else
            {
                benchmark = new BenchmarkPointDraft
                {
                    SourceType = result.SourceType, SourceKey = sourceKey, Symbol = symbol, EventUtc = eventUtc, Value = value,
                    SourceTimeText = dateText, RowNumber = rowNumber
                };
            }

            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = sourceKey, RowNumber = rowNumber, PayloadJson = payload, Benchmark = benchmark });
        }
        return result;
    }

    private static ParsedImport ParseBars(string[] lines, string headerLine, string[] headers, char delimiter, string interval, string timeZone, string barSymbol, string sourceType)
    {
        var result = NewResult(sourceType);
        var rows = Rows(lines, headerLine, headers, delimiter).ToArray();
        var requiresInterval = sourceType.Equals(TradeFoundryConstants.SierraOhlcBars, StringComparison.Ordinal);
        interval = ResolveBarInterval(interval, rows, headers, timeZone, requiresInterval);
        result.ResolvedBarInterval = interval;
        foreach (var (row, rowNumber) in rows)
        {
            var payload = JsonSerializer.Serialize(row);
            var symbol = NormalizeBarSymbol(Value(row, headers, "symbol", "ticker", "instrument"));
            if (string.IsNullOrWhiteSpace(symbol)) symbol = NormalizeBarSymbol(barSymbol);
            var dateText = BarDateTimeText(row, headers);
            if (!TryDate(dateText, timeZone, out var eventUtc))
            {
                result.Warnings.Add($"OHLC row {rowNumber}: date and time could not be parsed.");
                result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = Fingerprint(result.SourceType, row), RowNumber = rowNumber, PayloadJson = payload });
                continue;
            }
            var open = DecimalOrNull(Value(row, headers, "open"));
            var high = DecimalOrNull(Value(row, headers, "high"));
            var low = DecimalOrNull(Value(row, headers, "low"));
            var close = DecimalOrNull(Value(row, headers, "close", "last"));
            if (string.IsNullOrWhiteSpace(symbol) || !open.HasValue || !high.HasValue || !low.HasValue || !close.HasValue)
            {
                result.Warnings.Add($"OHLC row {rowNumber}: symbol and Open, High, Low, and Last/Close values are required.");
                result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = Fingerprint(result.SourceType, row), RowNumber = rowNumber, PayloadJson = payload });
                continue;
            }
            if (high.Value < Math.Max(open.Value, close.Value) || low.Value > Math.Min(open.Value, close.Value) || high.Value < low.Value)
            {
                result.Warnings.Add($"OHLC row {rowNumber}: High/Low do not contain the Open and Close values; the raw row was retained.");
                result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = Fingerprint(result.SourceType, row), RowNumber = rowNumber, PayloadJson = payload });
                continue;
            }
            var bar = new BarDraft
            {
                Symbol = symbol,
                Interval = interval,
                EventUtc = eventUtc,
                Open = open.Value,
                High = high.Value,
                Low = low.Value,
                Close = close.Value,
                Volume = LongOrNull(Value(row, headers, "volume", "vol")),
                NumberOfTrades = LongOrNull(Value(row, headers, "numberoftrades", "trades")),
                BidVolume = LongOrNull(Value(row, headers, "bidvolume", "bidvol")),
                AskVolume = LongOrNull(Value(row, headers, "askvolume", "askvol"))
            };
            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = $"{symbol}\u001f{interval}\u001f{eventUtc:O}", RowNumber = rowNumber, PayloadJson = payload, Bar = bar });
        }
        return result;
    }

    private static string ResolveBarInterval(string interval, IReadOnlyList<(Dictionary<string, string> Row, int Number)> rows, string[] headers, string timeZone, bool required)
    {
        if (!BarIntervals.IsAuto(interval)) return BarIntervals.Normalize(interval, allowSource: !required);
        if (!required) return BarIntervals.Source;

        var timestamps = rows
            .Take(32)
            .Select(item => BarDateTimeText(item.Row, headers))
            .Select(value => TryDate(value, timeZone, out var timestamp) ? timestamp : (DateTimeOffset?)null)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        var minuteDifferences = timestamps
            .Zip(timestamps.Skip(1), (first, second) => (second - first).TotalMinutes)
            .Where(minutes => minutes >= 1 && minutes <= 10080)
            .Select(minutes => (int)Math.Round(minutes, MidpointRounding.AwayFromZero))
            .Where(minutes => minutes >= 1)
            .ToArray();
        var inferredMinutes = minuteDifferences
            .GroupBy(minutes => minutes)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key)
            .Select(group => group.Key)
            .FirstOrDefault();
        if (inferredMinutes < 1)
            throw new FormatException("The bar interval could not be inferred from the first rows. Enter an interval such as 1m, 5m, or 1h and import again.");
        return BarIntervals.Format(inferredMinutes);
    }

    private static string BarDateTimeText(Dictionary<string, string> row, string[] headers)
    {
        var combined = Value(row, headers, "datetime", "timestamp", "date-time", "dateandtime");
        if (!string.IsNullOrWhiteSpace(combined)) return combined;
        var date = Value(row, headers, "date");
        var time = Value(row, headers, "time");
        return string.IsNullOrWhiteSpace(time) ? date : $"{date} {time}".Trim();
    }

    private static IEnumerable<(Dictionary<string, string> Row, int Number)> Rows(string[] lines, string headerLine, string[] headers, char delimiter)
    {
        var headerIndex = Array.IndexOf(lines, headerLine);
        for (var i = headerIndex + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var cells = ParseDelimitedLine(lines[i], delimiter);
            var row = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var c = 0; c < headers.Length; c++) row[headers[c]] = c < cells.Count ? cells[c].Trim() : string.Empty;
            yield return (row, i + 1);
        }
    }

    private static ParsedImport NewResult(string source) => new()
    {
        SourceType = source,
        SourceApplication = source switch
        {
            TradeFoundryConstants.SierraFills => TradeFoundryConstants.SierraChart,
            TradeFoundryConstants.TradingViewAccount or TradeFoundryConstants.TradingViewStrategy => TradeFoundryConstants.TradingView,
            TradeFoundryConstants.BenchmarkSeries => TradeFoundryConstants.Benchmark,
            TradeFoundryConstants.OhlcvBars => TradeFoundryConstants.Ohlcv,
            TradeFoundryConstants.SierraOhlcBars => TradeFoundryConstants.SierraChart,
            _ => string.Empty
        }
    };

    private static string ExtractSierraTimeframe(string value)
    {
        foreach (Match match in SierraTimeframePattern.Matches(value))
        {
            if (int.TryParse(match.Groups["minutes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) && minutes > 0)
                return BarIntervals.Format(minutes);
        }

        return string.Empty;
    }

    private static decimal ResolveFees(InstrumentResolution resolution, decimal reportedFees, int quantity)
    {
        return resolution.CommissionPerContract.HasValue
            ? resolution.CommissionPerContract.Value * Math.Max(1, quantity)
            : reportedFees;
    }

    private static decimal ResolveTradeFees(InstrumentResolution resolution, decimal reportedFees, int quantity)
    {
        return resolution.HasFeeBreakdown
            ? resolution.FeePerContract * Math.Max(1, quantity)
            : ResolveFees(resolution, reportedFees, quantity);
    }

    private static ImportSource DetectSource(string[] headers, string requestedType)
    {
        var request = NormalizeHeader(requestedType);
        if (request is "sierra" or "sierrachart" or "sierracfills") return ImportSource.Sierra;
        if (request is "sierrabars" or "sierraohlc" or "sierraohlcbars" or "sierrachartbars") return ImportSource.SierraBars;
        if (request is "tradingviewstrategy" or "strategy" or "strategytester") return ImportSource.TradingViewStrategy;
        if (request is "tradingviewaccount" or "account" or "broker") return ImportSource.TradingViewAccount;
        if (request is "benchmark" or "benchmarkseries" or "benchmarkdaily") return ImportSource.Benchmark;
        if (request is "ohlcv" or "bars" or "candles") return ImportSource.Bars;
        if (headers.Contains("activitytype") || headers.Contains("fillexecutionserviceid")) return ImportSource.Sierra;
        if ((headers.Contains("trade") || headers.Contains("tradenumber")) && (headers.Contains("netpnl") || headers.Contains("profit") || headers.Contains("entryprice"))) return ImportSource.TradingViewStrategy;
        if ((headers.Contains("adjclose") || headers.Contains("adjustedclose") || headers.Contains("totalreturn") || headers.Contains("totalreturnvalue")) && (headers.Contains("date") || headers.Contains("datetime") || headers.Contains("timestamp"))) return ImportSource.Benchmark;
        if (headers.Contains("open") && headers.Contains("high") && headers.Contains("low") && (headers.Contains("close") || headers.Contains("last"))) return ImportSource.Bars;
        return ImportSource.TradingViewAccount;
    }

    private static string ResolveSourceTimeZone(string text, string requestedType, string requestedTimeZone, string journalTimeZone)
    {
        var canonical = TimeZoneCatalog.CanonicalId(requestedTimeZone);
        if (!TimeZoneCatalog.IsAuto(canonical))
        {
            if (!TimeZoneCatalog.TryResolve(canonical, out _))
                throw new FormatException("The selected source timezone is not supported.");
            return canonical;
        }

        if (!TryReadHeader(text, out var lines, out var headerLine, out var headers, out var delimiter))
            return journalTimeZone;

        var source = DetectSource(headers, requestedType);
        if (source == ImportSource.Sierra)
        {
            if (LooksLikeSierraFileExport(lines, headerLine, headers, delimiter))
                return "UTC";

            // Sierra's display-time Save Log As files do not carry a marker
            // distinguishing them from an exchange export whose multiplier is
            // one. Preserve the journal-timezone default for display prices;
            // users can explicitly choose UTC when the file came from File →
            // Export but has no detectable fixed-point price.
            return journalTimeZone;
        }

        // Sierra chart-bar files are normally written in the chart timezone.
        // Other unzoned sources use the journal timezone unless the user
        // explicitly selects another source timezone.
        return journalTimeZone;
    }

    private static bool TryReadHeader(string text, out string[] lines, out string headerLine, out string[] headers, out char delimiter)
    {
        lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        headerLine = lines.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(headerLine))
        {
            headers = Array.Empty<string>();
            delimiter = ',';
            return false;
        }

        delimiter = ChooseDelimiter(headerLine);
        headers = ParseDelimitedLine(headerLine, delimiter).Select(NormalizeHeader).ToArray();
        return headers.Length > 0;
    }

    private static bool LooksLikeSierraFileExport(string[] lines, string headerLine, string[] headers, char delimiter)
    {
        foreach (var (row, _) in Rows(lines, headerLine, headers, delimiter))
        {
            var symbol = CleanSymbol(Value(row, headers, "symbol", "ticker", "instrument"));
            var orderActionSource = Value(row, headers, "orderactionsource");
            foreach (var field in new[] { "fillprice", "price", "price2" })
            {
                var rawText = Value(row, headers, field);
                if (!TryDecimal(rawText, out var rawPrice) || rawPrice == 0m)
                    continue;

                if (SierraPriceNormalizer.DetermineScale(rawPrice, symbol, orderActionSource) > 1m)
                    return true;

                // Sierra's exchange-native export commonly preserves a
                // fixed-point representation with several trailing digits.
                // This catches symbols whose configured multiplier is not
                // available to the importer while avoiding ordinary display
                // prices from Save Log As.
                var decimalPoint = rawText.IndexOf('.', StringComparison.Ordinal);
                if (decimalPoint >= 0 && rawText.Length - decimalPoint - 1 >= 6)
                    return true;
            }
        }

        return false;
    }

    private static char ChooseDelimiter(string header) => header.Count(x => x == '\t') > header.Count(x => x == ',') ? '\t' : ',';

    private static List<string> ParseDelimitedLine(string line, char delimiter)
    {
        var cells = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (ch == delimiter && !quoted) { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        cells.Add(current.ToString());
        return cells;
    }

    private static string NormalizeHeader(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    private static string Value(Dictionary<string, string> row, string[] headers, params string[] names)
    {
        foreach (var name in names)
        {
            var key = NormalizeHeader(name);
            if (row.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return string.Empty;
    }

    private static string Fingerprint(string source, Dictionary<string, string> row)
    {
        var canonical = source + "\u001f" + string.Join("\u001f", row.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => $"{x.Key}={x.Value}"));
        return "hash:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static string CleanSymbol(string value) => value.Replace("[Sim]", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
    private static string NormalizeBarSymbol(string value)
    {
        var cleaned = CleanSymbol(value);
        return string.IsNullOrWhiteSpace(cleaned) ? string.Empty : InstrumentCatalog.ExtractRoot(cleaned);
    }
    private static string NormalizeSide(string value)
    {
        if (value.Contains("buy", StringComparison.OrdinalIgnoreCase) || value.Contains("long", StringComparison.OrdinalIgnoreCase)) return "Buy";
        if (value.Contains("sell", StringComparison.OrdinalIgnoreCase) || value.Contains("short", StringComparison.OrdinalIgnoreCase)) return "Sell";
        return string.Empty;
    }
    private static string NormalizeDirection(string value) => value.Contains("short", StringComparison.OrdinalIgnoreCase) || value.Contains("sell", StringComparison.OrdinalIgnoreCase) ? "Short" : "Long";
    private static bool IsEntryRow(Dictionary<string, string> row, string[] headers) => Value(row, headers, "type", "action", "side").Contains("entry", StringComparison.OrdinalIgnoreCase);
    private static bool IsExitRow(Dictionary<string, string> row, string[] headers) => Value(row, headers, "type", "action", "side").Contains("exit", StringComparison.OrdinalIgnoreCase);
    private static bool IsFilledStatus(string status) => status.Contains("fill", StringComparison.OrdinalIgnoreCase) || status.Contains("execut", StringComparison.OrdinalIgnoreCase) || status.Equals("closed", StringComparison.OrdinalIgnoreCase);

    private static bool TryTransactionDate(string text, string timeZoneId, out DateTimeOffset value)
    {
        // Sierra's Account Balance and Positions rows often use a time-only
        // TransDateTime value. Do not silently attach today's date to those rows.
        if (string.IsNullOrWhiteSpace(text) || !System.Text.RegularExpressions.Regex.IsMatch(text, @"(?:\d{4}[-/]\d{1,2}[-/]\d{1,2}|\d{1,2}[-/]\d{1,2}[-/]\d{2,4})"))
        {
            value = default;
            return false;
        }
        return TryDate(text, timeZoneId, out value);
    }

    private static bool TryDate(string text, string timeZoneId, out DateTimeOffset value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var hasExplicitOffset = text.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || System.Text.RegularExpressions.Regex.IsMatch(text, @"[+-]\d{2}:?\d{2}\s*$");
        if (hasExplicitOffset && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out value))
        {
            value = value.ToUniversalTime();
            return true;
        }
        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var local) && !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out local))
            return false;
        value = TimeZoneCatalog.FromLocal(local, timeZoneId);
        return true;
    }
    private static bool TryDecimal(string text, out decimal value)
    {
        text = text.Replace("$", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) || decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }
    private static decimal DecimalOrZero(string text) => TryDecimal(text, out var value) ? value : 0m;
    private static decimal? DecimalOrNull(string text) => TryDecimal(text, out var value) ? value : null;
    private static decimal? ScaleNullable(decimal? value, decimal scale) => value.HasValue ? value.Value / scale : null;
    private static bool TryInt(string text, out int value) => int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) || (TryDecimal(text, out var decimalValue) && (value = (int)decimalValue) > 0);
    private static int? IntOrNull(string text) => TryInt(text, out var value) ? value : null;
    private static long? LongOrNull(string text) => long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static bool? BoolOrNull(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (bool.TryParse(text, out var value)) return value;
        if (text is "1" or "Y" or "Yes") return true;
        if (text is "0" or "N" or "No") return false;
        return null;
    }

    private static string NormalizeExitType(string value)
    {
        if (value.Contains("stop", StringComparison.OrdinalIgnoreCase)) return "stop";
        if (value.Contains("target", StringComparison.OrdinalIgnoreCase) || value.Contains("limit", StringComparison.OrdinalIgnoreCase)) return "target";
        if (value.Contains("manual", StringComparison.OrdinalIgnoreCase)) return "manual";
        return string.Empty;
    }

    private enum ImportSource { Sierra, SierraBars, TradingViewAccount, TradingViewStrategy, Benchmark, Bars }
}
