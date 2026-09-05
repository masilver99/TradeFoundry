using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Services;

public sealed class BenchmarkDataException : Exception
{
    public BenchmarkDataException(string message) : base(message)
    {
    }
}

public sealed class BenchmarkDownload
{
    public string Symbol { get; init; } = string.Empty;
    public Uri SourceUri { get; init; } = new("https://query2.finance.yahoo.com/");
    public DateTimeOffset FetchedUtc { get; init; }
    public IReadOnlyList<BenchmarkDownloadPoint> Points { get; init; } = Array.Empty<BenchmarkDownloadPoint>();
}

public interface IBenchmarkDataProvider
{
    Task<BenchmarkDownload> DownloadAsync(string symbol, DateOnly startInclusive, DateOnly endInclusive, CancellationToken cancellationToken = default);
}

public sealed class YahooFinanceBenchmarkProvider : IBenchmarkDataProvider
{
    private readonly HttpClient _httpClient;
    private readonly BenchmarkOptions _options;

    public YahooFinanceBenchmarkProvider(HttpClient httpClient, IOptions<BenchmarkOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<BenchmarkDownload> DownloadAsync(string symbol, DateOnly startInclusive, DateOnly endInclusive, CancellationToken cancellationToken = default)
    {
        symbol = NormalizeSymbol(symbol);
        if (startInclusive > endInclusive) throw new ArgumentException("The benchmark start date must be on or before the end date.");

        var sourceUri = BuildSourceUri(symbol, startInclusive, endInclusive);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.RequestTimeoutSeconds, 1, 60)));

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.GetAsync(sourceUri, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new BenchmarkDataException("Yahoo Finance did not respond before the benchmark download timeout.");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
                throw new BenchmarkDataException($"Yahoo Finance returned {status}.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
            var points = ParsePoints(document.RootElement, symbol, startInclusive, endInclusive);
            return new BenchmarkDownload
            {
                Symbol = symbol,
                SourceUri = sourceUri,
                FetchedUtc = DateTimeOffset.UtcNow,
                Points = points
            };
        }
    }

    private Uri BuildSourceUri(string symbol, DateOnly startInclusive, DateOnly endInclusive)
    {
        var baseUrl = (_options.YahooChartBaseUrl ?? string.Empty).Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps)
            throw new BenchmarkDataException("The Yahoo Finance benchmark URL must be an absolute HTTPS URL.");

        var period1 = new DateTimeOffset(DateTime.SpecifyKind(startInclusive.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)).ToUnixTimeSeconds();
        var period2 = new DateTimeOffset(DateTime.SpecifyKind(endInclusive.AddDays(1).ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)).ToUnixTimeSeconds();
        var root = baseUrl.TrimEnd('/');
        var encodedSymbol = Uri.EscapeDataString(symbol);
        return new Uri($"{root}/v8/finance/chart/{encodedSymbol}?period1={period1.ToString(CultureInfo.InvariantCulture)}&period2={period2.ToString(CultureInfo.InvariantCulture)}&interval=1d&events=history&includeAdjustedClose=true", UriKind.Absolute);
    }

    private IReadOnlyList<BenchmarkDownloadPoint> ParsePoints(JsonElement root, string symbol, DateOnly startInclusive, DateOnly endInclusive)
    {
        if (!root.TryGetProperty("chart", out var chart) || chart.ValueKind != JsonValueKind.Object)
            throw new BenchmarkDataException("Yahoo Finance returned an invalid chart response.");

        if (chart.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var description = error.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(description)) throw new BenchmarkDataException($"Yahoo Finance rejected {symbol}: {description}");
        }

        if (!chart.TryGetProperty("result", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0 || results[0].ValueKind != JsonValueKind.Object)
            throw new BenchmarkDataException($"Yahoo Finance returned no daily data for {symbol}.");

        var result = results[0];
        if (!result.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array)
            throw new BenchmarkDataException($"Yahoo Finance returned no timestamps for {symbol}.");

        if (!result.TryGetProperty("indicators", out var indicators) || indicators.ValueKind != JsonValueKind.Object ||
            !indicators.TryGetProperty("quote", out var quoteSeries) || quoteSeries.ValueKind != JsonValueKind.Array || quoteSeries.GetArrayLength() == 0 ||
            quoteSeries[0].ValueKind != JsonValueKind.Object || !quoteSeries[0].TryGetProperty("close", out var closeValues) || closeValues.ValueKind != JsonValueKind.Array)
            throw new BenchmarkDataException($"Yahoo Finance returned no closing prices for {symbol}.");

        JsonElement? adjustedValues = null;
        if (_options.UseAdjustedClose && indicators.TryGetProperty("adjclose", out var adjustedSeries) && adjustedSeries.ValueKind == JsonValueKind.Array && adjustedSeries.GetArrayLength() > 0 &&
            adjustedSeries[0].ValueKind == JsonValueKind.Object && adjustedSeries[0].TryGetProperty("adjclose", out var adjusted) && adjusted.ValueKind == JsonValueKind.Array)
            adjustedValues = adjusted;

        var pointsByDate = new Dictionary<DateOnly, BenchmarkDownloadPoint>();
        for (var index = 0; index < timestamps.GetArrayLength(); index++)
        {
            if (!timestamps[index].TryGetInt64(out var unixSeconds)) continue;

            DateTimeOffset eventUtc;
            try
            {
                eventUtc = DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToUniversalTime();
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            var date = DateOnly.FromDateTime(eventUtc.UtcDateTime.Date);
            if (date < startInclusive || date > endInclusive) continue;

            var value = adjustedValues.HasValue ? ReadNumber(adjustedValues.Value, index) ?? ReadNumber(closeValues, index) : ReadNumber(closeValues, index);
            if (!value.HasValue || value <= 0m) continue;

            var point = new BenchmarkDownloadPoint
            {
                EventUtc = eventUtc,
                Value = value.Value,
                SourceTimeText = eventUtc.ToString("O", CultureInfo.InvariantCulture)
            };
            if (!pointsByDate.TryGetValue(date, out var existing) || point.EventUtc > existing.EventUtc)
                pointsByDate[date] = point;
        }

        var points = pointsByDate.OrderBy(x => x.Key).Select(x => x.Value).ToArray();
        if (points.Length == 0) throw new BenchmarkDataException($"Yahoo Finance returned no usable daily closes for {symbol} in the requested range.");
        return points;
    }

    private static decimal? ReadNumber(JsonElement values, int index)
    {
        if (values.ValueKind != JsonValueKind.Array || index >= values.GetArrayLength()) return null;
        var value = values[index];
        if (value.ValueKind != JsonValueKind.Number) return null;
        if (value.TryGetDecimal(out var decimalValue)) return decimalValue;
        if (value.TryGetDouble(out var doubleValue) && double.IsFinite(doubleValue)) return Convert.ToDecimal(doubleValue, CultureInfo.InvariantCulture);
        return null;
    }

    private static string NormalizeSymbol(string symbol) => string.IsNullOrWhiteSpace(symbol) ? "^GSPC" : symbol.Trim();
}

