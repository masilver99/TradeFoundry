using System.Globalization;
using System.Text.RegularExpressions;
using TradeFoundry.Core;

namespace TradeFoundry.Services.Statements;

/// <summary>
/// Reads AMP Global Clearing daily and monthly statements. Each trading day contributes its
/// confirmation fills, the buy/sell totals, the fee lines printed under them (exchange, NFA,
/// clearing and any commission), and the realized P&amp;L from the purchase &amp; sale section.
/// </summary>
public static partial class AmpStatementParser
{
    private enum Section { Header, Confirmations, PurchaseSale, Summary }

    [GeneratedRegex(@"^\d{2}-[A-Z]{3}-\d{2}$")]
    private static partial Regex DateWordPattern();

    [GeneratedRegex(@"FROM(\d{2}-[A-Z]{3}-\d{2})TO(\d{2}-[A-Z]{3}-\d{2})")]
    private static partial Regex PeriodPattern();

    [GeneratedRegex(@"SUMMARYFROM(\d{2}/\d{2}/\d{2})TO(\d{2}/\d{2}/\d{2})")]
    private static partial Regex SummaryPeriodPattern();

    [GeneratedRegex(@"ASOF(\d{2}-[A-Z]{3}-\d{2})")]
    private static partial Regex AsOfPattern();

