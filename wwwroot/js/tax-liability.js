(() => {
  const root = document.querySelector("[data-tax-liability]");
  if (!root) return;
  const key = `tf-history-tax-rate:${root.dataset.journalId}`;
  function update() {
    let entered = 24;
    try {
      const saved = localStorage.getItem(key);
      if (saved !== null && Number.isFinite(Number(saved)) && Number(saved) >= 0) entered = Number(saved);
    } catch {}
    const rate = .6 * .15 + .4 * entered / 100;
    root.querySelector("[data-tax-effective-rate]").textContent = `${(rate * 100).toFixed(1)}%`;
    const format = value => value.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    root.querySelectorAll("[data-tax-base]").forEach(cell => cell.textContent = format(Math.max(0, Number(cell.dataset.taxBase)) * rate));
    root.querySelectorAll("[data-tax-projection]").forEach(cell => cell.textContent = format(Number(root.dataset.projectedNet) * rate / Number(cell.dataset.taxProjection)));
  }
  window.addEventListener("storage", update);
  window.addEventListener("pageshow", update);
  update();
})();
