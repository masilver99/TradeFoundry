document.addEventListener("DOMContentLoaded", () => {
  initializeChartExportOptions();
  initializePlotlyCharts();
  initializeAnalysisNavigation();
  initializeIndicatorNavigation();
  initializeChartInfo();
  initializeSectionInfo();
  initializePeriodSummary();
  initializePnlCalendar();
  initializeSidebarResize();
  initializeImportDropzones();

  const filter = document.querySelector("#trade-filter");
  const table = document.querySelector("#trade-table");
  if (filter && table) {
    filter.addEventListener("input", () => {
      const needle = filter.value.trim().toLowerCase();
      table.querySelectorAll("tbody tr").forEach(row => {
        row.hidden = needle.length > 0 && !row.textContent.toLowerCase().includes(needle);
      });
    });
  }
});

function initializeImportDropzones() {
  document.querySelectorAll("[data-import-dropzone]").forEach(dropZone => {
    const fileInput = dropZone.querySelector("input[type=file]");
    if (!fileInput) return;
    const preview = dropZone.dataset.previewTarget ? document.querySelector(dropZone.dataset.previewTarget) : null;

    fileInput.addEventListener("change", () => {
      const file = fileInput.files?.[0];
      if (!file) return;
      const label = dropZone.querySelector("strong");
      if (label) label.textContent = file.name;
      if (!preview) return;
      const reader = new FileReader();
      reader.onload = () => {
        const lines = String(reader.result || "").replace(/^\uFEFF/, "").split(/\r?\n/).filter(Boolean).slice(0, 4);
        const header = (lines[0] || "").toLowerCase().replace(/\s+/g, "");
        const isSierraBars = header.includes("date") && header.includes("time") && header.includes("open") && header.includes("high") && header.includes("low") && header.includes("last");
        const detected = header.includes("activitytype") ? "Sierra Chart fills" : isSierraBars ? "Sierra Chart OHLC bars" : header.includes("open") && header.includes("high") && header.includes("low") && (header.includes("close") || header.includes("last")) ? "OHLCV bars" : header.includes("trade#") || header.includes("tradenumber") ? "TradingView Strategy Tester" : "TradingView account history";
        preview.textContent = `Preview · ${detected}\n${lines.join("\n")}\n\nReview the symbol and interval, then submit to commit.`;
        preview.hidden = false;
      };
      reader.readAsText(file.slice(0, 12000));
    });
    ["dragenter", "dragover"].forEach(eventName => dropZone.addEventListener(eventName, event => {
      event.preventDefault();
      dropZone.style.borderColor = "#58a6ff";
    }));
    ["dragleave", "drop"].forEach(eventName => dropZone.addEventListener(eventName, event => {
      event.preventDefault();
      dropZone.style.borderColor = "";
    }));
    dropZone.addEventListener("drop", event => {
      const files = event.dataTransfer?.files;
      if (!files?.length) return;
      try {
        const transfer = new DataTransfer();
        transfer.items.add(files[0]);
        fileInput.files = transfer.files;
      } catch {
        fileInput.files = files;
      }
      fileInput.dispatchEvent(new Event("change", { bubbles: true }));
    });
  });
}

