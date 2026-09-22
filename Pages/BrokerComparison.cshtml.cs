using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using TradeFoundry.Core;
using TradeFoundry.Data;

namespace TradeFoundry.Pages;

[Authorize]
public sealed class BrokerComparisonModel : PageModel
{
    public const string AllInstrumentsValue = "*";

    private readonly TradeFoundryDb _database;

    public BrokerComparisonModel(TradeFoundryDb database)
    {
        _database = database;
    }

    [BindProperty(SupportsGet = true)]
    public Guid JournalId { get; set; }

    [BindProperty(SupportsGet = true, Name = "instrument")]
    public string? Instrument { get; set; }

    public Journal? Journal { get; private set; }
    public IReadOnlyList<InstrumentOption> InstrumentOptions { get; private set; } = Array.Empty<InstrumentOption>();
    public IReadOnlyList<BrokerRow> BrokerRows { get; private set; } = Array.Empty<BrokerRow>();
    public ActivitySnapshot Activity { get; private set; } = new();
    public string SelectedInstrumentLabel { get; private set; } = "All instruments";
    public string Currency => string.IsNullOrWhiteSpace(Journal?.Currency) ? "USD" : Journal!.Currency;
    public string StorageKey
    {
        get
        {
            var revisions = string.Join("|", BrokerRows
                .Where(row => row.ProfileId.HasValue)
                .OrderBy(row => row.ProfileId)
                .Select(row => $"{row.ProfileId:D}:{row.Revision}"));
            return $"tradefoundry.broker-comparison.{JournalId:D}.{Instrument ?? AllInstrumentsValue}.{revisions}";
        }
    }
    public string InitialChartJson { get; private set; } = EmptyChartJson;
    public string InitialVolumeChartJson { get; private set; } = EmptyChartJson;
    public bool HasConfiguredRows => BrokerRows.Any(row => row.HasRateData);

    private static string EmptyChartJson => JsonSerializer.Serialize(new
    {
        data = Array.Empty<object>(),
        layout = new
        {
            paper_bgcolor = "rgba(0,0,0,0)",
            plot_bgcolor = "rgba(0,0,0,0)",
            font = new { color = "#c9d1d9" }
        },
        config = new
        {
            responsive = true,
            displaylogo = false,
            modeBarButtonsToRemove = new[] { "lasso2d", "select2d" }
        }
    });

    public IActionResult OnGet()
    {
        Journal = _database.GetJournal(JournalId);
        if (Journal is null) return NotFound();

        var allTrades = _database.GetAllTrades(JournalId);
        var completedTrades = allTrades
            .Where(trade => trade.ExitUtc.HasValue && ContractsForTrade(trade) > 0)
            .ToArray();
        var configuredInstruments = _database.GetInstruments();
        var observedCodes = completedTrades
            .Select(InstrumentCode)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var optionCodes = configuredInstruments
            .Select(instrument => InstrumentConfiguration.NormalizeCode(instrument.Code))
            .Concat(observedCodes)
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        InstrumentOptions = new[] { new InstrumentOption(AllInstrumentsValue, "All instruments") }
            .Concat(optionCodes.Select(code => new InstrumentOption(code, code)))
            .ToArray();

        var requestedInstrument = InstrumentConfiguration.NormalizeCode(Instrument ?? string.Empty);
        if (string.IsNullOrWhiteSpace(requestedInstrument))
        {
            requestedInstrument = observedCodes.Length == 1 ? observedCodes[0] : AllInstrumentsValue;
        }
        if (!requestedInstrument.Equals(AllInstrumentsValue, StringComparison.OrdinalIgnoreCase)
            && !optionCodes.Contains(requestedInstrument, StringComparer.OrdinalIgnoreCase))
        {
            requestedInstrument = AllInstrumentsValue;
        }
        Instrument = requestedInstrument;
        SelectedInstrumentLabel = requestedInstrument.Equals(AllInstrumentsValue, StringComparison.OrdinalIgnoreCase)
            ? "All instruments"
            : requestedInstrument;

        var selectedTrades = requestedInstrument.Equals(AllInstrumentsValue, StringComparison.OrdinalIgnoreCase)
            ? completedTrades
            : completedTrades.Where(trade => InstrumentCode(trade).Equals(requestedInstrument, StringComparison.OrdinalIgnoreCase)).ToArray();
        Activity = BuildActivity(selectedTrades);

        var current = BuildCurrentRow(selectedTrades, configuredInstruments, requestedInstrument);
        var savedProfiles = _database.GetBrokerFeeProfiles(JournalId, requestedInstrument);
        var savedRows = savedProfiles.Select(ProfileToRow).ToArray();
        BrokerRows = new[] { current }
            .Concat(savedRows)
            .Concat(savedRows.Length == 0
                ? new[]
                {
                    new BrokerRow { Name = "AMP Futures" },
                    new BrokerRow { Name = "NinjaTrader" },
                    new BrokerRow { Name = "Interactive Brokers" }
                }
                : Array.Empty<BrokerRow>())
            .ToArray();
        InitialChartJson = BuildChartJson(BrokerRows.Where(row => row.HasRateData));
        InitialVolumeChartJson = BuildVolumeChartJson(BrokerRows.Where(row => row.HasRateData));

        return Page();
    }