public sealed class BenchmarkRefreshResult
{
    public bool Succeeded { get; init; }
    public bool Skipped { get; init; }
    public int StoredPointCount { get; init; }
    public string Message { get; init; } = string.Empty;
    public bool Failed => !Succeeded && !Skipped;
}

public sealed class BenchmarkRefreshService
{
    private readonly TradeFoundryDb _database;
    private readonly IBenchmarkDataProvider _provider;
    private readonly BenchmarkOptions _options;
    private readonly ILogger<BenchmarkRefreshService> _logger;

    public BenchmarkRefreshService(TradeFoundryDb database, IBenchmarkDataProvider provider, IOptions<BenchmarkOptions> options, ILogger<BenchmarkRefreshService> logger)
    {
        _database = database;
        _provider = provider;
        _options = options.Value;
        _logger = logger;
    }

    public string DefaultSymbol => NormalizeSymbol(_options.DefaultSymbol);

    public async Task<BenchmarkRefreshResult> RefreshAsync(Guid journalId, IReadOnlyList<Trade> trades, bool force, CancellationToken cancellationToken = default)
    {
        var completedTrades = trades.Where(x => x.ExitUtc.HasValue).OrderBy(x => x.ExitUtc).ThenBy(x => x.Sequence).ToArray();
        if (completedTrades.Length == 0)
            return new BenchmarkRefreshResult { Skipped = true, Message = "Benchmark data will download after the first completed trade." };
        if (!force && !_options.AutomaticRefresh)
            return new BenchmarkRefreshResult { Skipped = true, Message = "Automatic benchmark refresh is disabled." };

        var symbol = DefaultSymbol;
        var firstDate = DateOnly.FromDateTime(completedTrades[0].ExitUtc!.Value.UtcDateTime.Date);
        var lastDate = DateOnly.FromDateTime(completedTrades[^1].ExitUtc!.Value.UtcDateTime.Date);
        var requestedStart = firstDate.AddDays(-Math.Clamp(_options.StartPaddingDays, 0, 60));
        var requestedEnd = lastDate.AddDays(Math.Clamp(_options.EndPaddingDays, 0, 60));
        var now = DateTimeOffset.UtcNow;
        var interval = TimeSpan.FromMinutes(Math.Clamp(_options.RefreshIntervalMinutes, 1, 10_080));
        var status = _database.GetBenchmarkSeriesStatus(journalId, symbol, TradeFoundryConstants.YahooFinanceBenchmarkSource);

        if (!force && status?.LastError is { Length: > 0 } && status.LastAttemptedUtc.HasValue && now - status.LastAttemptedUtc.Value < interval)
            return new BenchmarkRefreshResult { Skipped = true, Message = "The previous benchmark refresh failed recently; the cached result was kept." };
        if (!force && !NeedsRefresh(status, requestedStart, lastDate, now, interval))
            return new BenchmarkRefreshResult { Skipped = true, Message = "Benchmark data is up to date." };

        try
        {
            var download = await _provider.DownloadAsync(symbol, requestedStart, requestedEnd, cancellationToken);
            var stored = _database.StoreDownloadedBenchmark(journalId, download.Symbol, TradeFoundryConstants.YahooFinanceBenchmarkSource, download.SourceUri.ToString(), requestedStart, requestedEnd, download.FetchedUtc, download.Points);
            if (stored == 0) throw new BenchmarkDataException("The benchmark download contained no storable points.");
            return new BenchmarkRefreshResult
            {
                Succeeded = true,
                StoredPointCount = stored,
                Message = $"Updated {stored:N0} daily {download.Symbol} benchmark point(s) from Yahoo Finance."
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return await RecordFailureAsync(journalId, symbol, requestedStart, requestedEnd, now, "Yahoo Finance benchmark download timed out.");
        }
        catch (BenchmarkDataException ex)
        {
            return await RecordFailureAsync(journalId, symbol, requestedStart, requestedEnd, now, ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return await RecordFailureAsync(journalId, symbol, requestedStart, requestedEnd, now, $"Yahoo Finance request failed: {ex.Message}");
        }
        catch (JsonException ex)
        {
            return await RecordFailureAsync(journalId, symbol, requestedStart, requestedEnd, now, $"Yahoo Finance returned invalid JSON: {ex.Message}");
        }
    }

    private async Task<BenchmarkRefreshResult> RecordFailureAsync(Guid journalId, string symbol, DateOnly requestedStart, DateOnly requestedEnd, DateTimeOffset attemptedUtc, string error)
    {
        _logger.LogWarning("Benchmark refresh failed for {Symbol}: {Error}", symbol, error);
        try
        {
            _database.RecordBenchmarkDownloadFailure(journalId, symbol, TradeFoundryConstants.YahooFinanceBenchmarkSource, string.Empty, requestedStart, requestedEnd, attemptedUtc, error);
        }
        catch (Exception persistenceException)
        {
            _logger.LogWarning(persistenceException, "Could not record benchmark refresh failure for {Symbol}.", symbol);
        }

        await Task.CompletedTask;
        return new BenchmarkRefreshResult { Message = error };
    }

    private static bool NeedsRefresh(BenchmarkSeriesStatus? status, DateOnly requestedStart, DateOnly latestTradeDate, DateTimeOffset now, TimeSpan interval)
    {
        if (status is null || status.PointCount == 0 || !status.LastFetchedUtc.HasValue) return true;
        if (!status.FirstDate.HasValue || status.FirstDate.Value > requestedStart) return true;
        if (!status.LastDate.HasValue || status.LastDate.Value < latestTradeDate) return true;
        return now - status.LastFetchedUtc.Value >= interval;
    }

    private static string NormalizeSymbol(string? symbol) => string.IsNullOrWhiteSpace(symbol) ? "^GSPC" : symbol.Trim();
}
