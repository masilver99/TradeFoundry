document.addEventListener("DOMContentLoaded", () => {
  const details = document.getElementById("setup-details");
  const syncScrollLock = () => document.documentElement.classList.toggle("tf-setup-editor-open", !!document.querySelector(".tf-setup-drawer[open]"));
  if (details) {
    details.querySelector("[data-setup-details-close]").addEventListener("click", () => details.close());
    details.addEventListener("close", () => {
      syncScrollLock();
      document.querySelector(`[data-setup-id="${details.dataset.selectedSetup}"]`)?.focus();
    });
    details.showModal();
    syncScrollLock();
  }
  document.querySelectorAll("[data-setup-row]").forEach(row => {
    row.addEventListener("click", event => {
      if (event.target.closest("a, button") || window.getSelection()?.toString()) return;
      row.querySelector("a").click();
    });
    row.querySelector("a").addEventListener("click", event => {
      if (details && row.querySelector("a").dataset.setupId === details.dataset.selectedSetup && !event.ctrlKey && !event.metaKey && !event.shiftKey && !event.altKey) {
        event.preventDefault();
        details.showModal();
        syncScrollLock();
      }
    });
  });
  const dialog = document.getElementById("setup-editor");
  if (!dialog) return;
  const save = document.getElementById("setup-editor-save");
  const panels = [...dialog.querySelectorAll("[data-setup-panel]")];
  const originals = new Map(panels.map(panel => [panel, panel.innerHTML]));
  let currentForm, opener, baseline, submitting = false;
  const snapshot = () => JSON.stringify([...new FormData(currentForm)].filter(([name]) => name !== "__RequestVerificationToken"));
  const dirty = () => currentForm && (dialog.dataset.openEditor || snapshot() !== baseline);

  function initializeCriteria(form) {
    const list = form.querySelector("[data-setup-criteria-list]");
    if (!list) return;
    const stages = ["Context", "Setup", "Trigger", "Management", "Exit"];
    const icons = document.querySelector("[data-setup-stage-icons]").content;
    const prefix = form.dataset.criteriaPrefix;
    const rows = [...list.querySelectorAll("[data-criterion-row]")];
    let nextIndex = rows.length;
    list.querySelector(".tf-criteria-editor-heading")?.remove();
    const nav = document.createElement("nav");
    nav.className = "tf-setup-stage-nav";
    nav.setAttribute("aria-label", "Criterion stages");
    list.before(nav);
    const groups = new Map();
    function symbol(stage) {
      const element = document.createElement("span");
      element.className = "tf-setup-stage-symbol";
      element.append(icons.querySelector('[data-stage-icon="' + stage + '"] svg').cloneNode(true));
      const count = document.createElement("span");
      count.className = "tf-setup-stage-count is-empty";
      count.textContent = "0";
      element.append(count);
      return element;
    }
    function reveal(stage, scroll = false) {
      const group = groups.get(stage);
      group.details.open = true;
      if (scroll) group.details.scrollIntoView({ block: "start", behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "instant" : "smooth" });
      nav.querySelectorAll("button").forEach(button => button.setAttribute("aria-current", button === group.link ? "true" : "false"));
    }
    stages.forEach(stage => {
      const details = document.createElement("details");
      details.className = "tf-setup-stage-group";
      details.dataset.criterionStage = stage;
      details.id = form.id + "-stage-" + stage;
      const summary = document.createElement("summary");
      const title = document.createElement("span");
      title.className = "tf-setup-stage-heading";
      title.append(symbol(stage), document.createTextNode(stage));
      const add = document.createElement("button");
      add.className = "btn btn-sm btn-outline-secondary";
      add.type = "button";
      add.textContent = "Add criterion";
      add.setAttribute("aria-label", "Add " + stage + " criterion");
      add.addEventListener("click", event => { event.preventDefault(); event.stopPropagation(); addRow(stage); });
      summary.append(title, add);
      const content = document.createElement("div");
      content.className = "tf-setup-stage-content";
      const empty = document.createElement("p");
      empty.className = "text-secondary small mb-0";
      empty.textContent = "No criteria defined for this stage.";
      content.append(empty);
      details.append(summary, content);
      list.append(details);
      const link = document.createElement("button");
      link.type = "button";
      link.className = "tf-setup-stage-link";
      link.setAttribute("aria-controls", details.id);
      link.append(symbol(stage), document.createTextNode(stage));
      link.addEventListener("click", () => { reveal(stage, true); summary.focus({ preventScroll: true }); });
      details.addEventListener("toggle", () => link.setAttribute("aria-expanded", String(details.open)));
      link.setAttribute("aria-expanded", "false");
      nav.append(link);
      groups.set(stage, { details, content, empty, link, add });
    });
    function counts() {
      groups.forEach((group, stage) => {
        const groupRows = [...group.content.querySelectorAll("[data-criterion-row]")];
        const active = groupRows.filter(row => row.querySelector('input[name$=".Name"]').value.trim() && row.querySelector('input[type=checkbox][name$=".Active"]').checked).length;
        group.empty.hidden = groupRows.length > 0;
        [group.link, group.details.querySelector(":scope > summary")].forEach(element => {
          const badge = element.querySelector(".tf-setup-stage-count");
          badge.textContent = active;
          badge.classList.toggle("is-empty", active === 0);
          badge.setAttribute("aria-label", active + " active criteria");
        });
        group.link.setAttribute("aria-label", stage + ", " + active + " active criteria");
      });
    }
    function decorate(row, expanded = false, order = nextIndex) {
      const details = document.createElement("details");
      details.dataset.criterionDetails = "";
      details.className = "tf-setup-criterion-details";
      details.open = expanded;
      row.dataset.criterionOrder = order;
      const summary = document.createElement("summary");
      details.append(summary, row);
      const name = row.querySelector('input[name$=".Name"]');
      const type = row.querySelector('select[name$=".CriterionType"]');
      const stage = row.querySelector('select[name$=".Stage"]');
      const active = row.querySelector('input[type=checkbox][name$=".Active"]');
      name.setAttribute("aria-label", "Criterion name");
      row.querySelector('input[name$=".Description"]').setAttribute("aria-label", "Criterion description");
      [type, stage].forEach((select, index) => {
        const label = document.createElement("label");
        label.className = "tf-setup-select-label";
        const copy = document.createElement("span");
        copy.textContent = index === 0 ? "Type" : "Stage";
        select.before(label);
        label.append(copy, select);
      });
      const update = () => {
        summary.replaceChildren();
        const title = document.createElement("span");
        title.className = "tf-setup-criterion-name";
        title.textContent = name.value.trim() || "New criterion";
        const tag = document.createElement("span");
        tag.className = "tf-setup-criterion-tag";
        tag.textContent = "Type: " + type.value;
        summary.append(title, tag);
        if (!active.checked) {
          const inactive = document.createElement("span");
          inactive.className = "tf-setup-criterion-tag";
          inactive.textContent = "Inactive";
          summary.append(inactive);
        }
        counts();
      };
      groups.get(stage.value).content.append(details);
      name.addEventListener("input", update);
      type.addEventListener("change", update);
      active.addEventListener("change", update);
      stage.addEventListener("change", () => {
        groups.get(stage.value).content.append(details);
        reveal(stage.value);
        stage.focus({ preventScroll: true });
        counts();
      });
      const advanced = document.createElement("details");
      advanced.className = "tf-setup-criterion-advanced";
      const label = document.createElement("summary");
      label.textContent = "Advanced";
      advanced.append(label);
      const mode = row.querySelector('select[name$=".EvaluationMode"]');
      const rule = row.querySelector('input[name$=".RuleMetadataJson"]');
      rule.setAttribute("aria-label", "Optional rule JSON");
      advanced.open = !!dialog.dataset.openEditor && (rule.value.length > 0 || mode.value !== "Manual");
      advanced.append(mode.closest(".tf-criterion-help-field"), rule.closest(".tf-criterion-help-field"));
      row.append(advanced);
      update();
      if (expanded) reveal(stage.value);
    }
    rows.forEach((row, index) => decorate(row, !!dialog.dataset.openEditor, index));
    const firstStage = stages.find(stage => groups.get(stage).content.querySelector("[data-criterion-row]"));
    if (firstStage) reveal(firstStage);
    counts();
    function addRow(stage) {
      const holder = document.createElement("div");
      holder.innerHTML = form.querySelector("[data-setup-criterion-template]").innerHTML.replaceAll("__INDEX__", String(nextIndex));
      const row = holder.firstElementChild;
      row.querySelector('select[name$=".Stage"]').value = stage;
      decorate(row, true, nextIndex++);
      row.querySelector("input").focus();
    }
    list.addEventListener("click", event => {
      const remove = event.target.closest("[data-criterion-remove]");
      if (!remove) return;
      const details = remove.closest("[data-criterion-details]");
      const group = groups.get(details.querySelector('select[name$=".Stage"]').value);
      details.remove();
      counts();
      group.add.focus();
    });
    form.addEventListener("submit", () => {
      // Grouping changes presentation; keep the definition's original display order.
      [...list.querySelectorAll("[data-criterion-row]")].sort((a, b) => Number(a.dataset.criterionOrder) - Number(b.dataset.criterionOrder)).forEach((row, index) => {
        row.querySelectorAll("[name]").forEach(input => {
          input.name = input.name.replace(new RegExp(`^${prefix}\\[[^\\]]+\\]`), `${prefix}[${index}]`);
        });
      });
    });
    form.addEventListener("invalid", event => {
      const details = event.target.closest("[data-criterion-details]");
      if (details) { details.open = true; reveal(event.target.closest("[data-criterion-row]").querySelector('select[name$=".Stage"]').value); }
    }, true);
  }
  function panelMode(form) { return form.closest("[data-setup-panel]").dataset.setupPanel; }
  function open(mode, trigger) {
    const panel = panels.find(item => item.dataset.setupPanel === mode);
    if (!panel) return;
    opener = trigger || document.querySelector(`[data-setup-open="${mode}"]`);
    panels.forEach(item => { item.hidden = item !== panel; });
    currentForm = panel.querySelector("form");
    initializeCriteria(currentForm);
    currentForm.addEventListener("submit", () => { submitting = true; });
    const title = mode === "create" ? "New setup" : mode === "metadata" ? "Edit setup details" : "Edit setup criteria";
    document.getElementById("setup-editor-title").textContent = title;
    save.setAttribute("form", currentForm.id);
    save.textContent = mode === "create" ? "Create setup" : mode === "metadata" ? "Save details" : currentForm.action.includes("CreateVersion") ? "Create new version" : "Save criteria";
    baseline = snapshot();
    submitting = false;
    dialog.showModal();
    document.documentElement.classList.add("tf-setup-editor-open");
    requestAnimationFrame(() => {
      if (!dialog.open || !currentForm) return;
      (dialog.querySelector("[data-setup-error]") || currentForm.querySelector("input:not([type=hidden]), textarea, select"))?.focus();
    });
  }
  function close() {
    if (dirty() && !window.confirm("Discard your unsaved setup changes?")) return;
    if (dialog.dataset.openEditor) {
      submitting = true;
      const url = new URL(location.href);
      url.searchParams.delete("handler");
      location.href = url.href;
      return;
    }
    dialog.close();
  }
  document.querySelectorAll("[data-setup-open]").forEach(button => button.addEventListener("click", () => open(button.dataset.setupOpen, button)));
  dialog.querySelectorAll("[data-setup-close]").forEach(button => button.addEventListener("click", close));
  dialog.addEventListener("cancel", event => { event.preventDefault(); close(); });
  dialog.addEventListener("close", () => {
    syncScrollLock();
    panels.forEach(panel => { panel.innerHTML = originals.get(panel); });
    dialog.querySelector("[data-setup-error]")?.remove();
    delete dialog.dataset.openEditor;
    currentForm = null;
    opener?.focus();
  });
  window.addEventListener("beforeunload", event => {
    if (!submitting && dirty()) { event.preventDefault(); event.returnValue = ""; }
  });
  if (dialog.dataset.openEditor) open(dialog.dataset.openEditor);
});