    [GeneratedRegex(@"^[A-Z]{3}$")]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex(@"^\d{4,12}$")]
    private static partial Regex AccountPattern();

    /// <summary>Statement pages are US Letter or A4; anything right of this is the header's right column.</summary>
    private const double RightColumnStart = 290d;

    /// <summary>Reads a statement; anything unreadable is reported as a <see cref="StatementParseException"/>.</summary>
    public static ParsedBrokerStatement Parse(IReadOnlyList<StatementLine> lines)
    {
        try
        {
            return ParseLines(lines);
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException or InvalidOperationException and not StatementParseException)
        {
            throw new StatementParseException("This statement could not be read: a date or amount on it is not in the expected format.");
        }
    }

    private static ParsedBrokerStatement ParseLines(IReadOnlyList<StatementLine> lines)
    {
        BrokerStatementKind? kind = null;
        DateOnly? headerDate = null, periodStart = null, periodEnd = null;
        var account = string.Empty;
        var section = Section.Header;
        DateOnly? asOfDate = null, rowDate = null;
        double? buyColumn = null, sellColumn = null;
        var days = new SortedDictionary<DateOnly, StatementDay>();
        var totals = new Dictionary<DateOnly, (int Buy, int Sell)>();
        var warnings = new List<string>();
        decimal? openingCash = null, realized = null, totalFees = null, newCash = null, netLiq = null;

        StatementDay Day(DateOnly date)
        {
            if (!days.TryGetValue(date, out var day)) days[date] = day = new StatementDay { TradeDate = date };
            return day;
        }

        DateOnly? CurrentDay() => asOfDate ?? rowDate ?? headerDate;

        foreach (var line in lines)
        {
            var words = line.Words;
            if (words.Count == 0) continue;
            var normalized = Normalize(line.Text);

            if (normalized.Contains("DAILYSTATEMENT", StringComparison.Ordinal)) kind ??= BrokerStatementKind.Daily;
            if (normalized.Contains("MONTHLYSTATEMENT", StringComparison.Ordinal)) kind ??= BrokerStatementKind.Monthly;
            if (PeriodPattern().Match(normalized) is { Success: true } period && periodStart is null)
            {
                periodStart = ParseDateWord(period.Groups[1].Value);
                periodEnd = ParseDateWord(period.Groups[2].Value);
            }
            if (SummaryPeriodPattern().Match(normalized) is { Success: true } summaryPeriod && periodStart is null)
            {
                periodStart = ParseSlashDate(summaryPeriod.Groups[1].Value);
                periodEnd = ParseSlashDate(summaryPeriod.Groups[2].Value);
            }

            if (normalized.Contains("TRADESCONFIRMATIONS", StringComparison.Ordinal)) { section = Section.Confirmations; asOfDate = null; rowDate = null; continue; }
            if (normalized.Contains("PURCHASE&SALE", StringComparison.Ordinal) && !normalized.Contains("HAVEBEENMADE", StringComparison.Ordinal)) { section = Section.PurchaseSale; asOfDate = null; rowDate = null; continue; }
            if (normalized.StartsWith("ACCOUNTSUMMARY", StringComparison.Ordinal)) { section = Section.Summary; continue; }

            if (AsOfPattern().Match(normalized) is { Success: true } asOf && section is Section.Confirmations or Section.PurchaseSale)
            {
                asOfDate = ParseDateWord(asOf.Groups[1].Value);
                if (asOfDate is null) warnings.Add($"'{asOf.Groups[1].Value}' is not a valid trade date.");
                rowDate = null;
                continue;
            }
            if (normalized.Contains("EXECUTEDTHISDAY", StringComparison.Ordinal)) { asOfDate = null; continue; }

            var buyHeader = words.FirstOrDefault(w => w.Text.Equals("BUY", StringComparison.OrdinalIgnoreCase));
            var sellHeader = words.FirstOrDefault(w => w.Text.Equals("SELL", StringComparison.OrdinalIgnoreCase));
            if (buyHeader is not null && sellHeader is not null)
            {
                buyColumn = buyHeader.Center;
                sellColumn = sellHeader.Center;
                continue;
            }

            switch (section)
            {
                case Section.Header:
                    foreach (var word in words.Where(w => w.Left >= RightColumnStart))
                    {
                        if (headerDate is null && TryDateWord(word.Text, out var date)) headerDate = date;
                        else if (account.Length == 0 && AccountPattern().IsMatch(word.Text)) account = word.Text;
                    }
                    break;

                case Section.Confirmations:
                    if (TryDateWord(words[0].Text, out var fillDate))
                    {
                        if (TryParseFill(words, fillDate, buyColumn, sellColumn) is { } fill)
                        {
                            rowDate = fillDate;
                            Day(asOfDate ?? fillDate).Fills.Add(fill);
                        }
                        break;
                    }
                    if (words[0].Text.Equals("TOTAL", StringComparison.OrdinalIgnoreCase))
                    {
                        if (CurrentDay() is not { } totalDate) { warnings.Add("A confirmation total had no trade date and was skipped."); break; }
                        var (buy, sell) = ParseTotalQuantities(words, buyColumn, sellColumn);
                        var day = Day(totalDate);
                        day.BuyContracts += buy;
                        day.SellContracts += sell;
                        var running = totals.GetValueOrDefault(totalDate);
                        totals[totalDate] = (running.Buy + buy, running.Sell + sell);
                        break;
                    }
                    if (TryParseFee(words) is { } fee)
                    {
                        if (CurrentDay() is not { } feeDate) { warnings.Add($"The fee line '{fee.Name}' had no trade date and was skipped."); break; }
                        Day(feeDate).Fees.Add(fee);
                    }
                    break;

                case Section.PurchaseSale:
                    if (TryDateWord(words[0].Text, out var matchDate)) { rowDate = matchDate; break; }
                    var psIndex = IndexOf(words, "P&S");
                    if (psIndex >= 0 && TryMoneyAfter(words, psIndex + 1, out var pnl, out _))
                    {
                        if (CurrentDay() is not { } pnlDate) { warnings.Add("A purchase & sale total had no trade date and was skipped."); break; }
                        var day = Day(pnlDate);
                        day.RealizedPnl += pnl;
                        day.HasRealizedPnl = true;
                    }
                    break;

                case Section.Summary:
                    if (TrySummaryValue(words, normalized, "ACCOUNTCASHBALANCE", out var value)) openingCash ??= value;
                    else if (TrySummaryValue(words, normalized, "REALIZEDPROFIT/LOSS", out value)) realized ??= value;
                    else if (TrySummaryValue(words, normalized, "TOTALCOMMISSION&FEES", out value)) totalFees ??= -value;
                    else if (TrySummaryValue(words, normalized, "NEWCASHBALANCE", out value)) newCash ??= value;
                    else if (TrySummaryValue(words, normalized, "NETLIQUIDATINGVALUE", out value)) netLiq ??= value;
                    break;
            }
        }

        if (kind is null) throw new StatementParseException("This file is not an AMP daily or monthly statement.");

        if (kind == BrokerStatementKind.Daily)
        {
            var date = headerDate ?? (days.Count == 1 ? days.Keys.First() : null);
            if (date is null) throw new StatementParseException("The statement date could not be found on this daily statement.");
            periodStart = periodEnd = date;
        }
        else if (periodStart is null || periodEnd is null)
        {
            if (days.Count == 0) throw new StatementParseException("The statement period could not be found on this monthly statement.");
            periodStart = new DateOnly(days.Keys.First().Year, days.Keys.First().Month, 1);
            periodEnd = periodStart.Value.AddMonths(1).AddDays(-1);
        }

        foreach (var day in days.Values)
        {
            if (day.TradeDate < periodStart || day.TradeDate > periodEnd)
                warnings.Add($"{day.TradeDate:yyyy-MM-dd} is outside the statement period.");
            if (day.Fills.Count == 0) continue;
            var fillBuys = day.Fills.Where(f => f.Side == "BUY").Sum(f => f.Quantity);
            var fillSells = day.Fills.Where(f => f.Side == "SELL").Sum(f => f.Quantity);
            if (day.Fills.Any(f => f.Side.Length == 0))
                warnings.Add($"{day.TradeDate:yyyy-MM-dd}: some fills could not be placed in the buy or sell column.");
            else if (totals.ContainsKey(day.TradeDate) && (fillBuys != day.BuyContracts || fillSells != day.SellContracts))
                warnings.Add($"{day.TradeDate:yyyy-MM-dd}: the fills add up to {fillBuys} bought / {fillSells} sold, but the total line says {day.BuyContracts} / {day.SellContracts}.");
            if (!totals.ContainsKey(day.TradeDate))
            {
                day.BuyContracts = fillBuys;
                day.SellContracts = fillSells;
            }
        }

        // Cross-check the days against the statement's own totals, so a missed fee or P&L line is caught.
        var dayFees = days.Values.Sum(d => d.TotalFees);
        var dayPnl = days.Values.Sum(d => d.RealizedPnl);
        if (totalFees is { } summaryFees && Math.Abs(summaryFees - dayFees) > 0.005m)
            warnings.Add($"The account summary shows {summaryFees:0.00} in commission and fees, but the day fee lines add up to {dayFees:0.00}.");
        if (realized is { } summaryPnl && Math.Abs(summaryPnl - dayPnl) > 0.005m)
            warnings.Add($"The account summary shows {summaryPnl:0.00} realized P&L, but the purchase & sale totals add up to {dayPnl:0.00}.");
        foreach (var currency in days.Values.SelectMany(d => d.Fees.Select(f => f.Currency)).Where(c => c != "USD").Distinct())
            warnings.Add($"Fees in {currency} are not supported; only USD fees are reconciled.");

        return new ParsedBrokerStatement
        {
            Broker = "AMP",
            Kind = kind.Value,
            Account = account,
            PeriodStart = periodStart!.Value,
            PeriodEnd = periodEnd!.Value,
            Days = days.Values.ToArray(),
            Summary = new StatementAccountSummary(openingCash, realized, totalFees, newCash, netLiq),
            Warnings = warnings
        };
    }

    private static StatementFill? TryParseFill(IReadOnlyList<StatementWord> words, DateOnly date, double? buyColumn, double? sellColumn)
    {
        // DATE NUMBER MARKET QTY CONTRACT... PRICE CCY [DEBIT/CREDIT]
        if (words.Count < 6) return null;
        var number = words[1].Text;
        if (!number.All(char.IsDigit)) return null;
        var market = words[2].Text;
        var priceIndex = -1;
        for (var i = words.Count - 1; i > 3; i--)
        {
            if (CurrencyPattern().IsMatch(words[i].Text) && TryAmount(words[i - 1].Text, out _)) { priceIndex = i - 1; break; }
        }
        if (priceIndex < 0) return null;
        var quantityWord = words[3];
        if (!int.TryParse(quantityWord.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var quantity) || quantity <= 0) return null;
        TryAmount(words[priceIndex].Text, out var price);
        var contract = string.Join(' ', words.Skip(4).Take(priceIndex - 4).Select(w => w.Text));
        return new StatementFill(date, number, market, SideOf(quantityWord, buyColumn, sellColumn), quantity, contract, price, words[priceIndex + 1].Text);
    }

    private static (int Buy, int Sell) ParseTotalQuantities(IReadOnlyList<StatementWord> words, double? buyColumn, double? sellColumn)
    {
        var quantities = words.Skip(1).TakeWhile(w => int.TryParse(w.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _)).ToArray();
        int buy = 0, sell = 0;
        if (buyColumn is null || sellColumn is null)
        {
            if (quantities.Length > 0) buy = int.Parse(quantities[0].Text, CultureInfo.InvariantCulture);
            if (quantities.Length > 1) sell = int.Parse(quantities[1].Text, CultureInfo.InvariantCulture);
            return (buy, sell);
        }
        foreach (var word in quantities)
        {
            var value = int.Parse(word.Text, CultureInfo.InvariantCulture);
            if (SideOf(word, buyColumn, sellColumn) == "BUY") buy += value; else sell += value;
        }
        return (buy, sell);
    }

    private static string SideOf(StatementWord word, double? buyColumn, double? sellColumn)
    {
        if (buyColumn is null || sellColumn is null) return string.Empty;
        return Math.Abs(word.Center - buyColumn.Value) <= Math.Abs(word.Center - sellColumn.Value) ? "BUY" : "SELL";
    }

    /// <summary>A fee line: a name, then currency, amount and DR/CR, e.g. "Clearing Teton USD 1.82 DR".</summary>
    private static StatementFee? TryParseFee(IReadOnlyList<StatementWord> words)
    {
        for (var i = words.Count - 3; i >= 1; i--)
        {
            if (!CurrencyPattern().IsMatch(words[i].Text)) continue;
            if (!TryMoneyAfter(words, i, out var signed, out var currency)) continue;
            var nameWords = new List<string>();
            for (var j = i - 1; j >= 0 && words[j].Text.Any(char.IsLetter) && !words[j].Text.Any(char.IsDigit); j--)
                nameWords.Insert(0, words[j].Text);
            if (nameWords.Count == 0) return null;
            var name = string.Join(' ', nameWords);
            if (name.Equals("P&S", StringComparison.OrdinalIgnoreCase)) return null;
            // A debit is a charge; store fees as positive costs and credits (rebates) as negative.
            return new StatementFee(name, StatementFeeCategory.Classify(name), -signed, currency);
        }
        return null;
    }

    /// <summary>Reads "CCY amount DR|CR" starting at <paramref name="index"/>; credits are positive.</summary>
    private static bool TryMoneyAfter(IReadOnlyList<StatementWord> words, int index, out decimal signed, out string currency)
    {
        signed = 0m;
        currency = string.Empty;
        if (index < 0 || index + 2 >= words.Count) return false;
        if (!CurrencyPattern().IsMatch(words[index].Text)) return false;
        if (!TryAmount(words[index + 1].Text, out var amount)) return false;
        var direction = words[index + 2].Text.ToUpperInvariant();
        if (direction is not ("DR" or "CR")) return false;
        currency = words[index].Text;
        signed = direction == "DR" ? -amount : amount;
        return true;
    }

    private static bool TrySummaryValue(IReadOnlyList<StatementWord> words, string normalized, string label, out decimal value)
    {
        value = 0m;
        if (!normalized.StartsWith(label, StringComparison.Ordinal)) return false;
        for (var i = 0; i < words.Count - 1; i++)
        {
            if (!TryAmount(words[i].Text, out var amount)) continue;
            var direction = words[i + 1].Text.ToUpperInvariant();
            if (direction is not ("DR" or "CR")) continue;
            value = direction == "DR" ? -amount : amount;
            return true;
        }
        return false;
    }

    private static int IndexOf(IReadOnlyList<StatementWord> words, string text)
    {
        for (var i = 0; i < words.Count; i++)
            if (words[i].Text.Equals(text, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static string Normalize(string text) => new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray()).ToUpperInvariant();

    private static bool TryAmount(string text, out decimal amount) =>
        decimal.TryParse(text.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount)
        && text.Any(char.IsDigit);

    private static bool TryDateWord(string text, out DateOnly date)
    {
        date = default;
        if (!DateWordPattern().IsMatch(text) || ParseDateWord(text) is not { } parsed) return false;
        date = parsed;
        return true;
    }

    /// <summary>14-SEP-26, or null when it is not a real date.</summary>
    private static DateOnly? ParseDateWord(string text)
    {
        // Month names are matched case-insensitively by title-casing them first.
        var titled = text[..4] + text.Substring(4, 2).ToLowerInvariant() + text[6..];
        return DateOnly.TryParseExact(titled, "dd-MMM-yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    private static DateOnly? ParseSlashDate(string text) =>
        DateOnly.TryParseExact(text, "MM/dd/yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
}