function initializeSidebarResize() {
  const app = document.querySelector(".tf-app");
  const sidebar = app?.querySelector(".tf-sidebar");
  const resizer = app?.querySelector("[data-sidebar-resizer]");
  if (!app || !sidebar || !resizer) return;

  const minWidth = 180;
  const maxWidth = () => Math.min(360, Math.max(minWidth, window.innerWidth * .4));
  const storageKey = "tradefoundry.sidebar.width";
  let storedWidth = null;

  try {
    const value = Number.parseFloat(window.localStorage.getItem(storageKey) || "");
    if (Number.isFinite(value)) storedWidth = value;
  } catch {
    // Ignore storage restrictions; the resize still works for the current page.
  }

  const setWidth = (value, persist = false) => {
    const width = Math.round(Math.max(minWidth, Math.min(maxWidth(), value)));
    app.style.setProperty("--tf-sidebar-width", `${width}px`);
    resizer.setAttribute("aria-valuenow", String(width));
    if (!persist) return;

    try {
      window.localStorage.setItem(storageKey, String(width));
    } catch {
      // Ignore storage restrictions; the current width remains applied.
    }
  };

  setWidth(storedWidth ?? sidebar.getBoundingClientRect().width);

  let pointerId = null;
  let pointerStart = 0;
  let widthStart = 0;

  resizer.addEventListener("pointerdown", event => {
    if (event.pointerType === "mouse" && event.button !== 0) return;
    pointerId = event.pointerId;
    pointerStart = event.clientX;
    widthStart = sidebar.getBoundingClientRect().width;
    resizer.setPointerCapture?.(pointerId);
    app.classList.add("tf-sidebar-resizing");
    event.preventDefault();
  });

  resizer.addEventListener("pointermove", event => {
    if (event.pointerId !== pointerId) return;
    setWidth(widthStart + event.clientX - pointerStart);
  });

  const finishPointerResize = event => {
    if (event.pointerId !== pointerId) return;
    setWidth(sidebar.getBoundingClientRect().width, true);
    resizer.releasePointerCapture?.(pointerId);
    pointerId = null;
    app.classList.remove("tf-sidebar-resizing");
  };

  resizer.addEventListener("pointerup", finishPointerResize);
  resizer.addEventListener("pointercancel", finishPointerResize);
  resizer.addEventListener("keydown", event => {
    const currentWidth = sidebar.getBoundingClientRect().width;
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      setWidth(currentWidth + (event.key === "ArrowRight" ? 10 : -10), true);
      event.preventDefault();
    } else if (event.key === "Home") {
      setWidth(minWidth, true);
      event.preventDefault();
    } else if (event.key === "End") {
      setWidth(maxWidth(), true);
      event.preventDefault();
    }
  });

  window.addEventListener("resize", () => {
    setWidth(sidebar.getBoundingClientRect().width);
  });
}

function initializeSectionInfo() {
  document.querySelectorAll("[data-section-info-toggle]").forEach(button => {
    const panelId = button.getAttribute("aria-controls");
    const panel = panelId ? document.getElementById(panelId) : null;
    if (!panel) return;

    const label = button.dataset.sectionInfoLabel || "section";
    const syncState = expanded => {
      button.setAttribute("aria-expanded", String(expanded));
      button.setAttribute("aria-label", `${expanded ? "Hide" : "Show"} explanation for ${label}`);
      panel.hidden = !expanded;
    };

    syncState(button.getAttribute("aria-expanded") === "true");
    button.addEventListener("click", () => {
      syncState(button.getAttribute("aria-expanded") !== "true");
    });
  });
}

