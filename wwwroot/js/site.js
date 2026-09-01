document.addEventListener("DOMContentLoaded", () => {
  const fileInput = document.querySelector("#import-file");
  const dropZone = fileInput?.closest(".drop-zone");
  if (fileInput && dropZone) {
    fileInput.addEventListener("change", () => {
      const file = fileInput.files?.[0];
      if (!file) return;
      dropZone.querySelector("strong").textContent = file.name;
      const preview = document.querySelector("#import-preview");
      if (!preview) return;
      const reader = new FileReader();
      reader.onload = () => {
        const lines = String(reader.result || "").replace(/^\uFEFF/, "").split(/\r?\n/).filter(Boolean).slice(0, 4);
        const header = (lines[0] || "").toLowerCase();
        const detected = header.includes("activitytype") ? "Sierra Chart fills" : header.includes("open") && header.includes("high") && header.includes("low") && header.includes("close") ? "OHLCV bars" : header.includes("trade #") || header.includes("trade number") ? "TradingView Strategy Tester" : "TradingView account history";
        preview.textContent = `Preview · ${detected}\n${lines.join("\n")}\n\nReview the journal context, grouping, and bar interval, then submit to commit.`;
        preview.hidden = false;
      };
      reader.readAsText(file.slice(0, 12000));
    });
    ["dragenter", "dragover"].forEach(eventName => dropZone.addEventListener(eventName, event => {
      event.preventDefault();
      dropZone.style.borderColor = "#e6b566";
    }));
    ["dragleave", "drop"].forEach(eventName => dropZone.addEventListener(eventName, event => {
      event.preventDefault();
      dropZone.style.borderColor = "";
    }));
    dropZone.addEventListener("drop", event => {
      const files = event.dataTransfer?.files;
      if (!files?.length) return;
      fileInput.files = files;
      fileInput.dispatchEvent(new Event("change", { bubbles: true }));
    });
  }

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