    public string FormatRate(decimal? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? string.Empty;

    public string FormatEstimate(BrokerRow row, bool annual = false)
    {
        if (!row.HasRateData) return "—";
        var estimate = BrokerCommissionCalculator.Estimate(row.ToRate(), Activity.TradesPerMonth, Activity.ContractsPerTrade);
        return (annual ? estimate.AnnualCost : estimate.MonthlyCost).ToString("C2", CultureInfo.CurrentCulture);
    }

    public sealed record InstrumentOption(string Value, string Label);

    public sealed class ActivitySnapshot
    {
        public int CompletedTrades { get; init; }
        public decimal ContractRoundTurns { get; init; }
        public int ObservedMonths { get; init; }
        public decimal TradesPerMonth { get; init; }
        public decimal ContractsPerTrade { get; init; } = 1m;
    }

    public sealed class BrokerRow
    {
        public Guid? ProfileId { get; init; }
        public int? Revision { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Notes { get; init; } = string.Empty;
        public bool IsBaseline { get; init; }
        public decimal? CommissionPerContractSide { get; init; }
        public decimal? ExchangePerContractSide { get; init; }
        public decimal? NfaPerContractSide { get; init; }
        public decimal? ClearingPerContractSide { get; init; }
        public decimal? PlatformMonthly { get; init; }
        public decimal? DataMonthly { get; init; }
        public decimal? OtherMonthly { get; init; }

        public bool HasRateData => ToRate().HasInput;

        public BrokerCommissionRate ToRate() => new()
        {
            CommissionPerContractSide = CommissionPerContractSide,
            ExchangePerContractSide = ExchangePerContractSide,
            NfaPerContractSide = NfaPerContractSide,
            ClearingPerContractSide = ClearingPerContractSide,
            PlatformMonthly = PlatformMonthly,
            DataMonthly = DataMonthly,
            OtherMonthly = OtherMonthly
        };
    }

    private static BrokerRow ProfileToRow(BrokerFeeProfile profile) => new()
    {
        ProfileId = profile.Id,
        Revision = profile.Revision,
        Name = profile.Name,
        Notes = profile.Notes,
        CommissionPerContractSide = profile.CommissionPerContractSide,
        ExchangePerContractSide = profile.ExchangePerContractSide,
        NfaPerContractSide = profile.NfaPerContractSide,
        ClearingPerContractSide = profile.ClearingPerContractSide,
        PlatformMonthly = profile.PlatformMonthly,
        DataMonthly = profile.DataMonthly,
        OtherMonthly = profile.OtherMonthly
    };

    private BrokerRow BuildCurrentRow(
        IReadOnlyList<Trade> trades,
        IReadOnlyList<InstrumentDefinition> instruments,
        string requestedInstrument)
    {
        var contractSides = trades.Sum(ContractsForTrade) * 2m;
        var totalFees = trades.Sum(trade => Math.Max(0m, trade.Fees));
        var exchangeFees = trades.Sum(trade => Math.Max(0m, trade.ExchangeFees));
        var nfaFees = trades.Sum(trade => Math.Max(0m, trade.NfaFees));
        var clearingFees = trades.Sum(trade => Math.Max(0m, trade.ClearingFees));
        var componentTotal = exchangeFees + nfaFees + clearingFees;

        if (contractSides > 0m && (totalFees > 0m || componentTotal > 0m))
        {
            var remainder = Math.Max(0m, totalFees - componentTotal);
            return new BrokerRow
            {
                Name = "Current setup",
                IsBaseline = true,
                CommissionPerContractSide = remainder > 0m ? remainder / contractSides : null,
                ExchangePerContractSide = exchangeFees > 0m ? exchangeFees / contractSides : null,
                NfaPerContractSide = nfaFees > 0m ? nfaFees / contractSides : null,
                ClearingPerContractSide = clearingFees > 0m ? clearingFees / contractSides : null
            };
        }

        if (!requestedInstrument.Equals(AllInstrumentsValue, StringComparison.OrdinalIgnoreCase))
        {
            var definition = instruments.FirstOrDefault(instrument => instrument.Code.Equals(requestedInstrument, StringComparison.OrdinalIgnoreCase));
            if (definition is not null)
            {
                return new BrokerRow
                {
                    Name = "Current setup",
                    IsBaseline = true,
                    CommissionPerContractSide = definition.HasFeeBreakdown ? null : definition.DefaultCommission,
                    ExchangePerContractSide = definition.ExchangeFeePerContract,
                    NfaPerContractSide = definition.NfaFeePerContract,
                    ClearingPerContractSide = definition.ClearingFeePerContract
                };
            }
        }

        return new BrokerRow { Name = "Current setup", IsBaseline = true };
    }

    private ActivitySnapshot BuildActivity(IReadOnlyList<Trade> trades)
    {
        if (trades.Count == 0)
        {
            return new ActivitySnapshot();
        }

        var localExitDates = trades
            .Where(trade => trade.ExitUtc.HasValue)
            .Select(trade => TimeZoneCatalog.Convert(trade.ExitUtc!.Value, Journal?.TimeZone).Date)
            .ToArray();
        var first = localExitDates.Min();
        var last = localExitDates.Max();
        var months = Math.Max(1, (last.Year - first.Year) * 12 + last.Month - first.Month + 1);
        var contractRoundTurns = trades.Sum(ContractsForTrade);

        return new ActivitySnapshot
        {
            CompletedTrades = trades.Count,
            ContractRoundTurns = contractRoundTurns,
            ObservedMonths = months,
            TradesPerMonth = trades.Count / (decimal)months,
            ContractsPerTrade = contractRoundTurns / trades.Count
        };
    }

    private string BuildChartJson(IEnumerable<BrokerRow> rows)
    {
        var estimates = rows
            .Select(row => (row, estimate: BrokerCommissionCalculator.Estimate(row.ToRate(), Activity.TradesPerMonth, Activity.ContractsPerTrade)))
            .ToArray();
        if (estimates.Length == 0) return EmptyChartJson;

        var payload = new
        {
            data = new object[]
            {
                new
                {
                    type = "bar",
                    orientation = "h",
                    y = estimates.Select(item => item.row.Name).ToArray(),
                    x = estimates.Select(item => item.estimate.MonthlyCost).ToArray(),
                    customdata = estimates.Select(item => item.estimate.AnnualCost).ToArray(),
                    marker = new { color = estimates.Select(item => item.row.IsBaseline ? "#d29922" : "#58a6ff").ToArray() },
                    hovertemplate = "%{y}<br>%{x:,.2f} / month<br>%{customdata:,.2f} / year<extra></extra>"
                }
            },
            layout = new
            {
                height = Math.Max(260, estimates.Length * 54 + 80),
                margin = new { l = 145, r = 24, t = 12, b = 58 },
                paper_bgcolor = "rgba(0,0,0,0)",
                plot_bgcolor = "rgba(0,0,0,0)",
                font = new { color = "#c9d1d9", size = 11 },
                xaxis = new { title = $"{Currency} / month", gridcolor = "#30363d", zerolinecolor = "#30363d" },
                yaxis = new { automargin = true, categoryorder = "total ascending" }
            },
            config = new
            {
                responsive = true,
                displaylogo = false,
                modeBarButtonsToRemove = new[] { "lasso2d", "select2d" }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private string BuildVolumeChartJson(IEnumerable<BrokerRow> rows)
    {
        var configured = rows.ToArray();
        if (configured.Length == 0) return EmptyChartJson;

        var currentRoundTurns = Math.Max(0m, Activity.TradesPerMonth);
        var volumes = BuildVolumePoints(currentRoundTurns);
        var traces = new List<object>();
        var alternativeIndex = 0;
        for (var index = 0; index < configured.Length; index++)
        {
            var row = configured[index];
            var color = ChartColor(row, alternativeIndex);
            if (!row.IsBaseline) alternativeIndex++;
            var monthly = volumes
                .Select(roundTurns => BrokerCommissionCalculator.Estimate(row.ToRate(), roundTurns, Activity.ContractsPerTrade).MonthlyCost)
                .ToArray();
            var annual = monthly.Select(value => value * 12m).ToArray();

            traces.Add(new
            {
                type = "scatter",
                mode = "lines+markers",
                name = row.Name,
                legendgroup = row.Name,
                x = volumes,
                y = monthly,
                customdata = annual,
                xaxis = "x",
                yaxis = "y",
                line = new { color, width = row.IsBaseline ? 3 : 2 },
                marker = new { color, size = 5 },
                hovertemplate = "%{fullData.name}<br>%{x:,.2f} round turns / month<br>%{y:,.2f} / month<br>%{customdata:,.2f} / year<extra></extra>"
            });
            traces.Add(new
            {
                type = "scatter",
                mode = "lines+markers",
                name = row.Name,
                legendgroup = row.Name,
                showlegend = false,
                x = volumes,
                y = annual,
                customdata = monthly,
                xaxis = "x2",
                yaxis = "y2",
                line = new { color, width = row.IsBaseline ? 3 : 2 },
                marker = new { color, size = 5 },
                hovertemplate = "%{fullData.name}<br>%{x:,.2f} round turns / month<br>%{y:,.2f} / year<br>%{customdata:,.2f} / month<extra></extra>"
            });
        }

        var payload = new
        {
            data = traces,
            layout = new
            {
                height = 560,
                margin = new { l = 78, r = 24, t = 34, b = 104 },
                paper_bgcolor = "rgba(0,0,0,0)",
                plot_bgcolor = "rgba(0,0,0,0)",
                font = new { color = "#c9d1d9", size = 11 },
                hovermode = "closest",
                legend = new { orientation = "h", x = 0, y = -0.2 },
                xaxis = new { domain = new[] { 0d, 1d }, anchor = "y", showticklabels = false, gridcolor = "#30363d", zerolinecolor = "#30363d" },
                xaxis2 = new { domain = new[] { 0d, 1d }, anchor = "y2", matches = "x", title = "Round turns / month", gridcolor = "#30363d", zerolinecolor = "#30363d" },
                yaxis = new { domain = new[] { 0.56d, 1d }, title = $"{Currency} / month", gridcolor = "#30363d", zerolinecolor = "#30363d" },
                yaxis2 = new { domain = new[] { 0d, 0.42d }, title = $"{Currency} / year", gridcolor = "#30363d", zerolinecolor = "#30363d" },
                shapes = new[]
                {
                    new
                    {
                        type = "line",
                        xref = "x",
                        yref = "paper",
                        x0 = currentRoundTurns,
                        x1 = currentRoundTurns,
                        y0 = 0,
                        y1 = 1,
                        line = new { color = "#d29922", width = 1, dash = "dot" }
                    }
                },
                annotations = new[]
                {
                    new
                    {
                        x = currentRoundTurns,
                        y = 1.04,
                        xref = "x",
                        yref = "paper",
                        text = $"Current: {currentRoundTurns.ToString("0.##", CultureInfo.CurrentCulture)} round turns / month",
                        showarrow = false,
                        font = new { color = "#d29922", size = 10 }
                    }
                }
            },
            config = new
            {
                responsive = true,
                displaylogo = false,
                modeBarButtonsToRemove = new[] { "lasso2d", "select2d" }
            }
        };

        return JsonSerializer.Serialize(payload);
    }

    private static decimal[] BuildVolumePoints(decimal currentRoundTurns)
    {
        var maximum = Math.Max(10m, Math.Ceiling(Math.Max(1m, currentRoundTurns) * 2m));
        var step = maximum / 12m;
        return Enumerable.Range(0, 13)
            .Select(index => decimal.Round(step * index, 2, MidpointRounding.AwayFromZero))
            .ToArray();
    }

    private static string ChartColor(BrokerRow row, int index) => row.IsBaseline
        ? "#d29922"
        : new[] { "#58a6ff", "#bc8cff", "#39c5cf", "#3fb950", "#f778ba", "#f0883e" }[index % 6];

    private static int ContractsForTrade(Trade trade) => Math.Max(0, trade.ClosedQuantity > 0 ? trade.ClosedQuantity : trade.Quantity);

    private static string InstrumentCode(Trade trade)
    {
        var value = string.IsNullOrWhiteSpace(trade.Instrument) ? trade.Symbol : trade.Instrument;
        return InstrumentConfiguration.NormalizeCode(InstrumentCatalog.ExtractRoot(value));
    }
}