function initializeChartInfo() {
  const descriptions = new Map([
    ["Adjusted and raw balance", "Compare the journal's adjusted account path with the raw trade path to see how imported account history and completed-trade results line up over time."],
    ["Trade P&L waterfall", "Trace the contribution of each completed trade from the starting balance to the ending result, making clusters of gains, losses, and fee drag visible."],
    ["Gross, net, and cumulative fees", "Separate gross trading performance from net performance and cumulative fees to show how costs compound across the journal."],
    ["Daily gross P&L", "Group completed-trade results by day to reveal the journal's day-to-day rhythm before fees."],
    ["Monthly net P&L", "Summarize net P&L by month to expose seasonality, consistency, and the periods that drive the overall result."],
    ["Monthly net P&L by day", "Use the calendar to see how net P&L is distributed through each month, then drill into individual days for the underlying trades."],
    ["Yearly strategy vs benchmark", "Compare annual strategy returns with the selected benchmark to put the journal's gains and losses in a broader market context."],
    ["Distribution of monthly returns", "Show the shape and spread of monthly returns so typical outcomes, tails, and unusually strong or weak months are easy to compare."],
    ["Daily active returns", "Measure daily strategy returns relative to the benchmark to show where the journal adds or loses value independent of market direction."],
    ["Monthly return heatmap", "Scan month-by-month return patterns across years to identify recurring seasonal strength, weakness, and gaps in the record."],
    ["Strategy vs benchmark returns", "Compare cumulative strategy and benchmark paths from the same starting point to see whether the journal is building independent performance."],
    ["Monte Carlo simulated paths", "Resample the observed trades into simulated paths to estimate the range of plausible outcomes and the uncertainty around the recorded sequence."],
    ["Underwater net P&L", "Plot the distance below the running equity high to make drawdown depth, timing, and time underwater visible."],
    ["Worst five drawdown periods", "Rank the most severe drawdown episodes by depth and recovery duration so the journal's hardest stretches can be reviewed directly."],
    ["Drawdown duration and recovery", "Compare drawdown length with recovery length to show how quickly the strategy repairs losses after each equity high."],
    ["Expectancy, win rate, and Sharpe", "Track rolling expectancy, win rate, and Sharpe over a 20-trade window to see whether the edge and risk-adjusted return are stable."],
    ["Rolling six-month volatility", "Measure how the journal's six-month return variability changes over time, highlighting calmer and more turbulent regimes."],
    ["Rolling six-month Sharpe", "Track six-month Sharpe to compare return against volatility across changing conditions."],
    ["Rolling six-month Sortino", "Track six-month Sortino to compare return against downside variability, emphasizing harmful volatility."],
    ["Win rate over time", "Follow monthly win rate to see whether trade outcomes are becoming more or less consistent."],
    ["Daily P&L distribution", "Show the distribution of daily net P&L to reveal typical day size, skew, and the frequency of large wins or losses."],
    ["Trade P&L distribution", "Show the distribution of completed-trade P&L to make the journal's typical outcome and tail behavior visible."],
    ["Winner vs loser sizes", "Compare the size of winners and losers to see whether payoff asymmetry supports the strategy's expectancy."],
    ["Winner profit concentration", "Measure how much total profit comes from the largest winners so dependence on a small number of trades is explicit."],
    ["R-multiple distribution", "Express trade results in initial-risk units to compare outcomes consistently across different prices and position sizes."],
    ["MAE vs MFE", "Compare maximum adverse excursion with maximum favorable excursion for each trade to show heat taken versus opportunity available."],
    ["Heat taken by winning trades", "Focus on winning trades' maximum adverse excursion to see how much heat profitable positions typically withstand."],
    ["MAE and MFE percentile profile", "Summarize excursion percentiles to show the typical and extreme adverse and favorable paths trades travel before exit."],
    ["Time in trade vs gross P&L", "Relate holding duration to gross P&L to reveal whether time in the market is helping or hurting results."],
    ["Expectancy, win rate, and MFE capture", "Compare holding-time buckets by expectancy, win rate, and MFE capture to show where trades are most efficient."],
    ["MFE capture and profit left on table", "Compare realized profit with favorable excursion to show how much of each available move the exits capture or leave behind."],
    ["Next-trade expectancy and win rate", "Compare the next trade after wins, losses, and flat outcomes to test whether streak state changes subsequent performance."],
    ["Outcome mix", "Break completed trades into outcome categories to show which exit or result types make up the journal."],
    ["Average P&L by entry day and hour", "Map average P&L across entry day and local hour to find timing windows with stronger or weaker results."],
    ["Expectancy by session, day, and hour", "Compare expectancy and win rate across session, weekday, and hour buckets while keeping sample size visible."],
    ["Direction mix", "Compare long and short trade counts and results to show which direction is contributing to the journal."],
    ["Session mix", "Compare results across trading sessions to identify where the strategy is active and where its edge is concentrated."],
    ["Expectancy and win rate by contracts", "Show how expectancy and win rate change with position size so scaling effects and thin samples are visible."],
    ["Order execution rates", "Summarize order fills, cancels, modifications, and partial fills to show how the intended plan becomes executed orders."],
    ["Average P&L and win rate by exit type", "Compare average P&L and win rate by exit classification to show how different closing decisions perform."]
  ]);

  document.querySelectorAll(".tf-library-chart").forEach((card, index) => {
    if (card.dataset.chartInfoInitialized === "true") return;

    const title = card.querySelector(".tf-card-title");
    const titleContainer = title?.parentElement;
    const label = title?.textContent?.trim();
    const description = label ? descriptions.get(label) : null;
    if (!title || !titleContainer || !label || !description) return;

    const titleRow = document.createElement("div");
    titleRow.className = "tf-chart-card-title-row";
    titleContainer.insertBefore(titleRow, title);
    titleRow.appendChild(title);

    const button = document.createElement("button");
    button.type = "button";
    button.className = "tf-section-info-button";
    button.dataset.sectionInfoToggle = "";
    button.dataset.sectionInfoLabel = label;
    button.setAttribute("aria-expanded", "false");
    button.setAttribute("aria-controls", `chart-info-description-${index + 1}`);
    button.setAttribute("aria-label", `Show explanation for ${label}`);
    button.innerHTML = '<span aria-hidden="true">i</span>';
    titleRow.appendChild(button);

    const panel = document.createElement("p");
    panel.id = `chart-info-description-${index + 1}`;
    panel.className = "tf-section-info tf-chart-card-info";
    panel.hidden = true;
    panel.textContent = description;
    titleContainer.appendChild(panel);
    card.dataset.chartInfoInitialized = "true";
  });
}

