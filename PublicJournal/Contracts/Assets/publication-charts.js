(() => {
  const dialog = document.querySelector("#trade-chart-dialog");
  if (!dialog) return;
  const host = dialog.querySelector("[data-public-chart-host]");
  let chart = null;
  let barCount = 0;
  let opener = null;
  const focus = () => {
    chart?.timeScale().setVisibleLogicalRange({ from: -0.5, to: barCount - 0.5 });
    chart?.priceScale("right").applyOptions({ autoScale: true });
  };
  const dispose = () => { chart?.remove(); chart = null; host.replaceChildren(); };
  dialog.querySelector("[data-chart-close]").addEventListener("click", () => dialog.close());
  dialog.querySelector("[data-chart-focus]").addEventListener("click", focus);
  dialog.addEventListener("close", () => { dispose(); opener?.focus(); });
  dialog.addEventListener("click", event => {
    if (event.target !== dialog) return;
    const rect = dialog.getBoundingClientRect();
    if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close();
  });
  document.querySelectorAll("[data-public-chart]").forEach(button => button.addEventListener("click", () => {
    dispose();
    opener = button;
    const payload = JSON.parse(button.dataset.publicChart);
    const title = button.dataset.chartTitle;
    const timeZone = button.dataset.chartTimezone;
    dialog.querySelector("#trade-chart-title").textContent = title;
    dialog.querySelector("[data-chart-description]").textContent = `5 minute candles · ${timeZone} · Published snapshot`;
    dialog.querySelector("[data-chart-prices]").textContent = `Entry ${payload.entry.price.toLocaleString(undefined, { maximumFractionDigits: 8 })} → Exit ${payload.exit.price.toLocaleString(undefined, { maximumFractionDigits: 8 })}`;
    host.setAttribute("aria-label", `${title}, 5 minute candlestick chart with entry and exit prices`);
    dialog.showModal();
    try {
      const library = window.LightweightCharts;
      const formatTime = value => new Intl.DateTimeFormat("en-US", { timeZone, month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false }).format(new Date(Number(value) * 1000));
      chart = library.createChart(host, {
        autoSize: true,
        layout: { background: { type: "solid", color: "#0d1117" }, textColor: "#8b949e" },
        grid: { vertLines: { color: "#21262d" }, horzLines: { color: "#21262d" } },
        rightPriceScale: { borderColor: "#30363d", scaleMargins: { top: 0.08, bottom: 0.08 } },
        timeScale: { timeVisible: true, lockVisibleTimeRangeOnResize: true, tickMarkFormatter: formatTime },
        localization: { timeFormatter: formatTime },
        handleScroll: { mouseWheel: false, pressedMouseMove: true, horzTouchDrag: true, vertTouchDrag: false }
      });
      const candles = chart.addSeries(library.CandlestickSeries, {
        upColor: "#3fb950", downColor: "#f85149", borderVisible: false,
        wickUpColor: "#3fb950", wickDownColor: "#f85149", lastValueVisible: false, priceLineVisible: false,
        priceFormat: { type: "price", precision: 8, minMove: 0.00000001 }
      });
      // Use the published price precision, without rounding fractional executions.
      const precision = Math.min(8, Math.max(2, ...payload.bars.flatMap(bar => [bar.open, bar.high, bar.low, bar.close]).concat([payload.entry.price, payload.exit.price]).map(price => (String(price).split(".")[1] || "").length)));
      candles.applyOptions({ priceFormat: { type: "price", precision, minMove: 10 ** -precision } });
      candles.setData(payload.bars);
      const volume = chart.addSeries(library.HistogramSeries, { priceFormat: { type: "volume" }, lastValueVisible: false, priceLineVisible: false }, 1);
      volume.setData(payload.bars.map(bar => ({ time: bar.time, value: bar.volume, color: bar.close >= bar.open ? "#3fb95088" : "#f8514988" })));
      chart.panes()[1].setHeight(80);
      const drawing = new TradeExecutionDrawing();
      candles.attachPrimitive(drawing);
      drawing.setTrade({
        entry: { ...payload.entry, color: button.dataset.chartDirection.toLowerCase() === "short" ? "#f85149" : "#3fb950" },
        exit: { ...payload.exit, color: Number(button.dataset.chartPnl) > 0 ? "#3fb950" : Number(button.dataset.chartPnl) < 0 ? "#f85149" : "#d29922" }
      }, payload.bars);
      barCount = payload.bars.length;
      focus();
    } catch (error) {
      dispose();
      host.textContent = "This chart could not be loaded. Close it and try again.";
      console.error("Public trade chart failed to render.", error);
    }
  }));
})();
