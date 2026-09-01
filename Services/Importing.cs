using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class ImportService
{
    private readonly TradeFoundryDb _database;

    public ImportService(TradeFoundryDb database) => _database = database;

    public async Task<ImportResult> ImportAsync(Guid journalId, string fileName, Stream content, string requestedType, string groupingPolicy, string interval, CancellationToken cancellationToken = default)
    {
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, cancellationToken);
        var text = Encoding.UTF8.GetString(memory.ToArray()).TrimStart('\uFEFF');
        var journalTimeZone = _database.GetJournal(journalId)?.TimeZone ?? "UTC";
        var parsed = Parse(text, requestedType, interval, journalTimeZone);
        return _database.CommitImport(journalId, fileName, parsed, groupingPolicy, interval);
    }

    public ParsedImport Parse(string text, string requestedType = "auto", string interval = "source", string timeZone = "UTC")
    {
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
            ImportSource.Sierra => ParseSierra(lines, headerLine, headers, delimiter, timeZone),
            ImportSource.TradingViewAccount => ParseTradingViewAccount(lines, headerLine, headers, delimiter, timeZone),
            ImportSource.TradingViewStrategy => ParseTradingViewStrategy(lines, headerLine, headers, delimiter, timeZone),
            _ => ParseBars(lines, headerLine, headers, delimiter, interval, timeZone)
        };
        return result;
    }

    private static ParsedImport ParseSierra(string[] lines, string headerLine, string[] headers, char delimiter, string timeZone)
    {
        var result = NewResult(TradeFoundryConstants.SierraFills);
        foreach (var (row, rowNumber) in Rows(lines, headerLine, headers, delimiter))
        {
            var payload = JsonSerializer.Serialize(row);
            var activity = Value(row, headers, "activitytype", "activity");
            var serviceId = Value(row, headers, "fillexecutionserviceid");
            var sourceKey = string.IsNullOrWhiteSpace(serviceId) ? Fingerprint(result.SourceType, row) : serviceId.Trim();
            FillDraft? fill = null;
            if (activity.Equals("fills", StringComparison.OrdinalIgnoreCase) || activity.Contains("fill", StringComparison.OrdinalIgnoreCase))
            {
                var eventText = Value(row, headers, "datetime", "transdatetime", "timestamp", "date");
                if (!TryDate(eventText, timeZone, out var eventUtc))
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
                            var symbol = CleanSymbol(Value(row, headers, "symbol", "ticker", "instrument"));
                            var orderActionSource = Value(row, headers, "orderactionsource");
                            var scale = SierraPriceNormalizer.DetermineScale(price, symbol, orderActionSource);
                            var high = DecimalOrNull(Value(row, headers, "highduringposition", "high"));
                            var low = DecimalOrNull(Value(row, headers, "lowduringposition", "low"));
                            fill = new FillDraft
                            {
                                SourceType = result.SourceType, SourceKey = sourceKey, EventUtc = eventUtc, SourceTimeText = eventText,
                                Symbol = symbol, Account = Value(row, headers, "tradeaccount", "account"),
                                Side = side, Quantity = quantity, Price = price / scale,
                                OpenClose = Value(row, headers, "openclose"), High = high / scale,
                                Low = low / scale, Note = Value(row, headers, "note"),
                                PositionQuantity = IntOrNull(Value(row, headers, "positionquantity")), OrderId = Value(row, headers, "internalorderid", "orderid"),
                                ServiceOrderId = Value(row, headers, "serviceorderid"), Fees = DecimalOrZero(Value(row, headers, "fees", "commission")), RowNumber = rowNumber
                            };
                        }
                    }
                }
            }
            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = sourceKey, RowNumber = rowNumber, PayloadJson = payload, Fill = fill });
        }
        return result;
    }

    private static ParsedImport ParseTradingViewAccount(string[] lines, string headerLine, string[] headers, char delimiter, string timeZone)
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
                        fill = new FillDraft
                        {
                            SourceType = result.SourceType, SourceKey = sourceKey.Trim(), EventUtc = eventUtc, SourceTimeText = dateText,
                            Symbol = CleanSymbol(Value(row, headers, "symbol", "ticker", "instrument")), Account = Value(row, headers, "account", "broker", "tradeaccount"),
                            Side = side, Quantity = quantity, Price = price, OpenClose = Value(row, headers, "openclose", "positioneffect"),
                            Note = Value(row, headers, "note", "comment"), Fees = DecimalOrZero(Value(row, headers, "fees", "commission")), RowNumber = rowNumber,
                            OrderId = Value(row, headers, "orderid"), ServiceOrderId = Value(row, headers, "executionid")
                        };
                    }
                }
            }
            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = sourceKey.Trim(), RowNumber = rowNumber, PayloadJson = payload, Fill = fill });
        }
        return result;
    }

    private static ParsedImport ParseTradingViewStrategy(string[] lines, string headerLine, string[] headers, char delimiter, string timeZone)
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
            var grossPnl = reportedPnl ?? grossPoints * InstrumentCatalog.Resolve(symbol).PointValue;
            var fees = DecimalOrZero(Value(last, headers, "fees", "commission", "commissionc"));
            result.Trades.Add(new ImportedTradeDraft
            {
                SourceKey = $"trade:{group.Key}", Symbol = symbol, Account = Value(first, headers, "account", "broker") is { Length: > 0 } account ? account : "TradingView",
                Direction = direction, EntryUtc = entryUtc, ExitUtc = exitUtc, EntryPrice = entryPrice, ExitPrice = exitPrice, Quantity = quantity,
                GrossPoints = grossPoints, GrossPnl = grossPnl, Fees = fees, NetPnl = reportedPnl ?? grossPnl - fees, Note = Value(last, headers, "note", "comment")
            });
        }
        return result;
    }

    private static ParsedImport ParseBars(string[] lines, string headerLine, string[] headers, char delimiter, string interval, string timeZone)
    {
        var result = NewResult(TradeFoundryConstants.OhlcvBars);
        interval = string.IsNullOrWhiteSpace(interval) ? "source" : interval.Trim();
        foreach (var (row, rowNumber) in Rows(lines, headerLine, headers, delimiter))
        {
            var payload = JsonSerializer.Serialize(row);
            var symbol = CleanSymbol(Value(row, headers, "symbol", "ticker", "instrument"));
            var dateText = Value(row, headers, "datetime", "timestamp", "date-time", "date", "time");
            if (!TryDate(dateText, timeZone, out var eventUtc))
            {
                result.Warnings.Add($"OHLCV row {rowNumber}: timestamp could not be parsed.");
                result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = Fingerprint(result.SourceType, row), RowNumber = rowNumber, PayloadJson = payload });
                continue;
            }
            var open = DecimalOrNull(Value(row, headers, "open"));
            var high = DecimalOrNull(Value(row, headers, "high"));
            var low = DecimalOrNull(Value(row, headers, "low"));
            var close = DecimalOrNull(Value(row, headers, "close", "last"));
            if (string.IsNullOrWhiteSpace(symbol) || !open.HasValue || !high.HasValue || !low.HasValue || !close.HasValue)
            {
                result.Warnings.Add($"OHLCV row {rowNumber}: symbol and OHLC values are required.");
                result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = Fingerprint(result.SourceType, row), RowNumber = rowNumber, PayloadJson = payload });
                continue;
            }
            var bar = new BarDraft { Symbol = symbol, Interval = interval, EventUtc = eventUtc, Open = open.Value, High = high.Value, Low = low.Value, Close = close.Value, Volume = LongOrNull(Value(row, headers, "volume", "vol")) };
            result.Records.Add(new ParsedRecord { SourceType = result.SourceType, SourceKey = $"{symbol}\u001f{interval}\u001f{eventUtc:O}", RowNumber = rowNumber, PayloadJson = payload, Bar = bar });
        }
        return result;
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

    private static ParsedImport NewResult(string source) => new() { SourceType = source };

    private static ImportSource DetectSource(string[] headers, string requestedType)
    {
        var request = NormalizeHeader(requestedType);
        if (request is "sierra" or "sierrachart" or "sierracfills") return ImportSource.Sierra;
        if (request is "tradingviewstrategy" or "strategy" or "strategytester") return ImportSource.TradingViewStrategy;
        if (request is "tradingviewaccount" or "account" or "broker") return ImportSource.TradingViewAccount;
        if (request is "ohlcv" or "bars" or "candles") return ImportSource.Bars;
        if (headers.Contains("activitytype") || headers.Contains("fillexecutionserviceid")) return ImportSource.Sierra;
        if ((headers.Contains("trade") || headers.Contains("tradenumber")) && (headers.Contains("netpnl") || headers.Contains("profit") || headers.Contains("entryprice"))) return ImportSource.TradingViewStrategy;
        if (headers.Contains("open") && headers.Contains("high") && headers.Contains("low") && headers.Contains("close")) return ImportSource.Bars;
        return ImportSource.TradingViewAccount;
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
        var zone = ResolveTimeZone(timeZoneId);
        var unspecified = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        value = new DateTimeOffset(unspecified, zone.GetUtcOffset(unspecified)).ToUniversalTime();
        return true;
    }

    private static TimeZoneInfo ResolveTimeZone(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return TimeZoneInfo.Utc;
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Eastern Standard Time"] = "America/New_York", ["Central Standard Time"] = "America/Chicago", ["Mountain Standard Time"] = "America/Denver", ["Pacific Standard Time"] = "America/Los_Angeles"
        };
        if (aliases.TryGetValue(id.Trim(), out var mapped)) id = mapped;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (TimeZoneNotFoundException) { return TimeZoneInfo.Utc; }
        catch (InvalidTimeZoneException) { return TimeZoneInfo.Utc; }
    }
    private static bool TryDecimal(string text, out decimal value)
    {
        text = text.Replace("$", string.Empty, StringComparison.Ordinal).Replace(",", string.Empty, StringComparison.Ordinal).Trim();
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) || decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }
    private static decimal DecimalOrZero(string text) => TryDecimal(text, out var value) ? value : 0m;
    private static decimal? DecimalOrNull(string text) => TryDecimal(text, out var value) ? value : null;
    private static bool TryInt(string text, out int value) => int.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value) || (TryDecimal(text, out var decimalValue) && (value = (int)decimalValue) > 0);
    private static int? IntOrNull(string text) => TryInt(text, out var value) ? value : null;
    private static long? LongOrNull(string text) => long.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var value) ? value : null;

    private enum ImportSource { Sierra, TradingViewAccount, TradingViewStrategy, Bars }
}
