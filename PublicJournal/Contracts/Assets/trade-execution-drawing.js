// A series primitive keeps executions anchored to exact prices during pan, zoom,
// resize and PNG export, including entry and exit inside the same candle.
class TradeExecutionDrawing {
  constructor() {
    this.events = [];
    this.views = [{ zOrder: () => "top", renderer: () => ({ draw: target => this.draw(target) }) }];
  }

  attached({ chart, series, requestUpdate }) {
    this.chart = chart;
    this.series = series;
    this.requestUpdate = requestUpdate;
  }

  detached() {
    this.chart = null;
    this.series = null;
    this.requestUpdate = null;
  }

  paneViews() { return this.views; }

  setTrade(trade, bars) {
    const indices = new Map(bars.map((bar, index) => [bar.time, index]));
    this.events = [trade?.entry, trade?.exit].map((event, index) => event && ({
      time: Number(event.time),
      price: Number(event.price),
      color: event.color,
      side: index === 0 ? -1 : 1,
      logical: indices.get(Number(event.time))
    })).filter(event => event && Number.isFinite(event.price) && event.logical !== undefined);
    this.requestUpdate?.();
  }

  autoscaleInfo(from, to) {
    const prices = this.events.filter(event => event.logical >= from && event.logical <= to).map(event => event.price);
    return prices.length ? { priceRange: { minValue: Math.min(...prices), maxValue: Math.max(...prices) } } : null;
  }

  draw(target) {
    if (!this.chart || !this.series) return;
    const timeScale = this.chart.timeScale();
    // Candle bodies occupy approximately 80% of their bar spacing. Place the
    // triangle tips just outside the body, leaving the actual candle readable.
    const offset = Math.max(2, timeScale.options().barSpacing * 0.4 + 1);
    const points = this.events.map(event => {
      const x = timeScale.timeToCoordinate(event.time);
      const y = this.series.priceToCoordinate(event.price);
      return x === null || y === null ? null : { ...event, x: x + event.side * offset, y };
    });
    target.useMediaCoordinateSpace(({ context }) => {
      context.save();
      if (points.length === 2 && points.every(Boolean)) {
        context.beginPath();
        context.moveTo(points[0].x, points[0].y);
        context.lineTo(points[1].x, points[1].y);
        context.strokeStyle = "#0d1117";
        context.lineWidth = 4;
        context.stroke();
        context.strokeStyle = "#ffffff";
        context.lineWidth = 1.5;
        context.stroke();
      }
      points.filter(Boolean).forEach(point => {
        const baseX = point.x + point.side * 8;
        context.beginPath();
        context.moveTo(point.x, point.y);
        context.lineTo(baseX, point.y - 5);
        context.lineTo(baseX, point.y + 5);
        context.closePath();
        context.fillStyle = point.color;
        context.fill();
        context.strokeStyle = "#0d1117";
        context.lineWidth = 1;
        context.stroke();
      });
      context.restore();
    });
  }
}

