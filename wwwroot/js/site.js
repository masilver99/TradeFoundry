document.addEventListener("DOMContentLoaded", () => {
  initializeNotifications();
  initializeChartExportOptions();
  initializePlotlyCharts();
  initializeBrokerComparison();
  initializeEquityToggle();
  initializeLightweightCharts();
  initializeAnalysisNavigation();
  initializeIndicatorNavigation();
  initializeChartInfo();
  initializeSectionInfo();
  initializePeriodSummary();
  initializePnlCalendar();
  initializeDailyJournal();
  initializeReviewWorkspace();
  initializeReviewAttachmentPaste();
  initializeReviewAttachmentCaptions();
  initializeReviewAttachmentRemovals();
  initializeSidebarResize();
  initializeSidebarVisibility();
  initializeImportDropzones();
  window.TradeFoundryLexical?.initializeLexicalEditors();

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

function initializeNotifications() {
  const host = document.querySelector("[data-notification-host]");
  if (!host) return;

  const toggle = host.querySelector("[data-notification-toggle]");
  const panel = host.querySelector("[data-notification-panel]");
  const list = host.querySelector("[data-notification-list]");
  const empty = host.querySelector("[data-notification-empty]");
  const badge = host.querySelector("[data-notification-count]");
  const summary = host.querySelector("[data-notification-summary]");
  const owner = document.body.dataset.notificationOwnerName || "owner";
  const storageKey = "tradefoundry.notifications.v1." + encodeURIComponent(owner.trim().toLowerCase());
  const safeHref = value => {
    if (!value) return "";
    try {
      const url = new URL(value, window.location.href);
      return url.origin === window.location.origin && url.pathname.startsWith("/journal/") ? url.pathname + url.search + url.hash : "";
    } catch {
      return "";
    }
  };

  let state = { items: [], dismissed: [] };
  let ephemeralItems = [];
  try {
    const saved = JSON.parse(window.localStorage.getItem(storageKey) || "null");
    if (saved && Array.isArray(saved.items) && Array.isArray(saved.dismissed)) {
      state = {
        items: saved.items.filter(item => item && typeof item.key === "string" && typeof item.message === "string"),
        dismissed: saved.dismissed.filter(key => typeof key === "string")
      };
    }
  } catch {
    state = { items: [], dismissed: [] };
  }

  const persist = () => {
    try {
      window.localStorage.setItem(storageKey, JSON.stringify(state));
    } catch {
      // Keep notifications usable for this page even when browser storage is unavailable.
    }
  };

  const render = () => {
    list.replaceChildren();
    const items = [...ephemeralItems, ...state.items];
    const count = items.length;
    badge.textContent = String(count);
    badge.hidden = count === 0;
    toggle.setAttribute("aria-label", count ? "Notifications, " + count + " active" : "Notifications");
    if (summary) summary.textContent = count ? String(count) : "";
    empty.hidden = count !== 0;

    items.forEach(item => {
      const article = document.createElement("article");
      const severity = ["error", "warning", "success"].includes(item.severity) ? item.severity : "notice";
      article.className = "tf-notification-item tf-notification-" + severity;
      article.setAttribute("role", "listitem");

      const content = document.createElement("div");
      content.className = "tf-notification-content";
      const heading = document.createElement("div");
      heading.className = "tf-notification-meta";
      const type = document.createElement("strong");
      type.textContent = severity === "error" ? "Error" : severity === "warning" ? "Warning" : severity === "success" ? "Success" : "Notice";
      const date = document.createElement("time");
      date.dateTime = typeof item.createdAt === "string" ? item.createdAt : "";
      const parsedDate = new Date(item.createdAt);
      date.textContent = Number.isNaN(parsedDate.getTime()) ? "" : parsedDate.toLocaleString();
      heading.append(type, date);

      const href = safeHref(item.href);
      const message = href ? document.createElement("a") : document.createElement("p");
      message.className = "tf-notification-message";
      message.textContent = item.message;
      if (href) message.href = href;
      content.append(heading, message);

      const dismiss = document.createElement("button");
      dismiss.type = "button";
      dismiss.className = "tf-notification-dismiss";
      dismiss.setAttribute("aria-label", "Dismiss notification");
      dismiss.title = "Dismiss notification";
      dismiss.textContent = "×";
      dismiss.addEventListener("click", () => {
        if (item.ephemeral) {
          ephemeralItems = ephemeralItems.filter(candidate => candidate.key !== item.key);
        } else {
          state.items = state.items.filter(candidate => candidate.key !== item.key);
          if (!state.dismissed.includes(item.key)) state.dismissed.push(item.key);
          persist();
        }
        render();
      });

      article.append(content, dismiss);
      list.append(article);
    });
  };

  const capture = alert => {
    if (!(alert instanceof HTMLElement) || alert.hidden || alert.dataset.notificationExclude !== undefined) return;
    if (alert.closest("[data-notification-exclude]")) return;
    const containingAlert = alert.parentElement?.closest(".alert");
    if (containingAlert && containingAlert !== alert) return;

    const message = (alert.innerText || alert.textContent || "").replace(/\s+/g, " ").trim();
    if (!message) return;
    const ephemeral = alert.dataset.notificationEphemeral !== undefined;

    let severity = "notice";
    if (alert.classList.contains("error") || alert.classList.contains("alert-danger")) severity = "error";
    else if (alert.classList.contains("warning") || alert.classList.contains("alert-warning")) severity = "warning";
    else if (alert.classList.contains("success") || alert.classList.contains("alert-success")) severity = "success";
    else if (alert.getAttribute("role") === "alert") severity = "error";

    let href = safeHref(alert.dataset.notificationHref);
    if (!href) {
      const links = Array.from(alert.querySelectorAll("a[href]"));
      for (const link of links) {
        href = safeHref(link.href);
        if (href) break;
      }
    }

    const sourcePath = window.location.pathname;
    const key = JSON.stringify([sourcePath, severity, message, href]);
    const destination = ephemeral ? ephemeralItems : state.items;
    if ((ephemeral || !state.dismissed.includes(key)) && !destination.some(item => item.key === key)) {
      destination.unshift({ key, sourcePath, severity, message, href, createdAt: new Date().toISOString(), ephemeral });
      if (!ephemeral) persist();
      render();
    }

    alert.hidden = true;
    alert.dataset.notificationCaptured = "true";
  };

  const scan = root => {
    if (root instanceof HTMLElement && root.matches(".alert, [role='alert']")) capture(root);
    if (root && root.querySelectorAll) root.querySelectorAll(".alert, [role='alert']").forEach(capture);
  };

  toggle.addEventListener("click", () => {
    const open = panel.hidden;
    panel.hidden = !open;
    toggle.setAttribute("aria-expanded", String(open));
  });
  document.addEventListener("click", event => {
    if (!panel.hidden && !host.contains(event.target)) {
      panel.hidden = true;
      toggle.setAttribute("aria-expanded", "false");
    }
  });
  document.addEventListener("keydown", event => {
    if (event.key === "Escape" && !panel.hidden) {
      panel.hidden = true;
      toggle.setAttribute("aria-expanded", "false");
      toggle.focus();
    }
  });
  window.addEventListener("storage", event => {
    if (event.key !== storageKey) return;
    try {
      const saved = JSON.parse(event.newValue || "null");
      if (saved && Array.isArray(saved.items) && Array.isArray(saved.dismissed)) state = saved;
    } catch {
      state = { items: [], dismissed: [] };
    }
    render();
  });

  render();
  scan(document);
  const observer = new MutationObserver(records => {
    records.forEach(record => {
      if (record.type === "characterData") {
        const alert = record.target.parentElement?.closest(".alert, [role='alert']");
        if (alert) capture(alert);
        return;
      }
      if (record.target instanceof HTMLElement && record.target.matches(".alert, [role='alert']")) capture(record.target);
      record.addedNodes.forEach(scan);
    });
  });
  observer.observe(document.body, { childList: true, subtree: true, characterData: true });
}

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

function updateReviewImageIndicator(key, hasImages) {
  const row = Array.from(document.querySelectorAll("[data-review-select]")).find(item => item.dataset.reviewKey === key);
  row?.querySelector("[data-review-indicator=images]")?.toggleAttribute("hidden", !hasImages);
}

function initializeReviewAttachmentPaste() {
  const supportedTypes = new Set(["image/png", "image/jpeg", "image/jpg", "image/webp"]);
  document.querySelectorAll("[data-review-attachment-form]").forEach(form => {
    const zone = form.querySelector("[data-review-paste-zone]");
    const fileInput = form.querySelector("[data-review-image-input]");
    const preview = form.querySelector("[data-review-image-preview]");
    const previewImage = form.querySelector("[data-review-image-preview-image]");
    const previewName = form.querySelector("[data-review-image-preview-name]");
    const status = form.querySelector("[data-review-image-paste-status]");
    const submitButton = form.querySelector("[data-review-attachment-submit]");
    const editor = form.closest("[data-review-editor]");
    const attachments = editor?.querySelector("[data-review-attachments]");
    const template = editor?.querySelector("[data-review-attachment-template]");
    if (!zone || !fileInput) return;

    let objectUrl = "";
    let uploading = false;
    const setStatus = text => {
      if (status) status.textContent = text;
    };
    const showFile = file => {
      if (!file) return;
      if (!supportedTypes.has(String(file.type || "").toLowerCase())) {
        setStatus("Only PNG, JPEG, and WebP images are supported.");
        return;
      }
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = URL.createObjectURL(file);
      if (previewImage) previewImage.src = objectUrl;
      if (previewName) previewName.textContent = `${file.name} · ${Math.max(1, Math.round(file.size / 1024))} KB`;
      if (preview) preview.hidden = false;
      setStatus("Image ready. Add a caption, then attach it.");
    };

    const appendAttachment = attachment => {
      if (!attachments || !template?.content?.firstElementChild) throw new Error("The attachment area is unavailable.");
      const card = template.content.firstElementChild.cloneNode(true);
      const link = card.querySelector("[data-review-attachment-link]");
      const image = card.querySelector("[data-review-attachment-image]");
      const captionForm = card.querySelector("[data-review-attachment-caption-form]");
      const captionInput = card.querySelector("[data-review-attachment-caption]");
      const captionLabel = card.querySelector("[data-review-attachment-caption-label]");
      const removeForm = card.querySelector("[data-review-attachment-remove-form]");
      const meta = card.querySelector("[data-review-attachment-meta]");
      const id = String(attachment.id);
      const fileName = String(attachment.originalFileName || "screenshot");
      const caption = String(attachment.caption || "");
      const url = String(attachment.url || "");

      card.dataset.attachmentId = id;
      if (link) link.href = url;
      if (image) {
        image.src = url;
        image.alt = caption || fileName;
        image.dataset.captionFallback = fileName;
      }
      if (captionForm) {
        if (attachment.captionUrl) {
          captionForm.action = attachment.captionUrl;
          captionForm.dataset.captionUrl = attachment.captionUrl;
        }
        const idInput = captionForm.querySelector('input[name="attachmentId"]');
        if (idInput) idInput.value = id;
      }
      if (captionInput) {
        captionInput.value = caption;
        captionInput.defaultValue = caption;
        captionInput.id = `attachment-caption-${id}`;
      }
      if (captionLabel && captionInput) {
        captionLabel.htmlFor = captionInput.id;
        captionLabel.textContent = `Caption for ${fileName}`;
      }
      if (removeForm) {
        const idInput = removeForm.querySelector('input[name="attachmentId"]');
        if (idInput) idInput.value = id;
      }
      if (meta) meta.textContent = `${fileName} · ${Math.max(1, Math.round(Number(attachment.length || 0) / 1024))} KB`;

      attachments.appendChild(card);
      attachments.hidden = false;
      window.tradeFoundryRegisterReviewAttachmentCaption?.(captionForm);
      return card;
    };

    const resetUpload = () => {
      fileInput.value = "";
      const captionInput = form.querySelector('input[name="AttachmentCaption"]');
      if (captionInput) captionInput.value = "";
      if (objectUrl) URL.revokeObjectURL(objectUrl);
      objectUrl = "";
      if (previewImage) previewImage.removeAttribute("src");
      if (previewName) previewName.textContent = "";
      if (preview) preview.hidden = true;
    };

    const upload = async () => {
      if (uploading) return false;
      const file = fileInput.files?.[0];
      if (!file) {
        setStatus("Choose or paste an image before attaching it.");
        return false;
      }

      uploading = true;
      if (submitButton) submitButton.disabled = true;
      setStatus("Uploading image…");
      try {
        const response = await fetch(form.action, {
          method: "POST",
          body: new FormData(form),
          credentials: "same-origin",
          headers: { "X-Requested-With": "XMLHttpRequest" }
        });
        let result = {};
        try { result = await response.json(); } catch { /* The status below is enough for a malformed response. */ }
        if (!response.ok || !result.saved || !result.attachment) {
          throw new Error(result.message || "Could not attach the image.");
        }

        const card = appendAttachment(result.attachment);
        resetUpload();
        setStatus("Image attached.");
        card?.querySelector("[data-review-attachment-caption]")?.focus({ preventScroll: true });
        return true;
      } catch (error) {
        setStatus(error instanceof Error ? error.message : "Could not attach the image.");
        return false;
      } finally {
        uploading = false;
        if (submitButton) submitButton.disabled = false;
      }
    };

    fileInput.addEventListener("change", () => showFile(fileInput.files?.[0]));
    form.addEventListener("paste", event => {
      const item = Array.from(event.clipboardData?.items || []).find(candidate => supportedTypes.has(String(candidate.type || "").toLowerCase()) && candidate.kind === "file");
      if (!item) return;
      const blob = item.getAsFile();
      if (!blob) return;

      const type = String(item.type || blob.type || "image/png").toLowerCase();
      const extension = type === "image/jpeg" || type === "image/jpg" ? "jpg" : type === "image/webp" ? "webp" : "png";
      const file = new File([blob], `pasted-image.${extension}`, { type, lastModified: Date.now() });
      let assigned = false;
      try {
        const transfer = new DataTransfer();
        transfer.items.add(file);
        fileInput.files = transfer.files;
        assigned = true;
      } catch {
        setStatus("Your browser could not attach the clipboard image. Use the file picker instead.");
      }
      if (!assigned) return;

      event.preventDefault();
      fileInput.dispatchEvent(new Event("change", { bubbles: true }));
      void upload();
    });

    form.addEventListener("submit", event => {
      event.preventDefault();
      void upload();
    });

    zone.addEventListener("click", () => zone.focus({ preventScroll: true }));
    zone.addEventListener("keydown", event => {
      if (event.key !== "Enter" && event.key !== " ") return;
      event.preventDefault();
      fileInput.click();
    });
  });
}

function initializeReviewAttachmentCaptions() {
  const flushers = [];
  const forms = [];
  const bindForm = form => {
    if (!form || form.dataset.reviewAttachmentCaptionBound === "true") return;
    const input = form.querySelector("[data-review-attachment-caption]");
    const status = form.querySelector("[data-review-attachment-caption-status]");
    const image = form.closest(".tf-review-attachment-card")?.querySelector("[data-review-attachment-image]");
    const url = form.dataset.captionUrl || form.action;
    if (!input || !url) return;
    form.dataset.reviewAttachmentCaptionBound = "true";
    forms.push(form);

    let savedValue = input.value;
    let timer = null;
    let inFlight = null;

    const setStatus = (text, type = "") => {
      if (!status) return;
      status.textContent = text;
      status.classList.remove("is-saving", "is-saved", "is-error");
      if (type) status.classList.add(type);
    };

    const schedule = () => {
      if (timer) window.clearTimeout(timer);
      timer = window.setTimeout(() => { void save(); }, 450);
    };

    const save = () => {
      if (input.value === savedValue) {
        setStatus("");
        return Promise.resolve(true);
      }
      if (inFlight) return inFlight;

      const value = input.value;
      const payload = new FormData(form);
      payload.set("caption", value);
      setStatus("Saving…", "is-saving");
      let failed = false;
      inFlight = fetch(url, {
        method: "POST",
        body: payload,
        credentials: "same-origin",
        headers: { "X-Requested-With": "XMLHttpRequest" }
      }).then(async response => {
        let result = {};
        try { result = await response.json(); } catch { /* The status below is enough for a malformed response. */ }
        if (!response.ok || !result.saved) throw new Error(result.message || "Could not save caption.");
        savedValue = String(result.caption ?? value);
        if (input.value === value) {
          input.value = savedValue;
          input.defaultValue = savedValue;
          if (image) image.alt = savedValue || image.dataset.captionFallback || "";
          setStatus("Saved", "is-saved");
        } else {
          setStatus("Unsaved");
        }
        return true;
      }).catch(error => {
        failed = true;
        setStatus(error instanceof Error ? error.message : "Could not save caption.", "is-error");
        return false;
      }).finally(() => {
        inFlight = null;
        if (!failed && input.value !== savedValue) schedule();
      });
      return inFlight;
    };

    flushers.push(async () => {
      if (timer) window.clearTimeout(timer);
      const result = inFlight ? await inFlight : await save();
      if (result !== false && input.value !== savedValue) return await save();
      return result !== false;
    });

    input.addEventListener("input", () => {
      setStatus("Unsaved");
      schedule();
    });
    input.addEventListener("blur", () => {
      if (timer) window.clearTimeout(timer);
      void save();
    });
    input.addEventListener("keydown", event => {
      if (event.key !== "Enter") return;
      event.preventDefault();
      input.blur();
    });
    form.addEventListener("submit", event => {
      event.preventDefault();
      void save();
    });
  };

  document.querySelectorAll("[data-review-attachment-caption-form]").forEach(bindForm);
  window.tradeFoundryRegisterReviewAttachmentCaption = bindForm;

  window.tradeFoundryFlushAttachmentCaptions = async () => {
    const results = await Promise.all(flushers.map(flush => flush()));
    return results.every(Boolean);
  };
  window.addEventListener("beforeunload", event => {
    if (forms.some(form => {
      const input = form.querySelector("[data-review-attachment-caption]");
      return input && input.value !== input.defaultValue;
    })) {
      event.preventDefault();
      event.returnValue = "";
    }
  });
}

function initializeReviewAttachmentRemovals() {
  document.querySelectorAll("[data-review-editor]").forEach(editor => {
    if (editor.dataset.reviewAttachmentRemoveBound === "true") return;
    editor.dataset.reviewAttachmentRemoveBound = "true";

    editor.addEventListener("submit", event => {
      const form = event.target.closest("[data-review-attachment-remove-form]");
      if (!form || !editor.contains(form)) return;
      if (form.dataset.removing === "true") {
        event.preventDefault();
        return;
      }

      const card = form.closest(".tf-review-attachment-card");
      const button = form.querySelector("button[type=submit]");
      if (!card || !button) return;

      event.preventDefault();
      form.dataset.removing = "true";
      const status = form.querySelector("[data-review-attachment-remove-status]");
      const originalText = button.textContent;
      button.disabled = true;
      button.textContent = "Removing…";
      if (status) status.textContent = "";

      fetch(form.action, {
        method: "POST",
        body: new FormData(form),
        credentials: "same-origin",
        headers: { "X-Requested-With": "XMLHttpRequest" }
      }).then(async response => {
        let result = {};
        try { result = await response.json(); } catch { /* The status below is enough for a malformed response. */ }
        if (!response.ok || !result.removed) throw new Error(result.message || "Could not remove the image.");

        card.remove();
        const attachments = editor.querySelector("[data-review-attachments]");
        const hasImages = Boolean(attachments?.querySelector(".tf-review-attachment-card"));
        if (attachments) attachments.hidden = !hasImages;
        updateReviewImageIndicator(editor.dataset.reviewKey || "", result.hasReviewImages ?? hasImages);
      }).catch(error => {
        if (status) status.textContent = error instanceof Error ? error.message : "Could not remove the image.";
        button.disabled = false;
        button.textContent = originalText;
      }).finally(() => {
        delete form.dataset.removing;
      });
    });
  });
}

function initializeDailyJournal() {
  const form = document.querySelector("[data-daily-journal-form]");
  const input = form?.querySelector("[name=dailyJournalStateJson]");
  const status = form?.querySelector("[data-daily-journal-status]");
  if (!form || !input) return;

  let savedSnapshot = input.value;
  let timer = null;
  let savePromise = null;

  const setStatus = (text, type = "") => {
    if (!status) return;
    status.textContent = text;
    status.classList.remove("is-saving", "is-error", "is-saved");
    if (type) status.classList.add(`is-${type}`);
  };

  const save = () => {
    if (savePromise) return savePromise;
    if (input.value === savedSnapshot) return Promise.resolve(true);
    const sentSnapshot = input.value;
    savePromise = (async () => {
      setStatus("Saving…", "saving");
      const response = await fetch(form.dataset.autosaveUrl, {
        method: "POST",
        body: new FormData(form),
        credentials: "same-origin",
        headers: { "X-Requested-With": "XMLHttpRequest" }
      });
      let payload = {};
      try { payload = await response.json(); } catch { /* fall through to the generic error */ }
      if (!response.ok) {
        if (response.status === 409 || payload.conflict) {
          setStatus("Conflict · reload latest values", "error");
          return false;
        }
        throw new Error(payload.message || "The daily journal could not be saved.");
      }
      const revision = form.querySelector("input[name=expectedRevision]");
      if (revision && payload.revision !== undefined) revision.value = payload.revision;
      savedSnapshot = sentSnapshot;
      const dirty = input.value !== savedSnapshot;
      setStatus(dirty ? "Saving changes…" : "Saved", dirty ? "saving" : "saved");
      if (dirty) window.setTimeout(save, 350);
      return true;
    })().catch(error => {
      console.error("Daily journal autosave failed.", error);
      setStatus(error.message || "Save failed", "error");
      return false;
    }).finally(() => {
      savePromise = null;
    });
    return savePromise;
  };

  const schedule = (immediate = false) => {
    clearTimeout(timer);
    if (input.value === savedSnapshot) return Promise.resolve(true);
    setStatus("Unsaved");
    if (immediate) return save();
    timer = window.setTimeout(save, 450);
    return Promise.resolve(true);
  };

  const flush = async () => {
    clearTimeout(timer);
    return savePromise ? await savePromise : await save();
  };
  window.tradeFoundryFlushDailyJournal = flush;

  input.addEventListener("input", () => schedule());
  input.addEventListener("blur", () => schedule(true));
  form.addEventListener("submit", event => {
    event.preventDefault();
    void schedule(true);
  });

  const shell = form.closest("[data-focus]");
  const flushNavigation = event => {
    const link = event.target.closest("a");
    if (!link || !link.href) return;
    const url = new URL(link.href, window.location.href);
    if (url.origin !== window.location.origin) return;
    event.preventDefault();
    void flush().then(ok => { if (ok) window.location.href = link.href; });
  };

  if (!document.querySelector("[data-review-workspace]")) {
    const dateForm = document.querySelector(".tf-review-date-form");
    dateForm?.addEventListener("submit", event => {
      event.preventDefault();
      void flush().then(ok => { if (ok) dateForm.submit(); });
    });
    shell?.addEventListener("click", flushNavigation);
  }

  window.addEventListener("beforeunload", event => {
    if (input.value !== savedSnapshot) {
      event.preventDefault();
      event.returnValue = "";
    }
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

function initializeSidebarVisibility() {
  const app = document.querySelector(".tf-app");
  const sidebar = app?.querySelector(".tf-sidebar");
  const toggles = Array.from(app?.querySelectorAll("[data-sidebar-toggle]") || []);
  if (!app || !sidebar || !toggles.length) return;

  const storageKey = "tradefoundry.sidebar.hidden";
  let hidden = false;
  try {
    hidden = window.localStorage.getItem(storageKey) === "true";
  } catch {
    // Ignore storage restrictions; visibility still works for the current page.
  }

  const syncState = (nextHidden, persist = false) => {
    hidden = nextHidden;
    app.classList.toggle("tf-sidebar-hidden", hidden);
    toggles.forEach(toggle => {
      const show = hidden;
      toggle.setAttribute("aria-expanded", String(!show));
      toggle.setAttribute("aria-label", `${show ? "Show" : "Hide"} sidebar`);
      toggle.setAttribute("title", `${show ? "Show" : "Hide"} sidebar`);
      if (toggle.classList.contains("tf-sidebar-reopen")) toggle.hidden = !show;
    });
    if (!persist) return;
    try {
      window.localStorage.setItem(storageKey, String(hidden));
    } catch {
      // Ignore storage restrictions; the current visibility remains applied.
    }
  };

  toggles.forEach(toggle => toggle.addEventListener("click", () => syncState(!hidden, true)));
  syncState(hidden);
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
    ["Monte Carlo simulated paths", "Each of 400 paths samples recorded trade P&Ls with replacement and compounds them in a new order. Compare the actual path with the median and 5th–95th percentile bands to see how sensitive this sample's outcomes are to trade order. Use the spread as a scenario range for planning and capital decisions. This is not a forecast: it assumes the sample remains representative and cannot create market regimes or trade outcomes absent from the record."],
    ["Underwater net P&L", "Plot the distance below the running equity high to make drawdown depth, timing, and time underwater visible."],
    ["Worst five drawdown periods", "Rank the most severe drawdown episodes by depth and recovery duration so the journal's hardest stretches can be reviewed directly."],
    ["Drawdown duration and recovery", "Compare drawdown length with recovery length to show how quickly the strategy repairs losses after each equity high."],
    ["Expectancy, win rate, and Sharpe", "Track rolling expectancy, win rate, and Sharpe over a 20-trade window to see whether the edge and risk-adjusted return are stable."],
    ["Rolling six-month volatility", "Measure how the journal's six-month return variability changes over time, highlighting calmer and more turbulent regimes."],
    ["Rolling six-month Sharpe", "Track six-month Sharpe to compare return against volatility across changing conditions."],
    ["Rolling six-month Sortino", "Track six-month Sortino to compare return against downside variability, emphasizing harmful volatility."],
    ["Rolling rescue dependency", "Track rescue rate and rescue profit share over a fixed 20-trade window; hover details retain the winner count and risk-data coverage."],
    ["Rolling winner heat and MAE", "Track winner MAE P90 and Winner Heat Ratio over a fixed 20-trade window to see whether small winners are taking more heat or producing less result."],
    ["Rolling MAE violations", "Track MAE violation rate and count over a fixed 20-trade window; a violation is an MAE excursion at or above the configured risk threshold."],
    ["Monthly Risk Discipline score", "Track each month's Risk Discipline score beside its completed-trade count; incomplete monthly risk evidence leaves the score blank rather than implying a result."],
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
    if (label === "Monte Carlo simulated paths") {
      panel.append(document.createTextNode(" "));
      const learnMore = document.createElement("a");
      learnMore.href = "https://itl.nist.gov/div898/handbook/eda/section3/bootplot.htm";
      learnMore.target = "_blank";
      learnMore.rel = "noopener noreferrer";
      learnMore.textContent = "Learn more about bootstrap uncertainty.";
      panel.appendChild(learnMore);
    }
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
      const dayNode = element(day.tradeCount > 0 ? "a" : "div", "tf-pnl-day", undefined);
      if (day.tradeCount > 0 && root.dataset.pnlReviewBase) {
        dayNode.href = `${root.dataset.pnlReviewBase}?date=${encodeURIComponent(day.date)}`;
        dayNode.title = "Open Daybook for this date";
      }
      dayNode.dataset.tone = tone;
      dayNode.setAttribute("aria-label", day.tradeCount > 0
        ? `${day.date}: ${formatPnl(day.netPnl)}, ${day.tradeCount} ${day.tradeCount === 1 ? "trade" : "trades"}, ${Number(day.points || 0).toFixed(2)} points${day.averageMaePoints === null || day.averageMaePoints === undefined ? "" : `, average MAE ${Number(day.averageMaePoints).toFixed(2)} points`}`
        : `${day.date}: no completed trades`);
      if (day.tradeCount === 0) dayNode.classList.add("tf-pnl-day-no-trades");
      const footer = element("div", "tf-pnl-day-footer");
      footer.append(element("small", "tf-pnl-day-count", day.tradeCount > 0 ? `${day.tradeCount} ${day.tradeCount === 1 ? "trade" : "trades"}` : "no trades"));
      if (day.tradeCount > 0) {
        const metrics = element("span", "tf-pnl-day-metrics");
        metrics.append(element("span", "tf-pnl-day-points", `Pts ${Number(day.points || 0).toFixed(2)}`));
        if (day.averageMaePoints !== null && day.averageMaePoints !== undefined)
          metrics.append(element("span", "tf-pnl-day-mae", `Avg MAE ${Number(day.averageMaePoints).toFixed(2)}`));
        footer.append(metrics);
      }
      dayNode.append(
        element("span", "tf-pnl-day-number", String(day.day)),
        element("strong", "tf-pnl-day-value", day.tradeCount > 0 ? formatPnl(day.netPnl) : "—"),
        footer
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

function initializeReviewWorkspace() {
  const shell = document.querySelector("[data-review-workspace]")?.closest("[data-focus]");
  const workspace = shell?.querySelector("[data-review-workspace]");
  if (!shell || !workspace) return;

  const editors = Array.from(workspace.querySelectorAll("[data-review-editor]"));
  const rosterItems = Array.from(workspace.querySelectorAll("[data-review-select]"));
  if (!editors.length) return;

  shell.classList.add("is-enhanced");
  const formState = new WeakMap();
  const sections = ["review", "plan", "media"];
  let activeKey = shell.dataset.activeKey || editors[0].dataset.reviewKey;
  let activeSection = sections.includes(shell.dataset.reviewSection) ? shell.dataset.reviewSection : "review";

  const stateFor = form => {
    let state = formState.get(form);
    if (!state) {
      state = { savedSnapshot: reviewFormSnapshot(form), dirty: false, timer: null, savePromise: null };
      formState.set(form, state);
    }
    return state;
  };

  const setStatus = (form, text, type = "") => {
    const status = form.querySelector("[data-review-status]");
    if (!status) return;
    status.textContent = text;
    status.classList.remove("is-saving", "is-error", "is-saved");
    if (type) status.classList.add(`is-${type}`);
  };

  const setSection = (editor, section) => {
    const nextSection = sections.includes(section) ? section : "review";
    editor.querySelectorAll("[data-review-tab]").forEach(tab => {
      const selected = tab.dataset.reviewTab === nextSection;
      tab.classList.toggle("is-active", selected);
      tab.setAttribute("aria-selected", String(selected));
      tab.setAttribute("tabindex", selected ? "0" : "-1");
    });
    editor.querySelectorAll("[data-review-panel]").forEach(panel => {
      const selected = panel.dataset.reviewPanel === nextSection;
      panel.classList.toggle("is-active", selected);
      panel.setAttribute("aria-hidden", String(!selected));
    });
  };

  const updateUrl = () => {
    const url = new URL(window.location.href);
    url.searchParams.set("focus", activeKey);
    url.searchParams.set("section", activeSection);
    window.history.replaceState({}, "", url);
  };

  const updateRosterState = (key, payload) => {
    const row = rosterItems.find(item => item.dataset.reviewKey === key);
    if (row) {
      row.querySelector("[data-review-indicator=images]")?.toggleAttribute("hidden", !payload?.hasReviewImages);
      row.querySelector("[data-review-indicator=notes]")?.toggleAttribute("hidden", !payload?.hasReviewNotes);
    }
  };

  const updateEffectiveValues = (form, payload) => {
    const editor = form.closest("[data-review-editor]");
    if (!editor) return;
    const currency = shell.dataset.currency || "USD";
    const formatMoney = value => new Intl.NumberFormat(undefined, { style: "currency", currency }).format(Number(value || 0));
    if (payload.trade) {
      const pnl = editor.querySelector("[data-review-trade-pnl]");
      const pnlStat = editor.querySelector("[data-review-trade-pnl-stat]");
      const fees = editor.querySelector("[data-review-trade-fees]");
      const feesStat = editor.querySelector("[data-review-trade-fees-stat]");
      const detailFees = editor.querySelector("[data-review-trade-fees-detail]");
      const feeBreakdown = editor.querySelectorAll("[data-review-trade-fee-breakdown]");
      const row = rosterItems.find(item => item.dataset.reviewKey === editor.dataset.reviewKey);
      const rowPnl = row?.querySelector("[data-review-roster-pnl]");
      if (pnl) {
        pnl.textContent = formatMoney(payload.trade.netPnl);
        pnl.classList.toggle("text-success", Number(payload.trade.netPnl) >= 0);
        pnl.classList.toggle("text-danger", Number(payload.trade.netPnl) < 0);
      }
      const feeText = `${formatMoney(payload.trade.fees)} · ${payload.trade.hasAllInOverride ? "all-in override" : payload.trade.hasComponentOverride ? "component override" : "imported/settings-derived"}`;
      const breakdownText = `Exchange ${formatMoney(payload.trade.exchangeFees)} · NFA ${formatMoney(payload.trade.nfaFees)} · Clearing / commission ${formatMoney(payload.trade.clearingFees)}${payload.trade.hasAllInOverride ? " · not used while all-in override is set" : payload.trade.hasComponentOverride ? " · per-trade component override" : " · imported/instrument-derived"}`;
      if (pnlStat) {
        pnlStat.textContent = formatMoney(payload.trade.netPnl);
        pnlStat.classList.toggle("text-success", Number(payload.trade.netPnl) >= 0);
        pnlStat.classList.toggle("text-danger", Number(payload.trade.netPnl) < 0);
      }
      if (fees) fees.textContent = `All-in commission: ${feeText}`;
      if (feesStat) feesStat.textContent = feeText;
      if (detailFees) detailFees.textContent = feeText;
      feeBreakdown.forEach(item => { item.textContent = breakdownText; });
      if (rowPnl && rowPnl.textContent.trim() !== "OPEN") {
        rowPnl.textContent = formatMoney(payload.trade.netPnl);
        rowPnl.classList.toggle("text-success", Number(payload.trade.netPnl) >= 0);
        rowPnl.classList.toggle("text-danger", Number(payload.trade.netPnl) < 0);
      }
    }
    if (payload.day) {
      const dayPnl = workspace.querySelector(".tf-review-roster-summary strong");
      if (dayPnl) {
        dayPnl.textContent = formatMoney(payload.day.realizedNetPnl);
        dayPnl.classList.toggle("text-success", Number(payload.day.realizedNetPnl) >= 0);
        dayPnl.classList.toggle("text-danger", Number(payload.day.realizedNetPnl) < 0);
      }
    }
  };

  const saveReview = form => {
    const state = stateFor(form);
    if (state.savePromise) return state.savePromise;
    if (!state.dirty) return Promise.resolve(true);

    clearTimeout(state.timer);
    const sentSnapshot = reviewFormSnapshot(form);
    state.savePromise = (async () => {
      setStatus(form, "Saving…", "saving");
      const response = await fetch(form.dataset.autosaveUrl, {
        method: "POST",
        body: new FormData(form),
        credentials: "same-origin",
        headers: { "X-Requested-With": "XMLHttpRequest" }
      });
      let payload = {};
      try { payload = await response.json(); } catch { /* fall through to the generic error */ }
      if (!response.ok) {
        if (response.status === 409 || payload.conflict) {
          setStatus(form, "Conflict · reload latest values", "error");
          return false;
        }
        throw new Error(payload.message || "The review could not be saved.");
      }

      const revision = form.querySelector("input[name=expectedRevision]");
      if (revision && payload.revision !== undefined) revision.value = payload.revision;
      state.savedSnapshot = sentSnapshot;
      state.dirty = reviewFormSnapshot(form) !== state.savedSnapshot;
      updateEffectiveValues(form, payload);
      updateRosterState(form.closest("[data-review-editor]")?.dataset.reviewKey || "", payload.trade);
      setStatus(form, state.dirty ? "Saving changes…" : "Saved", state.dirty ? "saving" : "saved");
      if (state.dirty) window.setTimeout(() => saveReview(form), 350);
      return true;
    })().catch(error => {
      console.error("Review autosave failed.", error);
      setStatus(form, error.message || "Save failed", "error");
      return false;
    }).finally(() => {
      state.savePromise = null;
    });
    return state.savePromise;
  };

  const scheduleSave = (form, immediate = false) => {
    const state = stateFor(form);
    state.dirty = reviewFormSnapshot(form) !== state.savedSnapshot;
    if (!state.dirty) return Promise.resolve(true);
    clearTimeout(state.timer);
    if (immediate) return saveReview(form);
    state.timer = window.setTimeout(() => saveReview(form), 450);
    setStatus(form, "Unsaved", "");
    return Promise.resolve(true);
  };

  const flushActive = async () => {
    const editor = editors.find(item => item.dataset.reviewKey === activeKey);
    const form = editor?.querySelector("[data-review-form]");
    if (!form) return true;
    const state = stateFor(form);
    clearTimeout(state.timer);
    if (state.savePromise) return await state.savePromise;
    return await saveReview(form);
  };

  const flushContext = async () => {
    if (!await flushActive()) return false;
    const flushDailyJournal = window.tradeFoundryFlushDailyJournal;
    if (flushDailyJournal && !await flushDailyJournal()) return false;
    const flushAttachmentCaptions = window.tradeFoundryFlushAttachmentCaptions;
    return flushAttachmentCaptions ? await flushAttachmentCaptions() : true;
  };

  const activate = async (key, options = {}) => {
    if (!key) return true;
    if (key === activeKey) {
      const current = editors.find(editor => editor.dataset.reviewKey === activeKey);
      if (current) void loadReviewChart(current.querySelector("[data-review-chart]"));
      updateUrl();
      return true;
    }
    if (!await flushActive()) return false;
    const flushAttachmentCaptions = window.tradeFoundryFlushAttachmentCaptions;
    if (flushAttachmentCaptions && !await flushAttachmentCaptions()) return false;
    const next = editors.find(editor => editor.dataset.reviewKey === key);
    if (!next) return false;
    activeKey = key;
    shell.dataset.activeKey = key;
    editors.forEach(editor => {
      const selected = editor === next;
      editor.classList.toggle("is-active", selected);
      editor.setAttribute("aria-hidden", String(!selected));
    });
    rosterItems.forEach(item => {
      const selected = item.dataset.reviewKey === key;
      item.classList.toggle("is-active", selected);
      item.setAttribute("aria-pressed", String(selected));
      if (selected && options.scrollRoster !== false) item.scrollIntoView({ block: "nearest", inline: "nearest" });
    });
    setSection(next, activeSection);
    updateUrl();
    void loadReviewChart(next.querySelector("[data-review-chart]"));
    if (options.focus) next.querySelector("[data-review-tab].is-active")?.focus({ preventScroll: true });
    return true;
  };

  editors.forEach(editor => {
    const form = editor.querySelector("[data-review-form]");
    if (!form) return;
    stateFor(form);
    form.querySelectorAll("input:not([type=hidden]), textarea, select").forEach(field => {
      field.addEventListener("input", () => scheduleSave(form));
      field.addEventListener("change", () => scheduleSave(form));
      field.addEventListener("blur", () => scheduleSave(form));
    });
    form.addEventListener("submit", event => {
      if (!shell.classList.contains("is-enhanced")) return;
      event.preventDefault();
      scheduleSave(form, true);
    });
    editor.querySelectorAll("[data-review-tab]").forEach(tab => tab.addEventListener("click", () => {
      activeSection = tab.dataset.reviewTab || "review";
      setSection(editor, activeSection);
      updateUrl();
    }));
    editor.querySelector("[data-review-prev]")?.addEventListener("click", () => {
      const index = Number(editor.dataset.reviewIndex || 1);
      const previous = editors[index - 2];
      if (previous) void activate(previous.dataset.reviewKey, { focus: true });
    });
    editor.querySelector("[data-review-next]")?.addEventListener("click", () => {
      const index = Number(editor.dataset.reviewIndex || 1);
      const next = editors[index];
      if (next) void activate(next.dataset.reviewKey, { focus: true });
    });
  });

  rosterItems.forEach(item => item.addEventListener("click", () => void activate(item.dataset.reviewKey, { focus: true })));
  editors.forEach(editor => setSection(editor, activeSection));
  const initial = editors.find(editor => editor.dataset.reviewKey === activeKey) || editors[0];
  if (initial && initial.dataset.reviewKey !== activeKey) activeKey = initial.dataset.reviewKey;
  void activate(activeKey, { scrollRoster: false });

  const dateForm = shell.querySelector(".tf-review-date-form");
  dateForm?.addEventListener("submit", event => {
    event.preventDefault();
    void flushContext().then(ok => { if (ok) dateForm.submit(); });
  });
  shell.addEventListener("click", event => {
    const link = event.target.closest("a");
    if (!link || !link.href) return;
    const url = new URL(link.href, window.location.href);
    if (url.origin !== window.location.origin) return;
    event.preventDefault();
    void flushContext().then(ok => { if (ok) window.location.href = link.href; });
  });
  window.addEventListener("beforeunload", event => {
    const editor = editors.find(item => item.dataset.reviewKey === activeKey);
    const form = editor?.querySelector("[data-review-form]");
    if (form && stateFor(form).dirty) {
      event.preventDefault();
      event.returnValue = "";
    }
  });

  if (shell.dataset.focus) {
    window.requestAnimationFrame(() => {
      initial?.scrollIntoView({ block: "start", behavior: "smooth" });
    });
  }
}

function reviewFormSnapshot(form) {
  const values = [];
  for (const [key, value] of new FormData(form).entries()) {
    if (key === "__RequestVerificationToken" || key === "expectedRevision" || value instanceof File) continue;
    values.push(`${key}=${String(value)}`);
  }
  return values.join("&");
}

function initializeReviewFocus() {
  const shell = document.querySelector("[data-focus]");
  const focus = shell?.dataset.focus;
  if (!shell || !focus) return;
  const trade = document.getElementById(`review-${focus}`);
  if (!trade) return;
  window.requestAnimationFrame(() => {
    trade.scrollIntoView({ block: "start", behavior: "smooth" });
    trade.querySelector("textarea, input:not([type=hidden]), select")?.focus({ preventScroll: true });
  });
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

  if (!window.Plotly) {
    node.dataset.plotlyInitialized = "true";
    showPlotlyError(node, new Error("Plotly.js failed to load."));
    return;
  }

  try {
    const payload = JSON.parse(node.dataset.plotlyChart || "{}");
    applyPlotlyPayload(node, payload);
  } catch (error) {
    showPlotlyError(node, error);
  }
}

function buildPlotlyConfig(payload) {
  const payloadConfig = payload.config || {};
  return {
    responsive: true,
    displaylogo: false,
    ...payloadConfig,
    modeBarButtonsToRemove: Array.from(new Set([...(payloadConfig.modeBarButtonsToRemove || []), "toImage"])),
    modeBarButtonsToAdd: [...(payloadConfig.modeBarButtonsToAdd || []), createChartExportButton()]
  };
}

function applyPlotlyPayload(node, payload) {
  if (!window.Plotly) {
    node.dataset.plotlyInitialized = "true";
    showPlotlyError(node, new Error("Plotly.js failed to load."));
    return Promise.resolve(false);
  }

  const wasInitialized = node.dataset.plotlyInitialized === "true";
  node.dataset.plotlyInitialized = "true";

  try {
    const config = buildPlotlyConfig(payload);
    const renderer = wasInitialized && typeof window.Plotly.react === "function"
      ? window.Plotly.react(node, payload.data || [], payload.layout || {}, config)
      : window.Plotly.newPlot(node, payload.data || [], payload.layout || {}, config);

    return Promise.resolve(renderer)
      .then(() => {
        node.removeAttribute("aria-busy");
        return true;
      })
      .catch(error => {
        showPlotlyError(node, error);
        return false;
      });
  } catch (error) {
    showPlotlyError(node, error);
    return Promise.resolve(false);
  }
}

function initializeBrokerComparison() {
  document.querySelectorAll("[data-broker-comparison]").forEach(root => {
    if (root.dataset.brokerComparisonInitialized === "true") return;

    const rowsHost = root.querySelector("[data-broker-rows]");
    const rowTemplate = root.querySelector("[data-broker-row-template]");
    const roundTurnsInput = root.querySelector("[data-broker-round-turns]");
    const contractsInput = root.querySelector("[data-broker-contracts]");
    const sidesOutput = root.querySelector("[data-broker-sides]");
    const costChart = root.querySelector("[data-broker-cost-chart]");
    const volumeChart = root.querySelector("[data-broker-volume-chart]");
    const emptyChart = root.querySelector("[data-broker-chart-empty]");
    const emptyVolumeChart = root.querySelector("[data-broker-volume-chart-empty]");
    if (!rowsHost || !roundTurnsInput || !contractsInput || !costChart || !volumeChart) return;

    root.dataset.brokerComparisonInitialized = "true";
    const stateKey = root.dataset.storageKey || "";
    const currency = root.dataset.currency || "USD";
    const variableFields = ["commission", "exchange", "nfa", "clearing"];
    const fixedFields = ["platform", "data", "other"];
    const allFields = ["name", ...variableFields, ...fixedFields];
    let moneyFormatter;
    try {
      moneyFormatter = new Intl.NumberFormat(undefined, {
        style: "currency",
        currency,
        minimumFractionDigits: 2,
        maximumFractionDigits: 2
      });
    } catch {
      moneyFormatter = new Intl.NumberFormat(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    }

    const numberValue = input => {
      const raw = String(input?.value || "").trim();
      if (!raw) return null;
      const value = Number(raw);
      return Number.isFinite(value) ? Math.max(0, value) : null;
    };
    const formatMoney = value => Number.isFinite(value) ? moneyFormatter.format(value) : "—";
    const formatNumber = value => Number.isFinite(value) ? value.toLocaleString(undefined, { maximumFractionDigits: 2 }) : "—";
    const formatSignedMoney = value => {
      if (!Number.isFinite(value)) return "—";
      if (value > 0.005) return "+" + formatMoney(value);
      return formatMoney(value);
    };
    const inputFor = (row, field) => row.querySelector(`[data-broker-field="${field}"]`);
    const setTone = (node, tone) => {
      if (!node) return;
      node.classList.toggle("positive", tone === "positive");
      node.classList.toggle("negative", tone === "negative");
    };

    function snapshotRow(row) {
      const values = {};
      let configured = false;
      allFields.forEach(field => {
        const input = inputFor(row, field);
        const raw = String(input?.value || "").trim();
        values[field] = raw;
        if (field !== "name" && raw !== "") configured = true;
      });

      const sideRate = variableFields.reduce((total, field) => total + (numberValue(inputFor(row, field)) || 0), 0);
      const fixedMonthly = fixedFields.reduce((total, field) => total + (numberValue(inputFor(row, field)) || 0), 0);
      return {
        element: row,
        name: values.name || "Unnamed broker",
        baseline: row.dataset.baseline === "true",
        configured,
        values,
        sideRate,
        fixedMonthly,
        monthly: sideRate * currentSides() + fixedMonthly
      };
    }

    function currentSides() {
      const roundTurns = numberValue(roundTurnsInput) || 0;
      const contracts = numberValue(contractsInput) || 0;
      return roundTurns * contracts * 2;
    }

    function applySavedRow(row, saved) {
      if (!saved || typeof saved !== "object") return;
      allFields.forEach(field => {
        const input = inputFor(row, field);
        if (!input || !Object.prototype.hasOwnProperty.call(saved, field)) return;
        input.value = String(saved[field] ?? "");
      });
    }

    function bindRow(row) {
      row.querySelectorAll("input").forEach(input => input.addEventListener("input", update));
      row.querySelector("[data-remove-broker]")?.addEventListener("click", () => {
        row.remove();
        update();
      });
    }

    function addRow(saved) {
      if (!rowTemplate?.content?.firstElementChild) return null;
      const row = rowTemplate.content.firstElementChild.cloneNode(true);
      rowsHost.appendChild(row);
      applySavedRow(row, saved);
      bindRow(row);
      return row;
    }

    function chartPayload(results) {
      const configured = results.filter(result => result.configured);
      if (!configured.length) {
        return {
          data: [],
          layout: { paper_bgcolor: "rgba(0,0,0,0)", plot_bgcolor: "rgba(0,0,0,0)", font: { color: "#c9d1d9" } },
          config: { responsive: true, displaylogo: false, modeBarButtonsToRemove: ["lasso2d", "select2d"] }
        };
      }

      const lowest = Math.min(...configured.map(result => result.monthly));
      return {
        data: [{
          type: "bar",
          orientation: "h",
          y: configured.map(result => result.name),
          x: configured.map(result => result.monthly),
          customdata: configured.map(result => result.monthly * 12),
          marker: { color: configured.map(result => result.baseline ? "#d29922" : Math.abs(result.monthly - lowest) < 0.005 ? "#3fb950" : "#58a6ff") },
          hovertemplate: "%{y}<br>%{x:,.2f} / month<br>%{customdata:,.2f} / year<extra></extra>"
        }],
        layout: {
          height: Math.max(260, configured.length * 54 + 80),
          margin: { l: 145, r: 24, t: 12, b: 58 },
          paper_bgcolor: "rgba(0,0,0,0)",
          plot_bgcolor: "rgba(0,0,0,0)",
          font: { color: "#c9d1d9", size: 11 },
          xaxis: { title: `${currency} / month`, gridcolor: "#30363d", zerolinecolor: "#30363d" },
          yaxis: { automargin: true, categoryorder: "total ascending" }
        },
        config: { responsive: true, displaylogo: false, modeBarButtonsToRemove: ["lasso2d", "select2d"] }
      };
    }

    function volumeChartPayload(results) {
      const configured = results.filter(result => result.configured);
      if (!configured.length) {
        return {
          data: [],
          layout: { paper_bgcolor: "rgba(0,0,0,0)", plot_bgcolor: "rgba(0,0,0,0)", font: { color: "#c9d1d9" } },
          config: { responsive: true, displaylogo: false, modeBarButtonsToRemove: ["lasso2d", "select2d"] }
        };
      }

      const currentRoundTurns = numberValue(roundTurnsInput) || 0;
      const contracts = numberValue(contractsInput) || 0;
      const maximum = Math.max(10, Math.ceil(Math.max(1, currentRoundTurns) * 2));
      const step = maximum / 12;
      const volumes = Array.from({ length: 13 }, (_, index) => Number((step * index).toFixed(2)));
      const alternativeColors = ["#58a6ff", "#bc8cff", "#39c5cf", "#3fb950", "#f778ba", "#f0883e"];
      const data = [];
      let alternativeIndex = 0;

      configured.forEach(result => {
        const color = result.baseline ? "#d29922" : alternativeColors[alternativeIndex++ % alternativeColors.length];
        const monthly = volumes.map(volume => result.sideRate * volume * contracts * 2 + result.fixedMonthly);
        const annual = monthly.map(value => value * 12);
        const line = { color, width: result.baseline ? 3 : 2 };
        const marker = { color, size: 5 };
        data.push({
          type: "scatter",
          mode: "lines+markers",
          name: result.name,
          legendgroup: result.name,
          x: volumes,
          y: monthly,
          customdata: annual,
          xaxis: "x",
          yaxis: "y",
          line,
          marker,
          hovertemplate: "%{fullData.name}<br>%{x:,.2f} round turns / month<br>%{y:,.2f} / month<br>%{customdata:,.2f} / year<extra></extra>"
        });
        data.push({
          type: "scatter",
          mode: "lines+markers",
          name: result.name,
          legendgroup: result.name,
          showlegend: false,
          x: volumes,
          y: annual,
          customdata: monthly,
          xaxis: "x2",
          yaxis: "y2",
          line,
          marker,
          hovertemplate: "%{fullData.name}<br>%{x:,.2f} round turns / month<br>%{y:,.2f} / year<br>%{customdata:,.2f} / month<extra></extra>"
        });
      });

      return {
        data,
        layout: {
          height: 560,
          margin: { l: 78, r: 24, t: 34, b: 104 },
          paper_bgcolor: "rgba(0,0,0,0)",
          plot_bgcolor: "rgba(0,0,0,0)",
          font: { color: "#c9d1d9", size: 11 },
          hovermode: "closest",
          legend: { orientation: "h", x: 0, y: -0.2 },
          xaxis: { domain: [0, 1], anchor: "y", showticklabels: false, gridcolor: "#30363d", zerolinecolor: "#30363d" },
          xaxis2: { domain: [0, 1], anchor: "y2", matches: "x", title: "Round turns / month", gridcolor: "#30363d", zerolinecolor: "#30363d" },
          yaxis: { domain: [0.56, 1], title: `${currency} / month`, gridcolor: "#30363d", zerolinecolor: "#30363d" },
          yaxis2: { domain: [0, 0.42], title: `${currency} / year`, gridcolor: "#30363d", zerolinecolor: "#30363d" },
          shapes: [{
            type: "line",
            xref: "x",
            yref: "paper",
            x0: currentRoundTurns,
            x1: currentRoundTurns,
            y0: 0,
            y1: 1,
            line: { color: "#d29922", width: 1, dash: "dot" }
          }],
          annotations: [{
            x: currentRoundTurns,
            y: 1.04,
            xref: "x",
            yref: "paper",
            text: `Current: ${currentRoundTurns.toLocaleString(undefined, { maximumFractionDigits: 2 })} round turns / month`,
            showarrow: false,
            font: { color: "#d29922", size: 10 }
          }]
        },
        config: { responsive: true, displaylogo: false, modeBarButtonsToRemove: ["lasso2d", "select2d"] }
      };
    }

    function updateChart(results) {
      const payload = chartPayload(results);
      const hasData = payload.data.length > 0;
      updatePlotlyChart(costChart, emptyChart, payload, hasData);
      updatePlotlyChart(volumeChart, emptyVolumeChart, volumeChartPayload(results), hasData);
    }

    function updatePlotlyChart(chartNode, emptyNode, payload, hasData) {
      chartNode.dataset.plotlyChart = JSON.stringify(payload);
      chartNode.hidden = !hasData;
      if (emptyNode) emptyNode.hidden = hasData;
      if (!hasData) {
        if (chartNode.dataset.plotlyInitialized === "true" && window.Plotly?.purge) {
          window.Plotly.purge(chartNode);
          chartNode.dataset.plotlyInitialized = "false";
        }
        return;
      }
      if (!window.Plotly) return;
      if (chartNode.dataset.plotlyInitialized === "true") applyPlotlyPayload(chartNode, payload);
      else renderPlotlyChart(chartNode);
    }

    function saveState() {
      if (!stateKey) return;
      try {
        const rows = Array.from(rowsHost.querySelectorAll("[data-broker-row]")).map(row => snapshotRow(row).values);
        window.localStorage.setItem(stateKey, JSON.stringify({
          roundTurns: roundTurnsInput.value,
          contracts: contractsInput.value,
          rows
        }));
      } catch {
        // Local storage is an enhancement; the calculator remains usable when it is blocked.
      }
    }

    function restoreState() {
      if (!stateKey) return;
      try {
        const raw = window.localStorage.getItem(stateKey);
        if (!raw) return;
        const saved = JSON.parse(raw);
        if (saved && Object.prototype.hasOwnProperty.call(saved, "roundTurns")) roundTurnsInput.value = String(saved.roundTurns ?? "");
        if (saved && Object.prototype.hasOwnProperty.call(saved, "contracts")) contractsInput.value = String(saved.contracts ?? "");
        if (!Array.isArray(saved?.rows)) return;

        const rows = Array.from(rowsHost.querySelectorAll("[data-broker-row]"));
        saved.rows.forEach((savedRow, index) => {
          const row = rows[index] || addRow(savedRow);
          if (row && rows[index]) applySavedRow(row, savedRow);
        });
      } catch {
        // Ignore malformed or unavailable browser storage and use the server defaults.
      }
    }

    function update() {
      const sides = currentSides();
      if (sidesOutput) sidesOutput.textContent = formatNumber(sides);
      const caption = root.querySelector("[data-broker-chart-caption]");
      if (caption) caption.textContent = `${formatNumber(sides)} contract sides / month`;
      const volumeCaption = root.querySelector("[data-broker-volume-chart-caption]");
      if (volumeCaption) volumeCaption.textContent = `${formatNumber(numberValue(roundTurnsInput) || 0)} round turns / month now · monthly and yearly projections`;

      const results = Array.from(rowsHost.querySelectorAll("[data-broker-row]")).map(snapshotRow);
      const configured = results.filter(result => result.configured);
      const baseline = results.find(result => result.baseline) || results[0];
      const lowest = configured.length ? configured.reduce((best, result) => result.monthly < best.monthly ? result : best) : null;
      const baselineConfigured = Boolean(baseline?.configured);

      results.forEach(result => {
        const row = result.element;
        const monthlyNode = row.querySelector("[data-row-monthly]");
        const annualNode = row.querySelector("[data-row-annual]");
        const variableNode = row.querySelector("[data-row-variable]");
        const savingsNode = row.querySelector("[data-row-savings]");
        const savingsNote = row.querySelector("[data-row-savings-note]");
        const statusNode = row.querySelector("[data-row-status]");
        row.classList.toggle("is-baseline", result.baseline);
        row.classList.toggle("is-best", Boolean(result.configured && lowest === result));

        if (!result.configured) {
          if (monthlyNode) monthlyNode.textContent = "—";
          if (annualNode) annualNode.textContent = "—";
          if (variableNode) variableNode.textContent = "";
          if (savingsNode) savingsNode.textContent = "—";
          if (savingsNote) savingsNote.textContent = "";
          if (statusNode) {
            statusNode.textContent = result.baseline ? "Baseline · add rates" : "Enter rates";
            setTone(statusNode, "");
          }
          return;
        }

        if (monthlyNode) monthlyNode.textContent = formatMoney(result.monthly);
        if (annualNode) annualNode.textContent = formatMoney(result.monthly * 12);
        if (variableNode) variableNode.textContent = `${formatMoney(result.sideRate)} / side`;
        if (statusNode) {
          if (result.baseline) statusNode.textContent = "Baseline";
          else if (!baselineConfigured) statusNode.textContent = "Rate entered";
          else if (lowest === result) statusNode.textContent = "Lowest cost";
          else statusNode.textContent = "Compared";
          setTone(statusNode, lowest === result && !result.baseline ? "positive" : "");
        }

        if (savingsNode) setTone(savingsNode, "");
        if (savingsNote) savingsNote.textContent = "";
        if (!result.baseline && baselineConfigured) {
          const savings = baseline.monthly - result.monthly;
          if (savingsNode) {
            savingsNode.textContent = formatSignedMoney(savings);
            setTone(savingsNode, savings > 0.005 ? "positive" : savings < -0.005 ? "negative" : "");
          }
          if (savingsNote) savingsNote.textContent = savings > 0.005 ? "less than current" : savings < -0.005 ? "more than current" : "same as current";
        }
      });

      const bestName = root.querySelector("[data-broker-best-name]");
      const bestCost = root.querySelector("[data-broker-best-cost]");
      const bestSavings = root.querySelector("[data-broker-best-savings]");
      const bestSavingsNote = root.querySelector("[data-broker-best-savings-note]");
      if (bestName) bestName.textContent = lowest?.name || "—";
      if (bestCost) bestCost.textContent = lowest ? `${formatMoney(lowest.monthly)} / month · ${formatMoney(lowest.monthly * 12)} / year` : "Enter comparable rates to rank brokers.";
      if (bestSavings) setTone(bestSavings, "");

      const alternatives = results.filter(result => !result.baseline && result.configured);
      const bestAlternative = alternatives.length ? alternatives.reduce((best, result) => result.monthly < best.monthly ? result : best) : null;
      if (bestSavings && baselineConfigured && bestAlternative) {
        const savings = baseline.monthly - bestAlternative.monthly;
        if (savings > 0.005) {
          bestSavings.textContent = formatMoney(savings);
          setTone(bestSavings, "positive");
          if (bestSavingsNote) bestSavingsNote.textContent = `${bestAlternative.name} could save that amount each month at this activity level.`;
        } else {
          bestSavings.textContent = "No savings";
          setTone(bestSavings, "negative");
          if (bestSavingsNote) bestSavingsNote.textContent = "Current setup is already cheaper than the entered alternatives.";
        }
      } else {
        if (bestSavings) bestSavings.textContent = "—";
        if (bestSavingsNote) bestSavingsNote.textContent = baselineConfigured ? "Add at least one comparable broker rate." : "Current setup rates are required for a savings estimate.";
      }

      updateChart(results);
      saveState();
    }

    roundTurnsInput.addEventListener("input", update);
    contractsInput.addEventListener("input", update);
    root.querySelector("[data-add-broker]")?.addEventListener("click", () => {
      const row = addRow();
      update();
      row?.querySelector('[data-broker-field="name"]')?.focus();
    });
    root.querySelector("[data-reset-broker-comparison]")?.addEventListener("click", () => {
      if (stateKey) {
        try { window.localStorage.removeItem(stateKey); } catch { /* ignore */ }
      }
      window.location.reload();
    });

    Array.from(rowsHost.querySelectorAll("[data-broker-row]")).forEach(bindRow);
    restoreState();
    update();
  });
}

function initializeEquityToggle() {
  document.querySelectorAll("[data-equity-toggle-form]").forEach(form => {
    if (form.dataset.equityToggleInitialized === "true") return;

    const dailyCheckbox = form.querySelector("input[name=daily]");
    const hideEmptyDaysCheckbox = form.querySelector("input[name=hideEmptyDays]");
    const card = form.closest(".tf-chart-card");
    const host = card?.querySelector("[data-equity-chart-host]");
    if (!dailyCheckbox || !host) return;

    form.dataset.equityToggleInitialized = "true";
    form.dataset.equityCurrentDaily = String(dailyCheckbox.checked);
    form.dataset.equityCurrentHideEmptyDays = String(hideEmptyDaysCheckbox?.checked || false);
    updateEquityToggleAvailability(form, dailyCheckbox, hideEmptyDaysCheckbox);
    form.addEventListener("submit", event => {
      event.preventDefault();
      updateEquityChart(form, dailyCheckbox, hideEmptyDaysCheckbox, host);
    });
  });
}

function updateEquityToggleAvailability(form, dailyCheckbox, hideEmptyDaysCheckbox) {
  if (!hideEmptyDaysCheckbox) return;

  hideEmptyDaysCheckbox.disabled = dailyCheckbox.disabled;
  hideEmptyDaysCheckbox.setAttribute("aria-label", "Hide dates without trades from the equity curve");
}

async function updateEquityChart(form, dailyCheckbox, hideEmptyDaysCheckbox, host) {
  if (form.dataset.equityToggleLoading === "true") return;

  const previousDaily = form.dataset.equityCurrentDaily === "true";
  const previousHideEmptyDays = form.dataset.equityCurrentHideEmptyDays === "true";
  const requestedDaily = dailyCheckbox.checked;
  const requestedHideEmptyDays = hideEmptyDaysCheckbox?.checked || false;
  const requestId = String((Number(form.dataset.equityToggleRequest || "0") || 0) + 1);
  form.dataset.equityToggleRequest = requestId;
  form.dataset.equityToggleLoading = "true";
  form.setAttribute("aria-busy", "true");
  dailyCheckbox.disabled = true;
  updateEquityToggleAvailability(form, dailyCheckbox, hideEmptyDaysCheckbox);

  try {
    const url = new URL(form.action || window.location.href, window.location.href);
    const journalId = form.querySelector("input[name=journalId]")?.value;
    if (!journalId) throw new Error("The equity chart journal is missing.");
    url.searchParams.set("handler", "EquityChart");
    url.searchParams.set("journalId", journalId);
    if (requestedDaily) url.searchParams.set("daily", "true");
    else url.searchParams.delete("daily");
    url.searchParams.set("hideEmptyDays", String(requestedHideEmptyDays));

    const response = await fetch(url, {
      headers: { Accept: "text/html" },
      credentials: "same-origin"
    });
    if (!response.ok) throw new Error(`Equity chart request failed (${response.status}).`);

    const html = await response.text();
    const fragment = new DOMParser().parseFromString(html, "text/html");
    const nextChart = fragment.querySelector("[data-plotly-chart]");
    const nextEmpty = fragment.querySelector(".chart-empty");
    if (!nextChart && !nextEmpty) throw new Error("The equity chart response was invalid.");
    if (requestId !== form.dataset.equityToggleRequest) return;

    if (nextChart) {
      const currentChart = host.querySelector(".tf-plotly-chart");
      const chart = currentChart || nextChart.cloneNode(true);
      if (!currentChart) host.replaceChildren(chart);

      chart.dataset.plotlyChart = nextChart.dataset.plotlyChart || "";
      const ariaLabel = nextChart.getAttribute("aria-label");
      if (ariaLabel) chart.setAttribute("aria-label", ariaLabel);
      const rendered = await applyPlotlyPayload(chart, JSON.parse(chart.dataset.plotlyChart || "{}"));
      if (!rendered) throw new Error("The equity chart could not be rendered.");
    } else {
      const currentChart = host.querySelector(".tf-plotly-chart");
      if (currentChart && currentChart.dataset.plotlyInitialized === "true" && window.Plotly?.purge) {
        window.Plotly.purge(currentChart);
      }
      host.replaceChildren(nextEmpty.cloneNode(true));
    }

    const card = form.closest(".tf-chart-card");
    const viewLabel = card?.querySelector("[data-equity-view-label]");
    if (viewLabel) viewLabel.textContent = requestedDaily ? "Daily" : "by trade";
    dailyCheckbox.setAttribute("aria-label", requestedDaily ? "Show equity curve by trade" : "Show Daily equity curve");
    form.dataset.equityCurrentDaily = String(requestedDaily);
    form.dataset.equityCurrentHideEmptyDays = String(requestedHideEmptyDays);

    const displayUrl = new URL(window.location.href);
    if (requestedDaily) displayUrl.searchParams.set("daily", "true");
    else displayUrl.searchParams.delete("daily");
    displayUrl.searchParams.set("hideEmptyDays", String(requestedHideEmptyDays));
    displayUrl.searchParams.delete("handler");
    window.history.replaceState(null, "", `${displayUrl.pathname}${displayUrl.search}${displayUrl.hash}`);
  } catch (error) {
    if (requestId === form.dataset.equityToggleRequest) {
      dailyCheckbox.checked = previousDaily;
      if (hideEmptyDaysCheckbox) hideEmptyDaysCheckbox.checked = previousHideEmptyDays;
      console.error("TradeFoundry equity chart update failed.", error);
    }
  } finally {
    if (requestId === form.dataset.equityToggleRequest) {
      form.dataset.equityToggleLoading = "false";
      form.removeAttribute("aria-busy");
      dailyCheckbox.disabled = false;
      updateEquityToggleAvailability(form, dailyCheckbox, hideEmptyDaysCheckbox);
    }
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
  const body = document.body;
  const isEnabled = key => {
    try {
      return window.localStorage.getItem(key) === "true";
    } catch {
      return false;
    }
  };
  return {
    owner: isEnabled("tradefoundry.chart-export.include-owner") ? (body.dataset.chartExportOwnerName || "").trim() : "",
    journal: isEnabled("tradefoundry.chart-export.include-journal") ? (body.dataset.chartExportJournalName || "").trim() : ""
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

function bindLightweightTimeframeForm(form, node) {
  const select = form?.querySelector("select[name=interval]");
  if (!form || !select || form.dataset.lightweightBound === "true") return;

  form.dataset.lightweightBound = "true";
  select.dataset.lastInterval = select.value || "";
  const update = () => {
    const state = node?._tradeFoundryLightweightState;
    if (state) void updateLightweightTimeframe(state, form);
    else void loadReviewChart(node, select.value);
  };
  select.addEventListener("change", update);
  form.addEventListener("submit", event => {
    event.preventDefault();
    update();
  });
}

async function loadReviewChart(node, requestedInterval) {
  if (!node?.matches("[data-review-chart]")) return;

  const card = node.closest("[data-review-chart-card]") || node.closest(".tf-chart-card");
  const form = card?.querySelector("[data-review-chart-form]") || card?.querySelector("[data-lightweight-timeframe-form]");
  const select = form?.querySelector("select[name=interval]");
  bindLightweightTimeframeForm(form, node);

  const reviewState = node._tradeFoundryReviewChartState || (node._tradeFoundryReviewChartState = { loading: false, loaded: false, interval: "", requestVersion: 0 });
  const pageInterval = new URL(window.location.href).searchParams.get("interval") || "";
  const targetInterval = requestedInterval || select?.value || pageInterval || "";
  if (node._tradeFoundryLightweightState || (reviewState.loaded && (!targetInterval || targetInterval === reviewState.interval))) return;
  if (reviewState.loading) return;

  const requestVersion = ++reviewState.requestVersion;
  reviewState.loading = true;
  const url = new URL(node.dataset.reviewChartUrl, window.location.href);
  url.searchParams.set("handler", "CandleChart");
  if (targetInterval) url.searchParams.set("interval", targetInterval);
  else url.searchParams.delete("interval");

  if (form) form.setAttribute("aria-busy", "true");
  if (select) select.disabled = true;
  node.setAttribute("aria-busy", "true");
  setReviewChartEmpty(card, true, "");
  setLightweightStatusForNode(node, "Loading chart…");

  try {
    const response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("Chart request returned " + response.status);
    const result = await response.json();
    if (requestVersion !== reviewState.requestVersion) return;

    updateReviewChartControls(card, result);
    const payload = result.payload;
    reviewState.interval = result.requestedInterval || result.resolvedInterval || targetInterval;
    node.dataset.lightweightChart = payload ? JSON.stringify(payload) : "";
    node.hidden = !payload;

    if (payload) {
      if (!node._tradeFoundryLightweightState) renderLightweightChart(node);
      const state = node._tradeFoundryLightweightState;
      if (state) {
        updateLightweightTimeframeUi(state, result);
        setLightweightStatus(state, "");
      } else {
        node.hidden = true;
        setReviewChartEmpty(card, false, "The chart could not be rendered.");
        setLightweightStatusForNode(node, "The chart could not be rendered.");
      }
    } else {
      setReviewChartEmpty(card, false, result.availabilityNote || "No matching OHLCV bars for this timeframe.");
      setLightweightStatusForNode(node, result.availabilityNote || "No matching OHLCV bars for this timeframe.");
      updateReviewChartSummary(card, result);
      updateReviewChartDecorations(card, false, result);
    }

    reviewState.loaded = true;
  } catch (error) {
    if (requestVersion !== reviewState.requestVersion) return;
    reviewState.loaded = false;
    node.hidden = true;
    setReviewChartEmpty(card, false, "The chart could not be loaded.");
    setLightweightStatusForNode(node, "The chart could not be loaded. Select a timeframe to retry.");
    console.error("TradeFoundry review candle chart failed to load.", error);
  } finally {
    if (requestVersion === reviewState.requestVersion) {
      reviewState.loading = false;
      node.removeAttribute("aria-busy");
      if (form) form.removeAttribute("aria-busy");
      if (select && !reviewState.loaded) select.disabled = false;
    }
  }
}

function updateReviewChartControls(card, result) {
  const select = card?.querySelector("[data-review-chart-interval]");
  if (!select) return;
  const intervals = Array.isArray(result.availableIntervals) ? result.availableIntervals.filter(Boolean) : [];
  const selectedInterval = result.requestedInterval || result.resolvedInterval || "";
  select.replaceChildren();

  if (!intervals.length) {
    select.add(new Option("No chart timeframes", ""));
    select.disabled = true;
    select.dataset.lastInterval = "";
    return;
  }

  intervals.forEach(interval => select.add(new Option(interval, interval)));
  select.value = intervals.includes(selectedInterval) ? selectedInterval : intervals[0];
  select.dataset.lastInterval = select.value;
  select.disabled = false;
}

function updateReviewChartSummary(card, result) {
  if (!card) return;
  const payload = result.payload;
  const count = payload?.bars?.length ?? result.barCount ?? 0;
  const interval = result.resolvedInterval || payload?.interval || "source";
  const timeZone = result.timeZone || payload?.timeZone || "UTC";
  const summary = card.querySelector("[data-lightweight-summary]");
  if (summary) summary.textContent = `${count} bars · ${interval} · ${timeZone}`;
  const availabilityNote = card.querySelector("[data-lightweight-availability-note]");
  if (availabilityNote) {
    availabilityNote.textContent = result.availabilityNote || "";
    availabilityNote.hidden = !result.availabilityNote;
  }
}

function updateReviewChartDecorations(card, hasPayload, result) {
  if (!card) return;
  card.querySelector("[data-lightweight-focus]")?.toggleAttribute("disabled", !hasPayload);
  card.querySelector("[data-lightweight-export]")?.toggleAttribute("disabled", !hasPayload);
  const attribution = card.querySelector("[data-lightweight-attribution]");
  if (attribution) attribution.hidden = !hasPayload;
  const defaultNote = card.querySelector("[data-lightweight-default-note]");
  if (defaultNote) defaultNote.hidden = result?.usedDefaultBarInterval !== true;
}

function setReviewChartEmpty(card, hidden, message) {
  const empty = card?.querySelector("[data-review-chart-empty]");
  if (!empty) return;
  empty.textContent = message || "";
  empty.hidden = hidden;
}

function setLightweightStatusForNode(node, message) {
  const status = node?.parentElement?.querySelector("[data-lightweight-status]");
  if (status) status.textContent = message;
}

function initializeLightweightCharts() {
  const chartNodes = Array.from(document.querySelectorAll("[data-lightweight-chart]"));
  if (!chartNodes.length) return;

  chartNodes.forEach(node => {
    node.setAttribute("aria-busy", "true");
    renderLightweightChart(node);
  });
}

function renderLightweightChart(node) {
  if (node.dataset.lightweightInitialized === "true") return;
  node.dataset.lightweightInitialized = "true";

  if (!window.LightweightCharts) {
    showLightweightError(node, new Error("Lightweight Charts failed to load."));
    return;
  }

  try {
    const payload = JSON.parse(node.dataset.lightweightChart || "{}");
    const library = window.LightweightCharts;
    const chart = library.createChart(node, {
      autoSize: true,
      height: 420,
      attributionLogo: true,
      layout: {
        background: { type: "solid", color: "#0d1117" },
        textColor: "#8b949e",
        panes: {
          separatorColor: "#30363d",
          separatorHoverColor: "#58a6ff",
          enableResize: true
        }
      },
      grid: {
        vertLines: { color: "#21262d" },
        horzLines: { color: "#21262d" }
      },
      rightPriceScale: {
        borderColor: "#30363d",
        scaleMargins: { top: 0.08, bottom: 0.04 }
      },
      timeScale: {
        borderColor: "#30363d",
        rightOffset: 5,
        barSpacing: 7,
        timeVisible: true,
        secondsVisible: false,
        tickMarkFormatter: value => formatLightweightTime(value, payload.timeZone)
      },
      crosshair: {
        vertLine: { color: "#8b949e", width: 1, style: 3, labelBackgroundColor: "#30363d" },
        horzLine: { color: "#8b949e", width: 1, style: 3, labelBackgroundColor: "#30363d" }
      },
      handleScroll: {
        mouseWheel: false,
        pressedMouseMove: true,
        horzTouchDrag: true,
        vertTouchDrag: false
      },
      handleScale: {
        mouseWheel: true,
        pinch: true,
        axisPressedMouseMove: { time: true, price: true },
        axisDoubleClickReset: true
      },
      localization: { timeFormatter: value => formatLightweightTime(value, payload.timeZone) }
    });

    const candles = chart.addSeries(library.CandlestickSeries, {
      upColor: "#3fb950",
      downColor: "#f85149",
      borderVisible: false,
      wickUpColor: "#3fb950",
      wickDownColor: "#f85149",
      lastValueVisible: false,
      priceLineVisible: false
    });
    const volume = chart.addSeries(library.HistogramSeries, {
      priceFormat: { type: "volume" },
      priceScaleId: "volume",
      base: 0,
      lastValueVisible: false,
      priceLineVisible: false
    }, 1);
    const pathOutline = chart.addSeries(library.LineSeries, {
      color: "#000000",
      lineWidth: 4,
      lineStyle: 1,
      crosshairMarkerVisible: false,
      lastValueVisible: false,
      priceLineVisible: false
    });
    const path = chart.addSeries(library.LineSeries, {
      color: "#ffffff",
      lineWidth: 2,
      lineStyle: 1,
      crosshairMarkerVisible: false,
      lastValueVisible: false,
      priceLineVisible: false
    });

    const state = {
      node,
      bars: new Map(),
      beforeDone: false,
      afterDone: false,
      inFlight: { before: false, after: false },
      failed: null,
      lastRangeKey: "",
      historyReady: false,
      dataVersion: 0,
      chart,
      candles,
      volume,
      pathOutline,
      path,
      payload
    };

    normalizeLightweightBars(payload.bars || []).forEach(bar => state.bars.set(bar.time, bar));
    const tradePath = normalizeLightweightPath(payload.trade?.path || []);
    pathOutline.setData(tradePath);
    path.setData(tradePath);
    applyLightweightData(state, false);
    addLightweightMarkers(state);
    chart.timeScale().setVisibleRange({ from: payload.focus.from, to: payload.focus.to });
    if (chart.panes().length > 1) chart.panes()[1].setHeight(96);

    chart.timeScale().subscribeVisibleLogicalRangeChange(range => {
      if (!state.historyReady) return;
      if (!range) return;
      const rangeKey = Math.floor(range.from) + ":" + Math.ceil(range.to);
      if (state.failed && state.failed.rangeKey !== rangeKey) state.failed = null;
      state.lastRangeKey = rangeKey;
      const values = sortedLightweightBars(state);
      if (!values.length) return;
      const edgeThreshold = Math.min(20, Math.max(5, Math.floor(values.length * 0.2)));
      if (range.from < edgeThreshold && !state.beforeDone && !state.inFlight.before && (!state.failed || state.failed.direction !== "before"))
        loadLightweightBars(state, "before", rangeKey);
      if (range.to > values.length - 1 - edgeThreshold && !state.afterDone && !state.inFlight.after && (!state.failed || state.failed.direction !== "after"))
        loadLightweightBars(state, "after", rangeKey);
    });

    window.requestAnimationFrame(() => window.requestAnimationFrame(() => { state.historyReady = true; }));

    const card = node.closest(".tf-chart-card");
    card?.querySelector("[data-lightweight-focus]")?.addEventListener("click", () => focusLightweightTrade(state));
    card?.querySelector("[data-lightweight-export]")?.addEventListener("click", () => downloadLightweightChart(node, state));
    bindLightweightTimeframeForm(card?.querySelector("[data-lightweight-timeframe-form]"), node);
    node._tradeFoundryLightweightState = state;
    node.removeAttribute("aria-busy");
  } catch (error) {
    showLightweightError(node, error);
  }
}

function normalizeLightweightBars(bars) {
  return (Array.isArray(bars) ? bars : []).map(bar => ({
    time: Number(bar.time),
    open: Number(bar.open),
    high: Number(bar.high),
    low: Number(bar.low),
    close: Number(bar.close),
    volume: Number(bar.volume || 0)
  })).filter(bar => Number.isFinite(bar.time) && Number.isFinite(bar.open) && Number.isFinite(bar.high) && Number.isFinite(bar.low) && Number.isFinite(bar.close));
}

function normalizeLightweightPath(path) {
  return (Array.isArray(path) ? path : []).map(point => ({ time: Number(point.time), value: Number(point.value) })).filter(point => Number.isFinite(point.time) && Number.isFinite(point.value)).sort((left, right) => left.time - right.time);
}

function sortedLightweightBars(state) {
  return Array.from(state.bars.values()).sort((left, right) => left.time - right.time);
}

function applyLightweightData(state, preserveRange, previousFirst) {
  const previousRange = preserveRange ? state.chart.timeScale().getVisibleLogicalRange() : null;
  const oldFirst = preserveRange ? previousFirst ?? sortedLightweightBars(state)[0]?.time : undefined;
  const values = sortedLightweightBars(state);
  state.candles.setData(values.map(bar => ({ time: bar.time, open: bar.open, high: bar.high, low: bar.low, close: bar.close })));
  state.volume.setData(values.map(bar => ({ time: bar.time, value: bar.volume, color: bar.close >= bar.open ? "#3fb95088" : "#f8514988" })));
  if (!previousRange || oldFirst === undefined) return;
  const addedBefore = Math.max(0, values.findIndex(bar => bar.time === oldFirst));
  state.chart.timeScale().setVisibleLogicalRange({ from: previousRange.from + addedBefore, to: previousRange.to + addedBefore });
}

async function updateLightweightTimeframe(state, form) {
  const select = form.querySelector("select[name=interval]");
  if (!select?.value) return;

  const requestedInterval = select.value;
  const previousInterval = select.dataset.lastInterval || requestedInterval;
  const requestVersion = ++state.dataVersion;
  const url = new URL(form.action, window.location.href);
  url.searchParams.set("handler", "CandleChart");
  url.searchParams.set("interval", requestedInterval);
  state.historyReady = false;
  state.inFlight.before = false;
  state.inFlight.after = false;
  state.failed = null;
  select.disabled = true;
  form.setAttribute("aria-busy", "true");
  state.node.setAttribute("aria-busy", "true");
  setLightweightStatus(state, `Loading ${select.options[select.selectedIndex]?.textContent?.trim() || requestedInterval} candles…`);

  try {
    const response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("Timeframe request returned " + response.status);
    const result = await response.json();
    if (requestVersion !== state.dataVersion) return;

    applyLightweightTimeframeData(state, result);
    updateLightweightTimeframeUi(state, result);
    select.dataset.lastInterval = result.requestedInterval || requestedInterval;

    const pageUrl = new URL(window.location.href);
    pageUrl.searchParams.set("interval", result.requestedInterval || requestedInterval);
    window.history.replaceState({}, "", pageUrl);
    setLightweightStatus(state, result.payload ? "" : result.availabilityNote || "No matching OHLCV bars for this timeframe.");
  } catch (error) {
    if (requestVersion !== state.dataVersion) return;
    select.value = previousInterval;
    state.historyReady = true;
    setLightweightStatus(state, "The selected timeframe could not be loaded.");
    console.error("TradeFoundry candle timeframe failed to load.", error);
  } finally {
    if (requestVersion === state.dataVersion) {
      select.disabled = false;
      form.removeAttribute("aria-busy");
      state.node.removeAttribute("aria-busy");
    }
  }
}

function applyLightweightTimeframeData(state, result) {
  const payload = result.payload;
  state.historyReady = false;
  state.payload = payload;
  state.bars.clear();
  state.beforeDone = !payload;
  state.afterDone = !payload;
  state.inFlight.before = false;
  state.inFlight.after = false;
  state.failed = null;
  state.lastRangeKey = "";

  if (!payload) {
    state.candles.setData([]);
    state.volume.setData([]);
    state.pathOutline.setData([]);
    state.path.setData([]);
    state.markers?.setMarkers?.([]);
    state.historyReady = true;
    return;
  }

  normalizeLightweightBars(payload.bars || []).forEach(bar => state.bars.set(bar.time, bar));
  const tradePath = normalizeLightweightPath(payload.trade?.path || []);
  state.pathOutline.setData(tradePath);
  state.path.setData(tradePath);
  applyLightweightData(state, false);
  addLightweightMarkers(state);
  state.chart.timeScale().setVisibleRange({ from: payload.focus.from, to: payload.focus.to });
  state.historyReady = true;
}

function updateLightweightTimeframeUi(state, result) {
  const card = state.node.closest(".tf-chart-card");
  const payload = result.payload;
  if (card?.matches("[data-review-chart-card]")) {
    state.node.hidden = !payload;
    setReviewChartEmpty(card, Boolean(payload), payload ? "" : result.availabilityNote || "No matching OHLCV bars for this timeframe.");
  }
  updateReviewChartSummary(card, result);

  const defaultNote = card?.querySelector("[data-lightweight-default-note]");
  if (defaultNote) defaultNote.hidden = result.usedDefaultBarInterval !== true;
  const availabilityNote = card?.querySelector("[data-lightweight-availability-note]");
  if (availabilityNote) {
    availabilityNote.textContent = result.availabilityNote || "";
    availabilityNote.hidden = !result.availabilityNote;
  }
  updateReviewChartDecorations(card, Boolean(payload), result);
  const count = payload?.bars?.length ?? result.barCount ?? 0;
  const interval = result.resolvedInterval || payload?.interval || "source";
  state.node.setAttribute("aria-label", `Candlestick chart for ${payload?.symbol || state.payload?.symbol || "trade"}, ${interval}, ${count} bars`);
}

function addLightweightMarkers(state) {
  if (typeof window.LightweightCharts.createSeriesMarkers !== "function") return;
  const trade = state.payload.trade || {};
  const markers = [];
  const addEvent = event => {
    if (!event) return;
    const time = Number(event.time);
    if (!Number.isFinite(time)) return;

    markers.push({
      id: event.label + "-bar",
      time,
      position: event.barPosition,
      color: event.color,
      shape: event.shape,
      text: event.label,
      size: 1
    });
  };
  addEvent(trade.entry);
  addEvent(trade.exit);
  markers.sort((left, right) => left.time - right.time || String(left.id).localeCompare(String(right.id)));
  if (state.markers?.setMarkers) state.markers.setMarkers(markers);
  else state.markers = window.LightweightCharts.createSeriesMarkers(state.candles, markers);
}

function focusLightweightTrade(state) {
  if (!state.payload?.focus) return;
  state.chart.timeScale().setVisibleRange({ from: state.payload.focus.from, to: state.payload.focus.to });
  state.candles.priceScale().applyOptions({ autoScale: true });
  setLightweightStatus(state, "");
}

async function loadLightweightBars(state, direction, rangeKey) {
  const values = sortedLightweightBars(state);
  if (!state.payload || !values.length) return;
  const dataVersion = state.dataVersion;
  const cursorTime = direction === "before" ? values[0].time : values[values.length - 1].time;
  const cursor = new Date(cursorTime * 1000).toISOString();
  state.inFlight[direction] = true;
  setLightweightStatus(state, "Loading more candles…");
  try {
    const url = state.payload.historyUrl + "&direction=" + encodeURIComponent(direction) + "&cursor=" + encodeURIComponent(cursor) + "&limit=300";
    const response = await fetch(url, { credentials: "same-origin", headers: { Accept: "application/json" } });
    if (!response.ok) throw new Error("History request returned " + response.status);
    const result = await response.json();
    if (dataVersion !== state.dataVersion) return;
    const newBars = normalizeLightweightBars(result.bars || []);
    const previousFirst = values[0].time;
    const previousSize = state.bars.size;
    newBars.forEach(bar => state.bars.set(bar.time, bar));
    const addedCount = state.bars.size - previousSize;
    const exhausted = !result.hasMore || newBars.length === 0 || addedCount === 0;
    if (direction === "before") state.beforeDone = exhausted;
    else state.afterDone = exhausted;
    state.failed = null;
    applyLightweightData(state, true, previousFirst);
    setLightweightStatus(state, "");
  } catch (error) {
    if (dataVersion !== state.dataVersion) return;
    state.failed = { direction, rangeKey };
    setLightweightStatus(state, "More candles could not be loaded. Move the chart to retry.");
    console.error("TradeFoundry candle history failed to load.", error);
  } finally {
    if (dataVersion === state.dataVersion) state.inFlight[direction] = false;
  }
}

function setLightweightStatus(state, message) {
  const status = state.node?.parentElement?.querySelector("[data-lightweight-status]");
  if (status) status.textContent = message;
}

function formatLightweightTime(value, timeZone) {
  const date = new Date(Number(value) * 1000);
  try {
    return new Intl.DateTimeFormat("en-US", { timeZone: timeZone || "UTC", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false }).format(date);
  } catch {
    return new Intl.DateTimeFormat("en-US", { month: "short", day: "numeric", hour: "2-digit", minute: "2-digit", hour12: false }).format(date);
  }
}

function downloadLightweightChart(node, state) {
  try {
    const chartCanvas = state.chart.takeScreenshot(true, false);
    const card = node.closest(".tf-chart-card");
    const title = card?.querySelector(".tf-card-title")?.textContent?.replace(/\s+/g, " ").trim() || node.getAttribute("aria-label") || "TradeFoundry chart";
    const context = getChartExportContext();
    const contextLines = [];
    if (context.owner) contextLines.push("Owner: " + context.owner);
    if (context.journal) contextLines.push("Journal: " + context.journal);
    const scale = 2;
    const width = Math.max(chartCanvas.width, 480 * scale);
    const headerHeight = (44 + contextLines.length * 15) * scale;
    const output = document.createElement("canvas");
    output.width = width;
    output.height = chartCanvas.height + headerHeight;
    const context2d = output.getContext("2d");
    context2d.fillStyle = "#0d1117";
    context2d.fillRect(0, 0, output.width, output.height);
    context2d.fillStyle = "#c9d1d9";
    context2d.font = (13 * scale) + "px Segoe UI, system-ui, sans-serif";
    context2d.fillText(title, 16 * scale, 19 * scale);
    context2d.fillStyle = "#8b949e";
    context2d.font = (10 * scale) + "px Segoe UI, system-ui, sans-serif";
    contextLines.forEach((line, index) => context2d.fillText(line, 16 * scale, (35 + index * 15) * scale));
    context2d.drawImage(chartCanvas, 0, headerHeight);
    const link = document.createElement("a");
    link.download = chartExportFilename(title);
    link.href = output.toDataURL("image/png");
    link.click();
  } catch (error) {
    setLightweightStatus(state, "The chart image could not be downloaded.");
    console.error("TradeFoundry Lightweight Charts PNG export failed.", error);
  }
}

function showLightweightError(node, error) {
  console.error("Lightweight Chart failed to render.", error);
  node.removeAttribute("aria-busy");
  const fallback = document.createElement("div");
  fallback.className = "chart-empty";
  fallback.setAttribute("role", "status");
  fallback.textContent = "This chart could not be rendered.";
  node.replaceChildren(fallback);
}
