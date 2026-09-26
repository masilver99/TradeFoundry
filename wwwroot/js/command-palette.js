document.addEventListener("DOMContentLoaded", initializeCommandPalette);

function initializeCommandPalette() {
  const dialog = document.querySelector("[data-command-dialog]");
  const trigger = document.querySelector("[data-command-trigger]");
  const input = document.querySelector("[data-command-input]");
  const resultsHost = document.querySelector("[data-command-results]");
  const status = document.querySelector("[data-command-status]");
  const catalogNode = document.querySelector("#tf-command-catalog");
  const journalId = document.body.dataset.commandJournalId || "";
  const searchUrl = document.body.dataset.commandSearchUrl || "";
  if (!dialog || !trigger || !input || !resultsHost || !catalogNode || !journalId) return;

  let catalog = [];
  try {
    catalog = JSON.parse(catalogNode.textContent || "[]").filter(item => item && item.title && item.route);
  } catch (error) {
    console.error("The command palette catalog could not be read.", error);
  }

  assignCommandTargetIds();
  openHashTarget();

  let renderedResults = [];
  let selectedIndex = 0;
  let previousFocus = null;
  let queryTimer = null;
  let requestController = null;
  let requestRevision = 0;
  let restoreFocusOnClose = true;
  const defaultTitles = new Set(["Overview", "Trade Ledger", "Daybook Review", "Imports", "Settings"]);
  const shortcutLabel = trigger.querySelector("[data-command-shortcut]");
  if (shortcutLabel && /mac|iphone|ipad/i.test(navigator.platform || navigator.userAgent)) {
    shortcutLabel.textContent = "⌘ K";
    trigger.title = "Search TradeFoundry (⌘K)";
  }

  const basePath = `/journal/${encodeURIComponent(journalId)}`;
  const localHref = item => {
    const url = new URL(`${basePath}${item.route}`, window.location.origin);
    if (item.targetId) url.hash = item.targetId;
    return url.href;
  };

  const safeHref = value => {
    try {
      const url = new URL(value, window.location.href);
      if (url.origin !== window.location.origin || !url.pathname.startsWith(`${basePath}/`)) return null;
      return url.href;
    } catch {
      return null;
    }
  };

  const searchableText = item => [item.title, item.category, ...(item.aliases || []), ...(item.keywords || [])]
    .join(" ")
    .toLocaleLowerCase();

  const scoreResult = (item, query) => {
    const title = String(item.title || "").toLocaleLowerCase();
    const text = searchableText(item);
    const terms = query.toLocaleLowerCase().trim().split(/\s+/).filter(Boolean);
    let score = 0;
    for (const term of terms) {
      if (!text.includes(term)) return null;
      score += title === term ? 100 : title.startsWith(term) ? 45 : title.includes(term) ? 25 : 8;
    }
    return score;
  };

  const localResults = query => {
    if (!query.trim()) {
      return catalog
        .filter(item => item.category === "Navigation" && defaultTitles.has(item.title))
        .map(item => ({...item, href: localHref(item)}));
    }

    return catalog
      .map(item => ({item, score: scoreResult(item, query)}))
      .filter(result => result.score !== null)
      .sort((left, right) => right.score - left.score || left.item.title.localeCompare(right.item.title))
      .slice(0, 10)
      .map(result => ({...result.item, href: localHref(result.item)}));
  };

  const ledgerResult = query => ({
    type: "ledger-search",
    title: `Search Trade Ledger for “${query.trim()}”`,
    category: "Trade Ledger",
    subtitle: "Search symbols, instruments, accounts, and trade side",
    href: `${basePath}/trades?search=${encodeURIComponent(query.trim())}`
  });

  const setActive = index => {
    if (renderedResults.length === 0) {
      selectedIndex = 0;
      input.removeAttribute("aria-activedescendant");
      return;
    }
    selectedIndex = (index + renderedResults.length) % renderedResults.length;
    resultsHost.querySelectorAll("[role=option]").forEach((option, optionIndex) => {
      const active = optionIndex === selectedIndex;
      option.setAttribute("aria-selected", String(active));
      option.classList.toggle("is-active", active);
      if (active) option.scrollIntoView({block: "nearest"});
    });
    input.setAttribute("aria-activedescendant", `tf-command-result-${selectedIndex}`);
  };

  const render = (local, remote = []) => {
    const query = input.value.trim();
    const results = [...local];
    if (query.length > 0) results.push(ledgerResult(query));
    results.push(...remote);
    renderedResults = results.filter(result => safeHref(result.href));
    selectedIndex = Math.min(selectedIndex, Math.max(0, renderedResults.length - 1));
    resultsHost.replaceChildren();

    const groups = new Map();
    renderedResults.forEach(result => {
      const category = result.category || "Results";
      if (!groups.has(category)) groups.set(category, []);
      groups.get(category).push(result);
    });

    let index = 0;
    for (const [category, group] of groups) {
      const section = document.createElement("section");
      section.className = "tf-command-group";
      section.setAttribute("role", "group");
      section.setAttribute("aria-label", category);
      const heading = document.createElement("h3");
      heading.className = "tf-command-group-title";
      heading.textContent = category;
      section.append(heading);

      for (const result of group) {
        const option = document.createElement("button");
        option.type = "button";
        option.id = `tf-command-result-${index}`;
        option.className = "tf-command-option";
        option.setAttribute("role", "option");
        option.setAttribute("aria-selected", String(index === selectedIndex));
        option.dataset.commandIndex = String(index);

        const main = document.createElement("span");
        main.className = "tf-command-option-main";
        const title = document.createElement("strong");
        title.textContent = result.title;
        main.append(title);
        if (result.subtitle) {
          const subtitle = document.createElement("span");
          subtitle.className = "tf-command-option-subtitle";
          subtitle.textContent = result.subtitle;
          main.append(subtitle);
        }
        option.append(main);
        if (result.snippet) {
          const snippet = document.createElement("span");
          snippet.className = "tf-command-option-snippet";
          snippet.textContent = result.snippet;
          option.append(snippet);
        }
        section.append(option);
        index++;
      }
      resultsHost.append(section);
    }

    if (renderedResults.length === 0) {
      const empty = document.createElement("p");
      empty.className = "tf-command-empty";
      empty.textContent = query ? "No matching pages, charts, metrics, or notes." : "No commands available.";
      resultsHost.append(empty);
      input.removeAttribute("aria-activedescendant");
    } else {
      setActive(selectedIndex);
    }
  };

  const searchNotes = query => {
    clearTimeout(queryTimer);
    requestController?.abort();
    requestController = null;
    const revision = ++requestRevision;
    const local = localResults(query);
    render(local);
    if (query.trim().length < 2 || !searchUrl) {
      status.textContent = "";
      return;
    }

    status.textContent = "Searching journal notes…";
    queryTimer = window.setTimeout(async () => {
      const controller = new AbortController();
      requestController = controller;
      try {
        const url = new URL(searchUrl, window.location.origin);
        url.searchParams.set("q", query.trim());
        const response = await fetch(url, {
          credentials: "same-origin",
          headers: {"Accept": "application/json"},
          signal: controller.signal
        });
        if (!response.ok || response.redirected) throw new Error("Journal note search is unavailable.");
        const payload = await response.json();
        if (revision !== requestRevision || !dialog.open) return;
        const remote = Array.isArray(payload.results)
          ? payload.results.map(result => ({...result, type: "navigate"}))
          : [];
        render(local, remote);
        status.textContent = remote.length ? `${remote.length} journal note result${remote.length === 1 ? "" : "s"}.` : "No matching journal notes.";
      } catch (error) {
        if (error.name === "AbortError" || revision !== requestRevision) return;
        console.error("Journal note search failed.", error);
        status.textContent = "Journal note search is unavailable. Page and trade search still work.";
      }
    }, 240);
  };

  const closeDialog = restoreFocus => {
    restoreFocusOnClose = restoreFocus;
    requestController?.abort();
    clearTimeout(queryTimer);
    if (dialog.open) dialog.close();
  };

  const flushReviewNavigation = async () => {
    const flush = window.tradeFoundryFlushPaletteNavigation || window.tradeFoundryFlushDailyJournal;
    return flush ? await flush() : true;
  };

  const openNavigationResult = async result => {
    const href = safeHref(result.href);
    if (!href) return;
    if (!await flushReviewNavigation()) {
      status.textContent = "Save conflict or failure. Resolve it before navigating away.";
      return;
    }

    const target = new URL(href);
    const isSameLocation = target.pathname === window.location.pathname
      && target.search === window.location.search;
    if (isSameLocation) {
      closeDialog(false);
      window.history.replaceState(null, "", target.href);
      window.requestAnimationFrame(() => focusTarget(target.hash));
      return;
    }
    closeDialog(false);
    window.location.assign(target.href);
  };

  const commandActions = new Map();
  const registerCommandAction = (name, action) => commandActions.set(name, action);
  registerCommandAction("navigate", openNavigationResult);
  registerCommandAction("ledger-search", openNavigationResult);

  const openResult = async result => {
    const action = commandActions.get(result.type);
    if (!action) {
      status.textContent = "This command is not available yet.";
      return;
    }
    await action(result);
  };

  const openDialog = () => {
    if (dialog.open) return;
    previousFocus = document.activeElement instanceof HTMLElement && document.activeElement !== document.body
      ? document.activeElement
      : trigger;
    restoreFocusOnClose = true;
    trigger.setAttribute("aria-expanded", "true");
    input.value = "";
    status.textContent = "";
    selectedIndex = 0;
    searchNotes("");
    dialog.showModal();
    input.focus();
  };

  trigger.addEventListener("click", openDialog);
  document.addEventListener("keydown", event => {
    if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "k") {
      event.preventDefault();
      if (dialog.open) input.focus();
      else openDialog();
      return;
    }
    if (!dialog.open) return;
    if (event.key === "ArrowDown") {
      event.preventDefault();
      setActive(selectedIndex + 1);
    } else if (event.key === "ArrowUp") {
      event.preventDefault();
      setActive(selectedIndex - 1);
    } else if (event.key === "Enter" && renderedResults[selectedIndex]) {
      event.preventDefault();
      void openResult(renderedResults[selectedIndex]);
    }
  });
  input.addEventListener("input", () => {
    selectedIndex = 0;
    searchNotes(input.value);
  });
  resultsHost.addEventListener("click", event => {
    const option = event.target.closest("[data-command-index]");
    if (!option) return;
    void openResult(renderedResults[Number(option.dataset.commandIndex)]);
  });
  dialog.querySelector("[data-command-close]")?.addEventListener("click", () => closeDialog(true));
  dialog.addEventListener("click", event => {
    if (event.target === dialog) closeDialog(true);
  });
  dialog.addEventListener("cancel", () => {
    restoreFocusOnClose = true;
  });
  dialog.addEventListener("close", () => {
    trigger.setAttribute("aria-expanded", "false");
    if (restoreFocusOnClose && previousFocus instanceof HTMLElement && previousFocus.isConnected)
      previousFocus.focus({preventScroll: true});
  });

  function focusTarget(hash) {
    if (!hash) return;
    const id = decodeURIComponent(hash.slice(1));
    const target = document.getElementById(id);
    if (!target) return;
    target.scrollIntoView({behavior: window.matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth", block: "start"});
    target.classList.remove("tf-command-target-highlight");
    void target.offsetWidth;
    target.classList.add("tf-command-target-highlight");
    window.setTimeout(() => target.classList.remove("tf-command-target-highlight"), 1700);
  }

  function openHashTarget() {
    if (!window.location.hash) return;
    window.requestAnimationFrame(() => window.setTimeout(() => focusTarget(window.location.hash), 70));
  }
}

function assignCommandTargetIds() {
  const selectors = [
    ".tf-library-chart",
    ".tf-chart-card",
    ".tf-period-breakdown-card",
    ".tf-metric",
    ".tf-indicator-card",
    ".tf-risk-discipline-score-card",
    ".tf-edge-persistence-score-card"
  ].join(",");
  const counts = new Map();
  document.querySelectorAll(selectors).forEach(card => {
    const title = card.querySelector(".tf-card-title, .tf-indicator-label, .tf-metric-label")?.textContent?.trim();
    const section = card.closest("[id]")?.id || "overview";
    if (!title) return;
    const base = `tf-command-${commandSlug(section)}-${commandSlug(title)}`;
    const count = (counts.get(base) || 0) + 1;
    counts.set(base, count);
    card.id = count === 1 ? base : `${base}-${count}`;
    card.dataset.commandTarget = "true";
  });
}

function commandSlug(value) {
  return String(value || "")
    .normalize("NFD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, "-")
    .replace(/^-+|-+$/g, "");
}
