(() => {
  // Journal image viewer: click or press Enter on an image to open it in a zoomable popup.
  const images = [...document.querySelectorAll(".journal-content img")];
  if (!images.length || typeof HTMLDialogElement !== "function") return;

  const dialog = document.createElement("dialog");
  dialog.className = "image-viewer";
  dialog.setAttribute("aria-label", "Image viewer");
  dialog.innerHTML = `<div class="image-viewer-toolbar">
      <button type="button" class="chart-button" data-zoom="-1" aria-label="Zoom out">−</button>
      <button type="button" class="chart-button" data-zoom-reset aria-label="Reset zoom"><span data-zoom-level>100%</span></button>
      <button type="button" class="chart-button" data-zoom="1" aria-label="Zoom in">+</button>
      <button type="button" class="chart-button" data-viewer-close aria-label="Close image">Close</button>
    </div>
    <div class="image-viewer-stage"><img alt="" draggable="false"></div>
    <p class="image-viewer-caption muted" hidden></p>`;
  document.body.append(dialog);
  const stage = dialog.querySelector(".image-viewer-stage");
  const view = stage.querySelector("img");
  const level = dialog.querySelector("[data-zoom-level]");
  const caption = dialog.querySelector(".image-viewer-caption");
  const minScale = 1, maxScale = 8;
  let scale = 1, x = 0, y = 0, opener = null;

  const clamp = () => {
    // Keep the zoomed image covering the stage instead of drifting out of view.
    const maxX = Math.max(0, (view.offsetWidth * scale - stage.clientWidth) / 2);
    const maxY = Math.max(0, (view.offsetHeight * scale - stage.clientHeight) / 2);
    x = Math.max(-maxX, Math.min(maxX, x));
    y = Math.max(-maxY, Math.min(maxY, y));
  };
  const apply = () => {
    clamp();
    view.style.transform = `translate(${x}px, ${y}px) scale(${scale})`;
    level.textContent = `${Math.round(scale * 100)}%`;
    stage.classList.toggle("zoomed", scale > 1);
  };
  // Zoom toward a point in client coordinates (defaults to the stage centre).
  const zoomTo = (next, clientX, clientY) => {
    next = Math.max(minScale, Math.min(maxScale, next));
    const box = stage.getBoundingClientRect();
    const px = (clientX ?? box.left + box.width / 2) - (box.left + box.width / 2);
    const py = (clientY ?? box.top + box.height / 2) - (box.top + box.height / 2);
    x = px - (px - x) * next / scale;
    y = py - (py - y) * next / scale;
    scale = next;
    if (scale === 1) x = y = 0;
    apply();
  };
  const reset = () => { scale = 1; x = y = 0; apply(); };

  const open = image => {
    opener = image;
    view.src = image.currentSrc || image.src;
    view.alt = image.alt;
    caption.textContent = image.alt;
    caption.hidden = !image.alt;
    reset();
    dialog.showModal();
  };
  for (const image of images) {
    image.classList.add("zoomable");
    image.tabIndex = 0;
    image.setAttribute("role", "button");
    image.setAttribute("aria-label", image.alt ? `Enlarge image: ${image.alt}` : "Enlarge image");
    image.addEventListener("click", () => open(image));
    image.addEventListener("keydown", event => {
      if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(image); }
    });
  }

  dialog.addEventListener("close", () => { view.removeAttribute("src"); opener?.focus(); opener = null; });
  dialog.querySelector("[data-viewer-close]").addEventListener("click", () => dialog.close());
  dialog.querySelector("[data-zoom-reset]").addEventListener("click", reset);
  for (const button of dialog.querySelectorAll("[data-zoom]"))
    button.addEventListener("click", () => zoomTo(scale * (button.dataset.zoom > 0 ? 1.5 : 1 / 1.5)));
  dialog.addEventListener("keydown", event => {
    if (event.key === "+" || event.key === "=") zoomTo(scale * 1.5);
    else if (event.key === "-") zoomTo(scale / 1.5);
    else if (event.key === "0") reset();
    else return;
    event.preventDefault();
  });
  // Clicking the dimmed area around the image closes the viewer.
  dialog.addEventListener("click", event => { if (event.target === dialog || (event.target === stage && scale === 1)) dialog.close(); });
  stage.addEventListener("wheel", event => {
    event.preventDefault();
    zoomTo(scale * Math.exp(-event.deltaY * (event.deltaMode === 1 ? 0.05 : 0.002)), event.clientX, event.clientY);
  }, { passive: false });
  view.addEventListener("dblclick", event => zoomTo(scale > 1 ? 1 : 2.5, event.clientX, event.clientY));

  // Drag to pan and two-finger pinch to zoom.
  const pointers = new Map();
  let pinch = null;
  stage.addEventListener("pointerdown", event => {
    if (event.target !== view) return;
    stage.setPointerCapture(event.pointerId);
    pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
    if (pointers.size === 2) {
      const [a, b] = [...pointers.values()];
      pinch = { distance: Math.hypot(a.x - b.x, a.y - b.y) || 1, scale };
    }
  });
  stage.addEventListener("pointermove", event => {
    const last = pointers.get(event.pointerId);
    if (!last) return;
    const current = { x: event.clientX, y: event.clientY };
    pointers.set(event.pointerId, current);
    if (pointers.size === 2 && pinch) {
      const [a, b] = [...pointers.values()];
      zoomTo(pinch.scale * Math.hypot(a.x - b.x, a.y - b.y) / pinch.distance, (a.x + b.x) / 2, (a.y + b.y) / 2);
    } else if (pointers.size === 1 && scale > 1) {
      x += current.x - last.x;
      y += current.y - last.y;
      apply();
    }
  });
  const release = event => {
    pointers.delete(event.pointerId);
    if (pointers.size < 2) pinch = null;
  };
  stage.addEventListener("pointerup", release);
  stage.addEventListener("pointercancel", release);
  addEventListener("resize", () => { if (dialog.open) apply(); });
})();

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
