// Tag picker and the "create a tag" popup. Picking only ever selects an existing tag: a new one can
// only come from the popup, after the trader confirms the name and describes it.
(() => {
  "use strict";

  const JOINING_DASH = /(?<=[\p{L}\p{N}])(?:[-–—]|\s+[-–—]+\s+)(?=[\p{L}\p{N}])/gu;
  const JOINING_PUNCTUATION = /(?<=\p{L})[./](?=[\p{L}\p{N}])|(?<=\p{N})[./](?=\p{L})/gu;

  // Mirrors TagNames.Key on the server, which has the final say.
  const keyOf = value => String(value || "").normalize("NFKC").toLowerCase()
    .replace(/['‘’`"“”]/g, "")
    .replace(JOINING_DASH, " ")
    .replace(JOINING_PUNCTUATION, " ")
    .replace(/[\s_]+/g, " ")
    .trim()
    .replace(/ (?!\p{Nd})|(?<!\p{Nd}) /gu, "");

  const make = (tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text !== undefined) node.textContent = text;
    return node;
  };

  const plural = (count, word) => `${count} ${word}${count === 1 ? "" : "s"}`;
  const usage = tag => [tag.trades && plural(tag.trades, "trade"), tag.days && plural(tag.days, "day")].filter(Boolean).join(" · ") || "unused";
  const antiforgeryToken = () => document.querySelector("input[name=__RequestVerificationToken]")?.value || "";

  async function getJson(url) {
    const response = await fetch(url, { cache: "no-store", credentials: "same-origin", headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("Tags could not be loaded.");
    return response.json();
  }

  async function postForm(url, values) {
    const body = new URLSearchParams(values);
    body.set("__RequestVerificationToken", antiforgeryToken());
    const response = await fetch(url, { method: "POST", body, credentials: "same-origin", headers: { Accept: "application/json", "X-Requested-With": "XMLHttpRequest" } });
    let payload = {};
    try { payload = await response.json(); } catch { /* the generic message below covers it */ }
    // An expired session redirects to the login page, which is a 200 but saved nothing.
    return { ok: response.ok && !response.redirected, status: response.status, payload };
  }

  const catalogs = new Map();
  function loadCatalog(url, refresh = false) {
    if (refresh || !catalogs.has(url)) {
      const request = getJson(`${url}?handler=Catalog`).then(payload => payload.tags);
      request.catch(() => { if (catalogs.get(url) === request) catalogs.delete(url); });
      catalogs.set(url, request);
    }
    return catalogs.get(url);
  }

  let popup;
  function tagPopup() {
    if (popup) return popup;
    const dialog = make("dialog", "tf-tag-dialog");
    dialog.setAttribute("aria-labelledby", "tf-tag-dialog-title");
    // site.js turns alerts into toasts, which would sit hidden behind this modal. Its errors stay inline.
    dialog.dataset.notificationExclude = "";
    // Static markup only. Names and descriptions are always written with textContent.
    dialog.innerHTML = `
      <form method="dialog" novalidate>
        <h2 id="tf-tag-dialog-title" class="tf-card-title">Create a new tag</h2>
        <label class="tf-tag-field">Confirm name
          <input class="form-control" name="name" maxlength="80" autocomplete="off" spellcheck="true">
        </label>
        <div class="tf-tag-matches" data-matches aria-live="polite" hidden></div>
        <label class="tf-tag-field">Description
          <textarea class="form-control" name="description" rows="3" maxlength="500" placeholder="When exactly does this tag apply?"></textarea>
        </label>
        <fieldset class="tf-tag-choice">
          <legend>Applies to</legend>
          <label><input type="radio" name="appliesTo" value="Trade"> Trades</label>
          <label><input type="radio" name="appliesTo" value="Day"> Days</label>
          <label><input type="radio" name="appliesTo" value="Both"> Both</label>
        </fieldset>
        <fieldset class="tf-tag-choice tf-tag-choice-stacked">
          <legend>When do you know it?</legend>
          <label><input type="radio" name="known" value="Beforehand"> <span><strong>Beforehand</strong>: before the entry, or before the session for a day. Counts toward probabilities.</span></label>
          <label><input type="radio" name="known" value="During"> <span><strong>During</strong>: while the trade or session is underway.</span></label>
          <label><input type="radio" name="known" value="After"> <span><strong>After</strong>: only once you know how it turned out.</span></label>
        </fieldset>
        <p class="tf-tag-error" data-error role="alert" hidden></p>
        <div class="tf-tag-dialog-actions">
          <button type="button" class="btn btn-outline-secondary" data-cancel>Cancel</button>
          <button type="submit" class="btn btn-primary" data-create>Create tag</button>
        </div>
      </form>`;
    document.body.append(dialog);

    const form = dialog.querySelector("form");
    const name = form.elements.namedItem("name");
    const description = form.elements.namedItem("description");
    const matches = dialog.querySelector("[data-matches]");
    const error = dialog.querySelector("[data-error]");
    const create = dialog.querySelector("[data-create]");
    let options;
    let similar = [];
    let confirmation = null;
    let checkTimer;
    let checkNumber = 0;

    const showError = message => {
      error.textContent = message || "";
      error.hidden = !message;
    };

    const useExisting = tag => {
      dialog.close();
      options.onUse(tag);
    };

    const existingButton = tag => {
      if (tag.archived) return make("small", "", "Archived. Restore it on the Tags page to use it.");
      const widen = options.target && !tag[options.target];
      const label = widen ? `Allow on ${options.target}s and use ${tag.name}` : options.target ? `Use ${tag.name}` : `Open ${tag.name}`;
      const button = make("button", "btn btn-sm btn-outline-primary", label);
      button.type = "button";
      button.addEventListener("click", async () => {
        if (!widen) return useExisting(tag);
        button.disabled = true;
        try {
          const result = await postForm(`${options.url}?handler=AllowEverywhere`, { id: tag.id, revision: tag.revision });
          if (result.ok) return useExisting(result.payload.tag);
          showError(result.payload.message || "The tag could not be changed.");
        } catch {
          showError("The tag could not be changed. Try again.");
        }
        button.disabled = false;
      });
      return button;
    };

    const showMatches = result => {
      matches.replaceChildren();
      similar = result.similar || [];
      confirmation = null;
      create.disabled = Boolean(result.existing);
      const listed = result.existing ? [result.existing] : similar;
      matches.hidden = listed.length === 0;
      if (!listed.length) return;

      matches.append(make("strong", "", result.existing ? "This tag already exists" : "Similar tags already exist"));
      listed.forEach(tag => {
        const row = make("div", "tf-tag-match");
        const text = make("div", "");
        text.append(make("b", "", tag.name), make("small", "", `${usage(tag)}${tag.description ? ` · ${tag.description}` : ""}`));
        row.append(text, existingButton(tag));
        matches.append(row);
      });
      if (!result.existing) {
        const label = make("label", "tf-tag-confirm");
        confirmation = make("input", "");
        confirmation.type = "checkbox";
        label.append(confirmation, document.createTextNode("This is a different tag"));
        matches.append(label);
      }
    };

    const check = async () => {
      const number = ++checkNumber;
      try {
        const query = new URLSearchParams({ handler: "Check", name: name.value });
        const result = await getJson(`${options.url}?${query}`);
        if (number === checkNumber) showMatches(result);
      } catch {
        // Creating runs the same checks on the server.
      }
    };

    name.addEventListener("input", () => {
      clearTimeout(checkTimer);
      checkNumber++;
      similar = [];
      confirmation = null;
      create.disabled = false;
      matches.hidden = true;
      showError("");
      checkTimer = setTimeout(check, 200);
    });
    // Enter in the name must not create; the remaining fields still need attention.
    form.addEventListener("keydown", event => {
      if (event.key !== "Enter" || event.target.tagName !== "INPUT") return;
      event.preventDefault();
      if (event.target === name) description.focus();
    });
    dialog.querySelector("[data-cancel]").addEventListener("click", () => dialog.close());
    dialog.addEventListener("close", () => options?.opener?.focus());

    form.addEventListener("submit", async event => {
      event.preventDefault();
      if (create.disabled) return;
      const known = form.querySelector("[name=known]:checked")?.value;
      const appliesTo = form.querySelector("[name=appliesTo]:checked")?.value;
      if (!name.value.trim()) { showError("Enter a tag name."); return name.focus(); }
      if (similar.length && !confirmation?.checked) return showError("Use the similar tag, or confirm that this is a different one.");
      if (!description.value.trim()) { showError("Describe when this tag applies."); return description.focus(); }
      if (!appliesTo) return showError("Choose what this tag applies to.");
      if (!known) return showError("Choose when you know this tag.");

      const values = [["name", name.value], ["description", description.value], ["known", known], ["appliesTo", appliesTo]];
      if (confirmation?.checked) similar.forEach(tag => values.push(["confirmedDifferent", tag.id]));
      create.disabled = true;
      showError("");
      try {
        const result = await postForm(`${options.url}?handler=Create`, values);
        if (result.ok) {
          const tag = result.payload.tag;
          loadCatalog(options.url, true).catch(() => {});
          dialog.close();
          if (!options.target || tag[options.target]) options.onUse(tag);
          return;
        }
        create.disabled = false;
        if (result.payload.existing || result.payload.similar) {
          clearTimeout(checkTimer);
          checkNumber++;
          showMatches(result.payload);
        }
        showError(result.payload.message || "The tag could not be created.");
      } catch {
        create.disabled = false;
        showError("The tag could not be created. Try again.");
      }
    });

    popup = {
      open(next) {
        options = next;
        clearTimeout(checkTimer);
        checkNumber++;
        form.reset();
        name.value = next.name || "";
        if (next.target) form.querySelector(`[name=appliesTo][value=${next.target === "day" ? "Day" : "Trade"}]`).checked = true;
        showMatches({});
        showError("");
        dialog.showModal();
        name.focus();
        name.select();
        if (name.value.trim()) void check();
      }
    };
    return popup;
  }

  const daySavers = new Set();

  function initializePicker(root) {
    if (root.dataset.tagReady) return;
    root.dataset.tagReady = "true";

    const url = root.dataset.tagsUrl;
    const target = root.dataset.target;
    const form = root.closest("form");
    const search = root.querySelector("[data-tag-search]");
    const menu = root.querySelector("[data-tag-menu]");
    const chips = root.querySelector("[data-tag-chips]");
    const status = root.querySelector("[data-tag-status]");
    const hidden = root.querySelector("[data-tag-ids]");
    let selected = JSON.parse(root.dataset.selected || "[]");
    let available = [];
    let options = [];
    let highlighted = -1;
    let revision = Number(root.dataset.revision || 0);
    let savedIds = selected.map(tag => tag.id).join(",");
    let saving = null;
    let loaded = false;
    let loading = null;

    const ids = () => selected.map(tag => tag.id).join(",");
    const setStatus = (message, isError = false) => {
      status.textContent = message || "";
      status.classList.toggle("is-error", isError);
    };

    const closeMenu = () => {
      menu.hidden = true;
      highlighted = -1;
      search.setAttribute("aria-expanded", "false");
      search.removeAttribute("aria-activedescendant");
    };

    const renderSummary = () => {
      const summary = root.closest("[data-daily-journal-entry]")?.querySelector("[data-tag-summary]");
      if (!summary) return;
      summary.replaceChildren(...selected.map(tag => make("span", "tf-tag-chip", tag.name)));
      summary.hidden = selected.length === 0;
    };

    const renderChips = () => {
      chips.replaceChildren(...selected.map(tag => {
        const chip = make("span", "tf-tag-chip", tag.name);
        const remove = make("button", "tf-tag-remove", "×");
        remove.type = "button";
        remove.setAttribute("aria-label", `Remove ${tag.name}`);
        remove.addEventListener("click", () => {
          selected = selected.filter(item => item.id !== tag.id);
          changed();
          search.focus();
        });
        chip.append(remove);
        return chip;
      }));
      renderSummary();
    };

    const save = () => {
      if (saving) return saving;
      if (ids() === savedIds) return Promise.resolve(true);
      saving = (async () => {
        while (ids() !== savedIds) {
          const sent = ids();
          setStatus("Saving…");
          const result = await postForm(`${url}?handler=SaveDay`, { date: root.dataset.date, tagIds: sent, expectedRevision: revision });
          if (result.status === 409) {
            selected = result.payload.tags;
            revision = result.payload.revision;
            savedIds = ids();
            renderChips();
            setStatus(result.payload.message, true);
            return false;
          }
          if (!result.ok) throw new Error(result.payload.message || "The tags could not be saved.");
          revision = result.payload.revision;
          savedIds = sent;
        }
        setStatus("Tags saved");
        return true;
      })().catch(error => {
        setStatus(error.message || "The tags could not be saved.", true);
        return false;
      }).finally(() => { saving = null; });
      return saving;
    };

    function changed() {
      renderChips();
      setStatus("");
      if (hidden) {
        hidden.value = ids();
        // The trade's tags share the review's revision, so they save with its autosave.
        form?.dispatchEvent(new CustomEvent("tf-tags-change"));
      } else {
        void save();
      }
    }

    const add = tag => {
      if (!selected.some(item => item.id === tag.id)) {
        if (selected.length >= 25) return setStatus("Choose at most 25 tags.", true);
        selected.push({ id: tag.id, name: tag.name });
        changed();
      }
      search.value = "";
      closeMenu();
    };

    const openPopup = () => {
      const name = search.value.trim();
      closeMenu();
      tagPopup().open({ url, target, name, opener: search, onUse: add });
    };

    const highlight = index => {
      highlighted = index;
      options.forEach((option, position) => option.node.setAttribute("aria-selected", String(position === index)));
      if (index < 0) return search.removeAttribute("aria-activedescendant");
      search.setAttribute("aria-activedescendant", options[index].node.id);
      options[index].node.scrollIntoView({ block: "nearest" });
    };

    const render = () => {
      if (!loaded) {
        options = [];
        highlighted = -1;
        menu.replaceChildren(make("li", "tf-tag-note", "Loading tags…"));
        menu.hidden = false;
        return;
      }
      const query = search.value.trim();
      const key = keyOf(query);
      const words = query.toLowerCase();
      const ranked = available
        .filter(tag => tag[target] && !selected.some(item => item.id === tag.id))
        .map(tag => {
          const nameKey = keyOf(tag.name);
          const aliases = (tag.aliases || []).map(keyOf);
          let rank = -1;
          if (!query) rank = 5;
          else if (key && (nameKey === key || aliases.includes(key))) rank = 0;
          else if (key && nameKey.startsWith(key)) rank = 1;
          else if (key && nameKey.includes(key)) rank = 2;
          else if (key && aliases.some(alias => alias.includes(key))) rank = 3;
          else if ((tag.description || "").toLowerCase().includes(words)) rank = 4;
          return { tag, rank };
        })
        .filter(item => item.rank >= 0)
        .sort((a, b) => a.rank - b.rank || (b.tag[`${target}s`] - a.tag[`${target}s`]) || a.tag.name.localeCompare(b.tag.name))
        .slice(0, 50);

      menu.replaceChildren();
      options = [];
      const append = (node, run) => {
        node.id = `${menu.id}-${options.length}`;
        node.setAttribute("role", "option");
        node.setAttribute("aria-selected", "false");
        node.addEventListener("mousedown", event => event.preventDefault());
        node.addEventListener("click", run);
        options.push({ node, run });
        menu.append(node);
      };

      ranked.forEach(({ tag }) => {
        const node = make("li", "tf-tag-option");
        node.append(make("strong", "", tag.name), make("span", "", usage(tag)), make("small", "", tag.needsReview ? "Needs a description or timing" : tag.description));
        append(node, () => add(tag));
      });

      const alreadyOn = key && selected.some(item => keyOf(item.name) === key);
      const hasExact = ranked[0]?.rank === 0;
      if (alreadyOn && !hasExact) {
        menu.append(make("li", "tf-tag-note", "Already added"));
      } else if (!hasExact) {
        append(make("li", "tf-tag-option tf-tag-create", query ? `Create new tag “${query}”…` : "Create new tag…"), openPopup);
      }

      menu.hidden = false;
      search.setAttribute("aria-expanded", "true");
      // Only an unambiguous match starts highlighted, so a stray Enter never picks a near miss.
      const onePrefix = ranked[0]?.rank === 1 && ranked.filter(item => item.rank === 1).length === 1;
      highlight(hasExact || onePrefix ? 0 : -1);
    };

    const refresh = async () => {
      try {
        available = await loadCatalog(url, true);
        loaded = true;
        if (document.activeElement === search) render();
      } catch {
        setStatus("Tags could not be loaded.", true);
      }
    };

    // Dragging the menu's scrollbar must not blur the search and close the menu.
    menu.addEventListener("mousedown", event => event.preventDefault());
    search.addEventListener("focus", () => { setStatus(""); loading = refresh(); });
    search.addEventListener("click", () => { if (menu.hidden) render(); });
    search.addEventListener("input", () => { setStatus(""); render(); });
    search.addEventListener("blur", () => {
      closeMenu();
      if (search.value.trim()) setStatus("Not added. Choose a tag from the list, or create it.");
    });
    search.addEventListener("keydown", event => {
      if (event.isComposing) return;
      if (event.key === "Enter") {
        event.preventDefault();
        if (!loaded) {
          // Typed faster than the list arrived: take the match once it is here, if nothing changed meanwhile.
          const typed = search.value;
          void loading?.then(() => {
            if (!loaded || !typed.trim() || document.activeElement !== search || search.value !== typed) return;
            if (highlighted >= 0) options[highlighted].run();
            else setStatus("Choose a tag from the list, or Create new tag.");
          });
          return;
        }
        if (!menu.hidden && highlighted >= 0) options[highlighted].run();
        else if (search.value.trim()) setStatus("Choose a tag from the list, or Create new tag.");
      } else if (event.key === "ArrowDown" || event.key === "ArrowUp") {
        event.preventDefault();
        if (menu.hidden) render();
        if (!options.length) return;
        const step = event.key === "ArrowDown" ? 1 : -1;
        highlight(highlighted < 0 ? (step > 0 ? 0 : options.length - 1) : (highlighted + step + options.length) % options.length);
      } else if (event.key === "Escape" && !menu.hidden) {
        event.preventDefault();
        event.stopPropagation();
        closeMenu();
      }
    });

    if (!hidden) daySavers.add({ flush: async () => (saving ? await saving : true) && await save(), dirty: () => Boolean(saving) || ids() !== savedIds });
    renderChips();
  }

  window.tradeFoundryInitializeTagPickers = (scope = document) => scope.querySelectorAll("[data-tag-picker]").forEach(initializePicker);
  window.tradeFoundryFlushTags = async () => {
    for (const saver of daySavers) if (!await saver.flush()) return false;
    return true;
  };

  window.addEventListener("beforeunload", event => {
    if (![...daySavers].some(saver => saver.dirty())) return;
    event.preventDefault();
    event.returnValue = "";
  });

  document.addEventListener("DOMContentLoaded", () => {
    window.tradeFoundryInitializeTagPickers();

    document.querySelector("[data-tag-new]")?.addEventListener("click", event => {
      const url = event.currentTarget.dataset.tagsUrl;
      tagPopup().open({ url, name: "", opener: event.currentTarget, onUse: tag => window.location.assign(`${url}?tagId=${tag.id}`) });
    });

    const filter = document.querySelector("[data-tag-filter]");
    filter?.addEventListener("input", () => {
      const key = keyOf(filter.value);
      const words = filter.value.trim().toLowerCase();
      document.querySelectorAll("[data-tag-row]").forEach(row => {
        row.hidden = Boolean(words) && !(key && keyOf(row.dataset.names).includes(key)) && !row.dataset.text.toLowerCase().includes(words);
      });
    });

    document.querySelectorAll("form[data-confirm]").forEach(form => {
      form.addEventListener("submit", event => {
        if (!window.confirm(form.dataset.confirm)) event.preventDefault();
      });
    });
  });
})();
