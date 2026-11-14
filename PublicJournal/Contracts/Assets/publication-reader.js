(() => {
  // Highlight the navigation link for the section currently under the sticky bar.
  const links = [...document.querySelectorAll(".site-nav [data-section]")];
  const sections = links.map(link => document.getElementById(link.dataset.section)).filter(Boolean);
  if (sections.length) {
    let queued = false;
    const update = () => {
      queued = false;
      const offset = (document.querySelector(".site-nav")?.offsetHeight ?? 0) + 24;
      let current = sections[0];
      for (const section of sections) if (section.getBoundingClientRect().top <= offset) current = section;
      links.forEach(link => link.toggleAttribute("aria-current", link.dataset.section === current.id));
    };
    addEventListener("scroll", () => { if (!queued) { queued = true; requestAnimationFrame(update); } }, { passive: true });
    update();
  }

  // Equity and drawdown hover readout. Points: [x, equityY, drawdownY, date, cumulative, drawdown] in SVG units.
  const equity = document.querySelector("[data-equity-points]");
  if (!equity) return;
  const points = JSON.parse(equity.dataset.equityPoints);
  const readout = document.querySelector("[data-equity-readout]");
  const charts = [equity, document.querySelector("[data-drawdown-chart]")].filter(Boolean);
  const initial = readout?.textContent ?? "";
  let index = points.length - 1;
  const show = next => {
    index = Math.max(0, Math.min(points.length - 1, next));
    const [x, y, drawdownY, date, value, drawdown] = points[index];
    for (const svg of charts) {
      const line = svg.querySelector(".hover-line");
      const dot = svg.querySelector(".hover-dot");
      line.setAttribute("x1", x);
      line.setAttribute("x2", x);
      dot.setAttribute("cx", x);
      dot.setAttribute("cy", svg === equity ? y : drawdownY);
      svg.classList.add("hovering");
    }
    if (readout) readout.textContent = `${date} · Cumulative ${value} · Drawdown ${drawdown}`;
  };
  const hide = () => {
    charts.forEach(svg => svg.classList.remove("hovering"));
    if (readout) readout.textContent = initial;
  };
  const nearest = (svg, clientX) => {
    const box = svg.getBoundingClientRect();
    const x = (clientX - box.left) / box.width * 1000;
    let best = 0;
    for (let i = 1; i < points.length; i++) if (Math.abs(points[i][0] - x) < Math.abs(points[best][0] - x)) best = i;
    return best;
  };
  for (const svg of charts) {
    svg.addEventListener("pointermove", event => show(nearest(svg, event.clientX)));
    svg.addEventListener("pointerleave", () => { if (document.activeElement !== equity) hide(); });
  }
  equity.addEventListener("focus", () => show(index));
  equity.addEventListener("blur", hide);
  equity.addEventListener("keydown", event => {
    const step = { ArrowLeft: -1, ArrowRight: 1, PageDown: -10, PageUp: 10 }[event.key];
    if (step !== undefined) show(index + step);
    else if (event.key === "Home") show(0);
    else if (event.key === "End") show(points.length - 1);
    else return;
    event.preventDefault();
  });
})();
