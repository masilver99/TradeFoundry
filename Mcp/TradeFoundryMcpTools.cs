using System.ComponentModel;
using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TradeFoundry.Core;
using TradeFoundry.Data;
using TradeFoundry.Services;

namespace TradeFoundry.Mcp;

[McpServerToolType]
[Authorize(Policy = McpAuthentication.Policy)]
public sealed class TradeFoundryMcpTools(
    JournalAnalysisService analysis,
    TradeFoundryDb database,
    MarketProbabilityService marketProbability,
    ILogger<TradeFoundryMcpTools> logger)
{
    [McpServerTool(Name = "list_broker_fee_profiles", Title = "List broker fee profiles", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists saved user-managed broker fee profiles for a journal. Profiles can be scoped to an instrument or apply to all instruments; they are the editable inputs used by the broker cost comparison page.")]
    public McpBrokerFeeProfileListResponse ListBrokerFeeProfiles(RequestContext<CallToolRequestParams> context, Guid journal_id, string? instrument = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("list_broker_fee_profiles", RequireTokenId(user), journal_id, () =>
        {
            var journal = database.GetJournal(journal_id) ?? throw new KeyNotFoundException("The requested journal was not found.");
            var profiles = database.GetBrokerFeeProfiles(journal_id, instrument);
            return new McpBrokerFeeProfileListResponse
            {
                JournalId = journal_id.ToString("D"),
                Currency = journal.Currency,
                Instrument = string.IsNullOrWhiteSpace(instrument) ? null : instrument,
                AppPath = BrokerComparisonPath(journal_id, instrument),
                Profiles = profiles.Select(ToMcpProfile).ToArray()
            };
        });
    }

    [McpServerTool(Name = "create_broker_fee_profile", Title = "Create broker fee profile", ReadOnly = false, OpenWorld = false, UseStructuredContent = true)]
    [Authorize(Policy = McpAuthentication.WritePolicy)]
    [Description("Creates a user-managed broker fee profile for a journal. Requires a write-scoped MCP token. This does not alter imported trades, fills, or other journal evidence.")]
    public McpBrokerFeeProfileMutationResponse CreateBrokerFeeProfile(RequestContext<CallToolRequestParams> context, Guid journal_id, BrokerFeeProfileInput input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        EnsureWriteAccess(user);
        var tokenId = RequireTokenId(user);
        return Run("create_broker_fee_profile", tokenId, journal_id, () =>
        {
            var profile = database.CreateBrokerFeeProfile(journal_id, ToDraft(input));
            return MutationResponse(journal_id, saved: true, conflict: false, notFound: false, "Broker fee profile created.", profile);
        });
    }

    [McpServerTool(Name = "update_broker_fee_profile", Title = "Update broker fee profile", ReadOnly = false, OpenWorld = false, UseStructuredContent = true)]
    [Authorize(Policy = McpAuthentication.WritePolicy)]
    [Description("Updates a user-managed broker fee profile with optimistic revision checking. Requires a write-scoped MCP token and the current profile revision. This does not alter imported trades, fills, or other journal evidence.")]
    public McpBrokerFeeProfileMutationResponse UpdateBrokerFeeProfile(RequestContext<CallToolRequestParams> context, Guid journal_id, Guid profile_id, int expected_revision, BrokerFeeProfileInput input, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        EnsureWriteAccess(user);
        var tokenId = RequireTokenId(user);
        return Run("update_broker_fee_profile", tokenId, journal_id, () =>
        {
            var result = database.UpdateBrokerFeeProfile(journal_id, profile_id, expected_revision, ToDraft(input));
            var message = result.Conflict
                ? "Revision conflict. Refresh the profile and retry with its current revision."
                : result.NotFound
                    ? "The broker fee profile was not found."
                    : "Broker fee profile updated.";
            return MutationResponse(journal_id, result.Saved, result.Conflict, result.NotFound, message, result.Profile);
        });
    }

    [McpServerTool(Name = "list_journals", Title = "List accessible journals", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Lists only the TradeFoundry journals granted to this MCP token, with coverage and evidence availability.")]
    public McpJournalListResponse ListJournals(RequestContext<CallToolRequestParams> context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        var tokenId = RequireTokenId(user);
        return Run("list_journals", tokenId, null, () => analysis.ListJournals(tokenId));
    }

    [McpServerTool(Name = "get_journal_overview", Title = "Get journal overview", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns deterministic historical performance metrics for a journal and optional trade filters. It never refreshes external data.")]
    public McpJournalOverviewResponse GetJournalOverview(RequestContext<CallToolRequestParams> context, Guid journal_id, TradeFilterInput? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("get_journal_overview", RequireTokenId(user), journal_id, () => analysis.GetOverview(journal_id, filter));
    }

    [McpServerTool(Name = "search_trades", Title = "Search trades", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Searches a journal's historical trades with local-date, symbol, account, direction, outcome, P&L, R-multiple, and text filters. Results are cursor paginated.")]
    public McpTradeSearchResponse SearchTrades(RequestContext<CallToolRequestParams> context, Guid journal_id, TradeFilterInput? filter = null, string sort = "entry_desc", int limit = 25, string? cursor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("search_trades", RequireTokenId(user), journal_id, () => analysis.SearchTrades(journal_id, filter, sort, limit, cursor));
    }

    [McpServerTool(Name = "get_trade_detail", Title = "Get trade detail", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets one trade by its stable review key, including risk, excursion, execution, provenance, nearby normalized order events, and explicit evidence gaps.")]
    public McpTradeDetailResponse GetTradeDetail(RequestContext<CallToolRequestParams> context, Guid journal_id, string review_key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("get_trade_detail", RequireTokenId(user), journal_id, () => analysis.GetTradeDetail(journal_id, review_key));
    }

    [McpServerTool(Name = "get_trade_price_context", Title = "Get trade price context", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Gets bounded stored OHLC bars around a trade. This is historical imported data, not a live market feed.")]
    public McpTradePriceContextResponse GetTradePriceContext(RequestContext<CallToolRequestParams> context, Guid journal_id, string review_key, string interval = "1m", int minutes_before = 30, int minutes_after = 30, int max_bars = 300, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("get_trade_price_context", RequireTokenId(user), journal_id, () => analysis.GetTradePriceContext(journal_id, review_key, interval, minutes_before, minutes_after, max_bars));
    }

    [McpServerTool(Name = "get_trading_day", Title = "Get trading day", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns trades opened and closed on one YYYY-MM-DD journal-local date, with realized metrics and evidence gaps.")]
    public McpTradingDayResponse GetTradingDay(RequestContext<CallToolRequestParams> context, Guid journal_id, string date, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("get_trading_day", RequireTokenId(user), journal_id, () => analysis.GetTradingDay(journal_id, date));
    }

    [McpServerTool(Name = "analyze_trades", Title = "Analyze trade cohorts", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Computes deterministic cohort metrics grouped by symbol, direction, entry_hour, weekday, session, week, or month, with small-sample warnings.")]
    public Task<McpTradeAnalysisResponse> AnalyzeTrades(RequestContext<CallToolRequestParams> context, Guid journal_id, string group_by, TradeFilterInput? filter = null, CancellationToken cancellationToken = default)
    {
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        var tokenId = RequireTokenId(user);
        return RunAsync("analyze_trades", tokenId, journal_id, () => analysis.AnalyzeTradesAsync(journal_id, filter, group_by, cancellationToken));
    }

    [McpServerTool(Name = "get_data_quality", Title = "Get journal data quality", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Reports missing risk, excursion, execution, bar, and cached benchmark evidence for the requested historical trade scope.")]
    public Task<McpDataQualityResponse> GetDataQuality(RequestContext<CallToolRequestParams> context, Guid journal_id, TradeFilterInput? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return RunAsync("get_data_quality", RequireTokenId(user), journal_id, () => analysis.GetDataQualityAsync(journal_id, filter, cancellationToken));
    }

    [McpServerTool(Name = "get_market_type_probabilities", Title = "Get market type probabilities", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns deterministic historical ES/MES market-day type, direction, volatility, and RTH-range statistics. Filters are applied inside TradeFoundry; raw and Bayesian-shrunk probabilities, baselines, and uncertainty intervals are returned separately. AsOfDate is exclusive to prevent look-ahead.")]
    public McpMarketProbabilityResponse GetMarketTypeProbabilities(RequestContext<CallToolRequestParams> context, Guid journal_id, MarketProbabilityFilterInput? filter = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("get_market_type_probabilities", RequireTokenId(user), journal_id, () =>
        {
            var query = ToMarketProbabilityQuery(filter);
            return ToMcpMarketProbability(journal_id, query, marketProbability.GetProbabilities(journal_id, query));
        });
    }

    [McpServerTool(Name = "get_market_days", Title = "Get classified market days", ReadOnly = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Returns bounded, paginated deterministic daily ES/MES observations and classifications from stored intraday OHLC bars. The same calendar, volatility, overnight, gap, date-window, and exclusive AsOfDate filters are supported.")]
    public McpMarketDaysResponse GetMarketDays(RequestContext<CallToolRequestParams> context, Guid journal_id, MarketProbabilityFilterInput? filter = null, int limit = 100, string? cursor = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = RequireUser(context);
        EnsureJournalAccess(user, journal_id);
        return Run("get_market_days", RequireTokenId(user), journal_id, () =>
        {
            var query = ToMarketProbabilityQuery(filter);
            return ToMcpMarketDays(journal_id, query, marketProbability.GetMarketDays(journal_id, query, limit, cursor));
        });
    }

    private T Run<T>(string tool, Guid tokenId, Guid? journalId, Func<T> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = action();
            logger.LogInformation("MCP tool {Tool} succeeded for token {TokenId} journal {JournalId} in {ElapsedMs} ms with {RowCount} rows", tool, tokenId, journalId, stopwatch.ElapsedMilliseconds, RowCount(result));
            return result;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MCP tool {Tool} failed for token {TokenId} journal {JournalId} after {ElapsedMs} ms with {RowCount} rows", tool, tokenId, journalId, stopwatch.ElapsedMilliseconds, 0);
            throw;
        }
    }

    private async Task<T> RunAsync<T>(string tool, Guid tokenId, Guid? journalId, Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            logger.LogInformation("MCP tool {Tool} succeeded for token {TokenId} journal {JournalId} in {ElapsedMs} ms with {RowCount} rows", tool, tokenId, journalId, stopwatch.ElapsedMilliseconds, RowCount(result));
            return result;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "MCP tool {Tool} failed for token {TokenId} journal {JournalId} after {ElapsedMs} ms with {RowCount} rows", tool, tokenId, journalId, stopwatch.ElapsedMilliseconds, 0);
            throw;
        }
    }

    private static Guid RequireTokenId(ClaimsPrincipal user)
    {
        return Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new UnauthorizedAccessException("The MCP token identity is missing.");
    }

    private static ClaimsPrincipal RequireUser(RequestContext<CallToolRequestParams> context) =>
        context.User ?? throw new UnauthorizedAccessException("The MCP token identity is missing.");

    private static void EnsureJournalAccess(ClaimsPrincipal user, Guid journalId)
    {
        var allowed = user.FindAll(McpAuthentication.JournalClaim).Any(claim => Guid.TryParse(claim.Value, out var id) && id == journalId);
        if (!allowed) throw new KeyNotFoundException("The requested journal or trade was not found.");
    }

    private static void EnsureWriteAccess(ClaimsPrincipal user)
    {
        if (!McpAuthentication.HasScope(user, "write"))
            throw new UnauthorizedAccessException("This MCP token is read-only. Use a write-scoped token to manage broker fee profiles.");
    }

    private static BrokerFeeProfileDraft ToDraft(BrokerFeeProfileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new BrokerFeeProfileDraft
        {
            Name = input.Name,
            Instrument = input.Instrument,
            Notes = input.Notes ?? string.Empty,
            CommissionPerContractSide = input.CommissionPerContractSide,
            ExchangePerContractSide = input.ExchangePerContractSide,
            NfaPerContractSide = input.NfaPerContractSide,
            ClearingPerContractSide = input.ClearingPerContractSide,
            PlatformMonthly = input.PlatformMonthly,
            DataMonthly = input.DataMonthly,
            OtherMonthly = input.OtherMonthly
        };
    }

    private static McpBrokerFeeProfileItem ToMcpProfile(BrokerFeeProfile profile) => new()
    {
        ProfileId = profile.Id.ToString("D"),
        JournalId = profile.JournalId.ToString("D"),
        Name = profile.Name,
        Instrument = profile.Instrument,
        Notes = profile.Notes,
        CommissionPerContractSide = profile.CommissionPerContractSide,
        ExchangePerContractSide = profile.ExchangePerContractSide,
        NfaPerContractSide = profile.NfaPerContractSide,
        ClearingPerContractSide = profile.ClearingPerContractSide,
        PlatformMonthly = profile.PlatformMonthly,
        DataMonthly = profile.DataMonthly,
        OtherMonthly = profile.OtherMonthly,
        Revision = profile.Revision,
        CreatedUtc = profile.CreatedUtc.ToUniversalTime().ToString("O"),
        UpdatedUtc = profile.UpdatedUtc.ToUniversalTime().ToString("O")
    };

    private static McpBrokerFeeProfileMutationResponse MutationResponse(Guid journalId, bool saved, bool conflict, bool notFound, string message, BrokerFeeProfile? profile) => new()
    {
        JournalId = journalId.ToString("D"),
        Saved = saved,
        Conflict = conflict,
        NotFound = notFound,
        Message = message,
        Profile = profile is null ? null : ToMcpProfile(profile)
    };

    private static MarketProbabilityQuery ToMarketProbabilityQuery(MarketProbabilityFilterInput? input)
    {
        input ??= new MarketProbabilityFilterInput();
        return new MarketProbabilityQuery
        {
            Symbol = string.IsNullOrWhiteSpace(input.Symbol) ? null : input.Symbol.Trim(),
            Month = input.Month,
            DayOfWeek = ParseDayOfWeek(input.DayOfWeek),
            StartDate = ParseMarketDate(input.StartDate, nameof(input.StartDate)),
            EndDate = ParseMarketDate(input.EndDate, nameof(input.EndDate)),
            AsOfDate = ParseMarketDate(input.AsOfDate, nameof(input.AsOfDate)),
            VolatilityRegime = ParseEnum<VolatilityRegime>(input.VolatilityRegime, nameof(input.VolatilityRegime)),
            OvernightDirection = ParseDirection(input.OvernightDirection, nameof(input.OvernightDirection)),
            GapDirection = ParseDirection(input.GapDirection, nameof(input.GapDirection)),
            RangeGreaterThanPoints = input.RangeGreaterThanPoints,
            RangeLessThanPoints = input.RangeLessThanPoints,
            NormalizedRangeGreaterThan = input.NormalizedRangeGreaterThan,
            NormalizedRangeLessThan = input.NormalizedRangeLessThan
        };
    }

    private static DateOnly? ParseMarketDate(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return DateOnly.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date)
            ? date
            : throw new ArgumentException($"{name} must use YYYY-MM-DD format.");
    }

    private static DayOfWeek? ParseDayOfWeek(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<DayOfWeek>(value.Trim(), ignoreCase: true, out var day)
            ? day
            : throw new ArgumentException("day_of_week must be a valid day such as Tuesday.");
    }

    private static MarketDirection? ParseDirection(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Equals("unknown", StringComparison.OrdinalIgnoreCase) || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
            return MarketDirection.Neutral;
        return ParseEnum<MarketDirection>(value, name);
    }

    private static T? ParseEnum<T>(string? value, string name) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return Enum.TryParse<T>(value.Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : throw new ArgumentException($"{name} is invalid.");
    }

    private static McpMarketProbabilityResponse ToMcpMarketProbability(Guid journalId, MarketProbabilityQuery query, MarketProbabilityQueryResult result) => new()
    {
        JournalId = journalId.ToString("D"),
        PopulationSymbol = result.PopulationSymbol,
        AppliedFilters = ToMcpFilter(query),
        FilterDescription = result.FilterDescription,
        SampleSize = result.SampleSize,
        AvailableObservationCount = result.AvailableObservationCount,
        ClassifierVersion = result.ClassifierVersion,
        SessionDefinition = result.SessionDefinition,
        MarketTypes = result.MarketTypes.Select(ToMcpEstimate).ToArray(),
        Directions = result.Directions.Select(ToMcpEstimate).ToArray(),
        DirectionGivenTrend = new McpConditionalDirection
        {
            Condition = result.DirectionGivenTrend.Condition,
            TrendSampleSize = result.DirectionGivenTrend.TrendSampleSize,
            Directions = result.DirectionGivenTrend.Directions.Select(ToMcpEstimate).ToArray()
        },
        RthRange = ToMcpRange(result.RthRange),
        NormalizedRange = ToMcpRange(result.NormalizedRange),
        ThresholdProbabilities = result.ThresholdProbabilities.Select(ToMcpThreshold).ToArray(),
        Baselines = result.Baselines.Select(ToMcpBaseline).ToArray(),
        ShrinkagePriorScope = result.ShrinkagePriorScope,
        ShrinkagePriorSampleSize = result.ShrinkagePriorSampleSize,
        DataQualityWarnings = result.DataQualityWarnings,
        AppPath = $"/journal/{journalId:D}/market-regime"
    };

    private static McpMarketDaysResponse ToMcpMarketDays(Guid journalId, MarketProbabilityQuery query, MarketDaysResult result) => new()
    {
        JournalId = journalId.ToString("D"),
        PopulationSymbol = result.PopulationSymbol,
        AppliedFilters = ToMcpFilter(query),
        TotalMatches = result.TotalMatches,
        Truncated = result.Truncated,
        NextCursor = result.NextCursor,
        DataQualityWarnings = result.DataQualityWarnings,
        Days = result.Days.Select(day => new McpMarketDay(
            day.TradeDate.ToString("yyyy-MM-dd"),
            day.Symbol,
            day.DayOfWeek.ToString(),
            day.Month,
            day.Year,
            day.Open,
            day.High,
            day.Low,
            day.Close,
            day.RthRangePoints,
            day.Atr20,
            day.NormalizedRange,
            day.OpenToClosePoints,
            day.DirectionalEfficiency,
            day.PathEfficiency,
            day.CloseLocation,
            day.VwapCrossings,
            day.PercentSessionAboveVwap,
            day.PercentSessionBelowVwap,
            day.PercentHigherHighs,
            day.PercentHigherLows,
            day.PercentLowerHighs,
            day.PercentLowerLows,
            day.MaximumFavorableDirectionalExcursion,
            day.MaximumCountertrendExcursion,
            day.OvernightHigh,
            day.OvernightLow,
            day.OvernightRange,
            day.OvernightDirection.ToString(),
            day.GapFromPriorRthClose,
            day.GapDirection.ToString(),
            day.VolatilityMeasurePoints,
            day.VolatilityRegime.ToString(),
            day.MarketType.ToString(),
            day.Direction.ToString(),
            day.ClassifierVersion,
            day.RthBarCount,
            day.OvernightBarCount)).ToArray(),
        AppPath = $"/journal/{journalId:D}/market-regime"
    };

    private static McpMarketProbabilityFilter ToMcpFilter(MarketProbabilityQuery query) => new(
        query.Symbol,
        query.Month,
        query.DayOfWeek?.ToString(),
        query.StartDate?.ToString("yyyy-MM-dd"),
        query.EndDate?.ToString("yyyy-MM-dd"),
        query.AsOfDate?.ToString("yyyy-MM-dd"),
        query.VolatilityRegime?.ToString(),
        query.OvernightDirection?.ToString(),
        query.GapDirection?.ToString(),
        query.RangeGreaterThanPoints,
        query.RangeLessThanPoints,
        query.NormalizedRangeGreaterThan,
        query.NormalizedRangeLessThan);

    private static McpProbabilityEstimate ToMcpEstimate(ProbabilityEstimate estimate) => new()
    {
        Outcome = estimate.Outcome,
        Count = estimate.Count,
        SampleSize = estimate.SampleSize,
        RawProbability = estimate.RawProbability,
        AdjustedProbability = estimate.AdjustedProbability,
        BaselineProbability = estimate.BaselineProbability,
        DifferenceFromBaseline = estimate.DifferenceFromBaseline,
        ConfidenceInterval = ToMcpInterval(estimate.ConfidenceInterval),
        CredibleInterval = ToMcpInterval(estimate.CredibleInterval),
        ShrinkagePriorScope = estimate.ShrinkagePriorScope,
        ShrinkagePriorSampleSize = estimate.ShrinkagePriorSampleSize,
        ShrinkagePriorProbability = estimate.ShrinkagePriorProbability
    };

    private static McpProbabilityInterval ToMcpInterval(ProbabilityInterval interval) => new(interval.Lower, interval.Upper, interval.Method);

    private static McpRangeDistribution ToMcpRange(RangeDistribution range) => new()
    {
        SampleSize = range.SampleSize,
        Mean = range.Mean,
        Median = range.Median,
        StandardDeviation = range.StandardDeviation,
        P10 = range.P10,
        P25 = range.P25,
        P50 = range.P50,
        P75 = range.P75,
        P90 = range.P90
    };

    private static McpThresholdProbability ToMcpThreshold(ThresholdProbability threshold) => new()
    {
        Metric = threshold.Metric,
        Operator = threshold.Operator,
        Threshold = threshold.Threshold,
        Count = threshold.Count,
        SampleSize = threshold.SampleSize,
        RawProbability = threshold.RawProbability,
        BaselineProbability = threshold.BaselineProbability,
        DifferenceFromBaseline = threshold.DifferenceFromBaseline,
        ConfidenceInterval = ToMcpInterval(threshold.ConfidenceInterval)
    };

    private static McpMarketProbabilityBaseline ToMcpBaseline(MarketProbabilityBaseline baseline) => new()
    {
        Scope = baseline.Scope,
        SampleSize = baseline.SampleSize,
        MarketTypes = baseline.MarketTypes.Select(ToMcpBaselineProbability).ToArray(),
        Directions = baseline.Directions.Select(ToMcpBaselineProbability).ToArray(),
        DirectionsGivenTrend = baseline.DirectionsGivenTrend.Select(ToMcpBaselineProbability).ToArray()
    };

    private static McpBaselineProbability ToMcpBaselineProbability(BaselineProbability probability) => new()
    {
        Outcome = probability.Outcome,
        Count = probability.Count,
        SampleSize = probability.SampleSize,
        RawProbability = probability.RawProbability
    };

    private static string BrokerComparisonPath(Guid journalId, string? instrument) =>
        $"/journal/{journalId:D}/broker-comparison" + (string.IsNullOrWhiteSpace(instrument) ? string.Empty : $"?instrument={Uri.EscapeDataString(instrument)}");

    private static int RowCount<T>(T result) => result switch
    {
        McpJournalListResponse response => response.Journals.Count,
        McpJournalOverviewResponse response => response.Metrics.ClosedTrades + response.Metrics.OpenTrades,
        McpTradeSearchResponse response => response.Trades.Count,
        McpTradeDetailResponse => 1,
        McpTradePriceContextResponse response => response.Bars.Count,
        McpTradingDayResponse response => response.OpenedTrades.Count + response.ClosedTrades.Count,
        McpTradeAnalysisResponse response => response.Cohorts.Count,
        McpDataQualityResponse response => response.TradeCount,
        McpMarketProbabilityResponse response => response.SampleSize,
        McpMarketDaysResponse response => response.Days.Count,
        McpBrokerFeeProfileListResponse response => response.Profiles.Count,
        McpBrokerFeeProfileMutationResponse response => response.Profile is null ? 0 : 1,
        _ => 0
    };
}