function initializeAnalysisNavigation() {
  const pageIndex = document.querySelector("[data-analysis-index]");
  const pageLinks = Array.from(pageIndex?.querySelectorAll("[data-analysis-target]") || []);
  const sidebarLinks = Array.from(document.querySelectorAll("[data-analysis-sidebar-link]"));
  const aliases = { behavior: "trade-quality", timing: "timing-sizing" };

  if (!pageIndex) return;

  const targets = pageLinks
    .map(link => document.getElementById(link.dataset.analysisTarget))
    .filter(Boolean);

  const normalizeId = id => aliases[id] || id;
  let suppressObserverUntil = 0;
  const setActive = id => {
    const normalizedId = normalizeId(id);
    pageLinks.forEach(link => {
      const isActive = link.dataset.analysisTarget === normalizedId;
      link.classList.toggle("active", isActive);
      if (isActive) link.setAttribute("aria-current", "location");
      else link.removeAttribute("aria-current");
    });
    sidebarLinks.forEach(link => {
      const isActive = link.dataset.analysisSidebarLink === normalizedId;
      link.classList.toggle("active", isActive);
      if (isActive) link.setAttribute("aria-current", "location");
      else link.removeAttribute("aria-current");
    });
  };

  const hashId = () => normalizeId(window.location.hash.replace(/^#/, ""));
  const initialId = hashId();
  setActive(targets.some(target => target.id === initialId) ? initialId : "sc-trade-statistics");

  pageLinks.forEach(link => link.addEventListener("click", () => {
    suppressObserverUntil = performance.now() + 1200;
    setActive(link.dataset.analysisTarget);
  }));
  window.addEventListener("hashchange", () => {
    const id = hashId();
    if (targets.some(target => target.id === id)) {
      suppressObserverUntil = performance.now() + 1200;
      setActive(id);
    }
  });

  if (!("IntersectionObserver" in window)) return;

  const observer = new IntersectionObserver(entries => {
    if (performance.now() < suppressObserverUntil) return;

    const visible = entries
      .filter(entry => entry.isIntersecting)
      .sort((left, right) => left.boundingClientRect.top - right.boundingClientRect.top)[0];
    if (visible) setActive(visible.target.id);
  }, {
    rootMargin: "-10% 0px -70% 0px",
    threshold: 0
  });

  targets.forEach(target => observer.observe(target));
}

function initializeIndicatorNavigation() {
  const pageIndex = document.querySelector("[data-indicator-index]");
  const pageLinks = Array.from(pageIndex?.querySelectorAll("[data-indicator-target]") || []);
  const sidebarLinks = Array.from(document.querySelectorAll("[data-indicator-sidebar-link]"));

  if (!pageIndex) return;

  const targets = pageLinks
    .map(link => document.getElementById(link.dataset.indicatorTarget))
    .filter(Boolean);

  let suppressObserverUntil = 0;
  const setActive = id => {
    pageLinks.forEach(link => {
      const isActive = link.dataset.indicatorTarget === id;
      link.classList.toggle("active", isActive);
      if (isActive) link.setAttribute("aria-current", "location");
      else link.removeAttribute("aria-current");
    });
    sidebarLinks.forEach(link => {
      const isActive = link.dataset.indicatorSidebarLink === id;
      link.classList.toggle("active", isActive);
      if (isActive) link.setAttribute("aria-current", "location");
      else link.removeAttribute("aria-current");
    });
  };

  const initialId = window.location.hash.replace(/^#/, "");
  setActive(targets.some(target => target.id === initialId) ? initialId : "indicator-key");

  pageLinks.forEach(link => link.addEventListener("click", () => {
    suppressObserverUntil = performance.now() + 1200;
    setActive(link.dataset.indicatorTarget);
  }));
  window.addEventListener("hashchange", () => {
    const id = window.location.hash.replace(/^#/, "");
    if (targets.some(target => target.id === id)) {
      suppressObserverUntil = performance.now() + 1200;
      setActive(id);
    }
  });

  if (!("IntersectionObserver" in window)) return;

  const observer = new IntersectionObserver(entries => {
    if (performance.now() < suppressObserverUntil) return;

    const visible = entries
      .filter(entry => entry.isIntersecting)
      .sort((left, right) => left.boundingClientRect.top - right.boundingClientRect.top)[0];
    if (visible) setActive(visible.target.id);
  }, {
    rootMargin: "-10% 0px -70% 0px",
    threshold: 0
  });

  targets.forEach(target => observer.observe(target));
}

function initializePeriodSummary() {
  const table = document.querySelector("[data-period-summary]");
  if (!table) return;

  const rows = Array.from(table.querySelectorAll("tbody [data-period-summary-row]"));
  const buttons = Array.from(table.querySelectorAll("[data-period-summary-toggle]"));
  const childrenOf = id => rows.filter(row => row.dataset.periodSummaryParentId === id);
  const setButtonState = (row, expanded) => {
    const button = row.querySelector("[data-period-summary-toggle]");
    if (!button) return;
    button.textContent = expanded ? "▼" : "▶";
    button.setAttribute("aria-expanded", String(expanded));
    button.setAttribute("aria-label", `${expanded ? "Collapse" : "Expand"} ${row.querySelector(".tf-period-summary-label > span")?.textContent.trim() || "period"}`);
  };
  const hideBranch = row => {
    row.classList.add("tf-period-summary-hidden");
    row.dataset.expanded = "false";
    setButtonState(row, false);
    childrenOf(row.dataset.periodSummaryRow).forEach(hideBranch);
  };

  buttons.forEach(button => button.addEventListener("click", () => {
    const row = button.closest("[data-period-summary-row]");
    if (!row) return;
    const expanded = row.dataset.expanded === "true";
    const nextExpanded = !expanded;
    row.dataset.expanded = String(nextExpanded);
    setButtonState(row, nextExpanded);
    childrenOf(row.dataset.periodSummaryRow).forEach(child => {
      if (nextExpanded) child.classList.remove("tf-period-summary-hidden");
      else hideBranch(child);
    });
  }));

  const taxInput = document.querySelector("#period-summary-tax-rate");
  const effectiveRate = document.querySelector("[data-period-summary-effective-rate]");
  const taxCells = rows.map(row => ({ row, cell: row.querySelector(".tf-period-summary-tax-cell") })).filter(item => item.cell);
  const longTermShare = Number(table.dataset.periodSummaryLongTermShare || ".6");
  const longTermRate = Number(table.dataset.periodSummaryLongTermRate || ".15");
  const shortTermShare = Number(table.dataset.periodSummaryShortTermShare || ".4");
  const formatter = new Intl.NumberFormat(undefined, { style: "currency", currency: table.dataset.periodSummaryCurrency || "USD" });
  const updateTaxes = () => {
    if (!taxInput) return;
    const enteredRate = Math.max(Number.parseFloat(taxInput.value) || 0, 0) / 100;
    const rate = longTermShare * longTermRate + shortTermShare * enteredRate;
    if (effectiveRate) effectiveRate.textContent = `${(rate * 100).toFixed(1)}%`;
    taxCells.forEach(({ row, cell }) => {
      const netPnl = Number.parseFloat(row.dataset.netPnl || "0") || 0;
      const estimatedTaxes = Math.max(netPnl, 0) * rate;
      cell.textContent = formatter.format(estimatedTaxes);
      cell.classList.toggle("negative", estimatedTaxes > 0);
    });
  };

  taxInput?.addEventListener("input", updateTaxes);
  taxInput?.addEventListener("change", updateTaxes);
  updateTaxes();
}

function initializePnlCalendar() {
  const root = document.querySelector("[data-pnl-calendar]");
  const dataNode = root?.querySelector("[data-pnl-calendar-data]");
  const monthly = root?.querySelector("[data-pnl-calendar-monthly]");
  const daily = root?.querySelector("[data-pnl-calendar-daily]");
  const title = root?.querySelector("[data-pnl-calendar-title]");
  const kicker = root?.querySelector("[data-pnl-calendar-kicker]");
  const back = root?.querySelector("[data-pnl-calendar-back]");
  const summary = root?.querySelector("[data-pnl-calendar-summary]");
  const days = root?.querySelector("[data-pnl-calendar-days]");

  if (!root || !dataNode || !monthly || !daily || !title || !kicker || !back || !summary || !days) return;

  let months;
  try {
    months = JSON.parse(dataNode.textContent || "[]");
  } catch {
    return;
  }

  if (!Array.isArray(months) || months.length === 0) return;

  const monthsByKey = new Map(months.map(month => [month.key, month]));
  let currencyFormat;
  try {
    currencyFormat = new Intl.NumberFormat(undefined, {
      style: "currency",
      currency: root.dataset.pnlCurrency || "USD",
      maximumFractionDigits: 2
    });
  } catch {
    currencyFormat = null;
  }

  const formatPnl = value => {
    const numericValue = Number(value || 0);
    return currencyFormat ? currencyFormat.format(numericValue) : numericValue.toFixed(2);
  };
  const element = (tagName, className, textContent) => {
    const node = document.createElement(tagName);
    if (className) node.className = className;
    if (textContent !== undefined) node.textContent = textContent;
    return node;
  };

  const renderMonthly = () => {
    monthly.hidden = false;
    daily.hidden = true;
    back.hidden = true;
    kicker.textContent = "MONTHLY";
    title.textContent = "Monthly net P&L";
    root.dataset.pnlCalendarView = "monthly";
  };

  const renderDaily = month => {
    monthly.hidden = true;
    daily.hidden = false;
    back.hidden = false;
    kicker.textContent = `DAILY · ${month.year}`;
    title.textContent = month.label;
    root.dataset.pnlCalendarView = "daily";

    summary.replaceChildren();
    const summaryValue = element("strong", "tf-pnl-calendar-summary-value", formatPnl(month.netPnl));
    summaryValue.classList.add(month.netPnl > 0 ? "positive" : month.netPnl < 0 ? "negative" : "flat");
    summary.append(summaryValue, element("span", "tf-pnl-calendar-summary-meta", `${month.tradeCount} ${month.tradeCount === 1 ? "trade" : "trades"}`));

    days.replaceChildren();
    const firstDay = new Date(month.year, month.month - 1, 1).getDay();
    const leadingDays = (firstDay + 6) % 7;
    for (let index = 0; index < leadingDays; index += 1) {
      days.append(element("span", "tf-pnl-day tf-pnl-day-leading"));
    }

    (month.days || []).forEach(day => {
      const tone = day.netPnl > 0 ? "positive" : day.netPnl < 0 ? "negative" : "flat";
      const dayNode = element("div", "tf-pnl-day", undefined);
      dayNode.dataset.tone = tone;
      dayNode.setAttribute("aria-label", day.tradeCount > 0
        ? `${day.date}: ${formatPnl(day.netPnl)}, ${day.tradeCount} ${day.tradeCount === 1 ? "trade" : "trades"}`
        : `${day.date}: no completed trades`);
      if (day.tradeCount === 0) dayNode.classList.add("tf-pnl-day-no-trades");
      dayNode.append(
        element("span", "tf-pnl-day-number", String(day.day)),
        element("strong", "tf-pnl-day-value", day.tradeCount > 0 ? formatPnl(day.netPnl) : "—"),
        element("small", "tf-pnl-day-count", day.tradeCount > 0 ? `${day.tradeCount} ${day.tradeCount === 1 ? "trade" : "trades"}` : "no trades")
      );
      days.append(dayNode);
    });
  };

  monthly.addEventListener("click", event => {
    const button = event.target.closest("[data-pnl-month]");
    if (!button || !monthly.contains(button)) return;
    const month = monthsByKey.get(button.dataset.pnlMonth);
    if (month) renderDaily(month);
  });
  back.addEventListener("click", renderMonthly);
  renderMonthly();
}

function initializePlotlyCharts() {
  const chartNodes = Array.from(document.querySelectorAll("[data-plotly-chart]"));
  if (!chartNodes.length) return;

  chartNodes.forEach(node => node.setAttribute("aria-busy", "true"));

  if (!("IntersectionObserver" in window)) {
    chartNodes.forEach(node => renderPlotlyChart(node));
    return;
  }

  const observer = new IntersectionObserver(entries => {
    entries.forEach(entry => {
      if (!entry.isIntersecting) return;

      observer.unobserve(entry.target);
      renderPlotlyChart(entry.target);
    });
  }, {
    rootMargin: "500px 0px",
    threshold: 0.01
  });

  chartNodes.forEach(node => observer.observe(node));
}

function renderPlotlyChart(node) {
  if (node.dataset.plotlyInitialized === "true") return;

  node.dataset.plotlyInitialized = "true";

  if (!window.Plotly) {
    showPlotlyError(node, new Error("Plotly.js failed to load."));
    return;
  }

  try {
    const payload = JSON.parse(node.dataset.plotlyChart || "{}");
    const payloadConfig = payload.config || {};
    const config = {
      responsive: true,
      displaylogo: false,
      ...payloadConfig,
      modeBarButtonsToRemove: Array.from(new Set([...(payloadConfig.modeBarButtonsToRemove || []), "toImage"])),
      modeBarButtonsToAdd: [...(payloadConfig.modeBarButtonsToAdd || []), createChartExportButton()]
    };

    window.Plotly.newPlot(node, payload.data || [], payload.layout || {}, config)
      .then(() => node.removeAttribute("aria-busy"))
      .catch(error => showPlotlyError(node, error));
  } catch (error) {
    showPlotlyError(node, error);
  }
}

function initializeChartExportOptions() {
  const options = document.querySelector("[data-chart-export-options]");
  if (!options) return;

  const settings = [
    [options.querySelector("[data-chart-export-owner]"), "tradefoundry.chart-export.include-owner"],
    [options.querySelector("[data-chart-export-journal]"), "tradefoundry.chart-export.include-journal"]
  ];

  settings.forEach(([checkbox, key]) => {
    if (!checkbox) return;

    try {
      checkbox.checked = window.localStorage.getItem(key) === "true";
    } catch {
      checkbox.checked = false;
    }

    checkbox.addEventListener("change", () => {
      try {
        window.localStorage.setItem(key, String(checkbox.checked));
      } catch {
        // A blocked storage area should not prevent PNG export.
      }
    });
  });
}

function createChartExportButton() {
  return {
    name: "tradefoundryDownloadPng",
    title: "Download as PNG",
    icon: window.Plotly.Icons.camera,
    click: graph => {
      downloadChartAsPng(graph).catch(error => console.error("TradeFoundry PNG export failed.", error));
    }
  };
}

async function downloadChartAsPng(graph) {
  if (graph.dataset.chartExportInProgress === "true") return;
  graph.dataset.chartExportInProgress = "true";

  const card = graph.closest(".tf-chart-card, .tf-library-chart");
  const title = card?.querySelector(".tf-card-title")?.textContent?.replace(/\s+/g, " ").trim()
    || graph.getAttribute("aria-label")
    || "TradeFoundry chart";
  const context = getChartExportContext();
  const contextLines = [];
  if (context.owner) contextLines.push(`Owner: ${context.owner}`);
  if (context.journal) contextLines.push(`Journal: ${context.journal}`);

  const originalLayout = graph.layout || {};
  const originalMargin = originalLayout.margin ? { ...originalLayout.margin } : null;
  const originalTitle = originalLayout.title ?? null;
  const originalHeight = originalLayout.height;
  const baseHeight = Number(originalHeight) || 280;
  const exportMargin = {
    ...(originalMargin || {}),
    t: Math.max(Number(originalMargin?.t) || 0, 44 + contextLines.length * 15)
  };

  try {
    await window.Plotly.relayout(graph, {
      title: {
        text: [title, ...contextLines].map(escapePlotlyText).join("<br>"),
        x: 0,
        xanchor: "left",
        y: 1,
        yanchor: "top",
        font: { family: "Segoe UI, system-ui, sans-serif", color: "#c9d1d9", size: 13 }
      },
      margin: exportMargin,
      height: baseHeight + 28 + contextLines.length * 15
    });

    await window.Plotly.downloadImage(graph, {
      format: "png",
      filename: chartExportFilename(title),
      width: Math.max(graph.clientWidth || 0, 480),
      height: baseHeight + 28 + contextLines.length * 15,
      scale: 2
    });
  } finally {
    try {
      await window.Plotly.relayout(graph, {
        title: originalTitle,
        margin: originalMargin,
        height: originalHeight
      });
    } finally {
      delete graph.dataset.chartExportInProgress;
    }
  }
}

function getChartExportContext() {
  const options = document.querySelector("[data-chart-export-options]");
  const body = document.body;
  return {
    owner: options?.querySelector("[data-chart-export-owner]")?.checked ? (body.dataset.chartExportOwnerName || "").trim() : "",
    journal: options?.querySelector("[data-chart-export-journal]")?.checked ? (body.dataset.chartExportJournalName || "").trim() : ""
  };
}

function escapePlotlyText(value) {
  return String(value)
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/\"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function chartExportFilename(title) {
  const filename = title
    .replace(/[<>:\"/\\|?*\u0000-\u001f]/g, " ")
    .replace(/\s+/g, " ")
    .trim()
    .slice(0, 120);
  return filename || "tradefoundry-chart";
}

function showPlotlyError(node, error) {
  console.error("Plotly chart failed to render.", error);
  node.removeAttribute("aria-busy");
  const fallback = document.createElement("div");
  fallback.className = "chart-empty";
  fallback.setAttribute("role", "status");
  fallback.textContent = "This chart could not be rendered.";
  node.replaceChildren(fallback);
}
