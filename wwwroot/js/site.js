document.addEventListener("DOMContentLoaded", () => {
  initializeChartExportOptions();
  initializePlotlyCharts();
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
  initializeMarkdownEditors();

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

function initializeMarkdownEditors() {
  document.querySelectorAll("[data-markdown-editor]").forEach(editor => {
    const input = editor.querySelector("[data-markdown-input]");
    const preview = editor.querySelector("[data-markdown-preview]");
    if (!input || !preview) return;

    const updatePreview = () => {
      preview.innerHTML = renderMarkdownPreview(input.value);
    };

    editor.querySelectorAll("[data-markdown-action]").forEach(button => {
      button.addEventListener("click", () => {
        input.focus();
        applyMarkdownAction(input, button.dataset.markdownAction || "");
        updatePreview();
      });
    });

    editor.querySelectorAll("[data-markdown-tab]").forEach(button => {
      button.addEventListener("click", () => {
        const previewMode = button.dataset.markdownTab === "preview";
        editor.querySelectorAll("[data-markdown-tab]").forEach(tab => tab.setAttribute("aria-pressed", String(tab === button)));
        input.hidden = previewMode;
        preview.hidden = !previewMode;
        if (previewMode) updatePreview();
      });
    });

    input.addEventListener("input", updatePreview);
  });
}

function initializeDailyJournal() {
  const form = document.querySelector("[data-daily-journal-form]");
  const input = form?.querySelector("[name=dailyJournalText]");
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

function applyMarkdownAction(input, action) {
  const start = input.selectionStart;
  const end = input.selectionEnd;
  const selected = input.value.slice(start, end);
  const fallback = action === "link" ? "link text" : action === "code" ? "code" : "text";
  const value = selected || fallback;
  let before = "";
  let after = "";

  switch (action) {
    case "bold":
      before = "**";
      after = "**";
      break;
    case "italic":
      before = "_";
      after = "_";
      break;
    case "heading":
      before = "### ";
      break;
    case "link":
      before = "[";
      after = "](https://)";
      break;
    case "unordered-list":
      before = "- ";
      break;
    case "ordered-list":
      before = "1. ";
      break;
    case "quote":
      before = "> ";
      break;
    case "code":
      before = "`";
      after = "`";
      break;
    default:
      return;
  }

  input.setRangeText(`${before}${value}${after}`, start, end, "select");
  const cursor = start + before.length + value.length + after.length;
  if (!selected && action === "link") input.setSelectionRange(start + before.length + value.length + 2, cursor - 1);
  else input.setSelectionRange(cursor, cursor);
  input.dispatchEvent(new Event("input", { bubbles: true }));
}

function renderMarkdownPreview(markdown) {
  const lines = String(markdown || "").replace(/\r\n?/g, "\n").split("\n");
  const output = [];
  let inCode = false;
  let codeLanguage = "";

  for (const line of lines) {
    const fence = line.match(/^\s*```\s*([\w-]*)\s*$/);
    if (fence) {
      if (inCode) output.push("</code></pre>");
      else {
        codeLanguage = fence[1] || "";
        output.push(`<pre><code${codeLanguage ? ` class="language-${escapeMarkdownHtml(codeLanguage)}"` : ""}>`);
      }
      inCode = !inCode;
      continue;
    }
    if (inCode) {
      output.push(escapeMarkdownHtml(line) + "\n");
      continue;
    }

    const heading = line.match(/^\s*(#{1,6})\s+(.+?)\s*#*\s*$/);
    if (heading) {
      output.push(`<h${heading[1].length}>${renderMarkdownInline(heading[2])}</h${heading[1].length}>`);
      continue;
    }
    if (/^\s*(---+|\*\*\*+)\s*$/.test(line)) {
      output.push("<hr>");
      continue;
    }
    const quote = line.match(/^\s*>\s?(.*)$/);
    if (quote) {
      output.push(`<blockquote>${renderMarkdownInline(quote[1])}</blockquote>`);
      continue;
    }
    const unordered = line.match(/^\s*[-*+]\s+(.+)$/);
    if (unordered) {
      output.push(`<ul><li>${renderMarkdownInline(unordered[1])}</li></ul>`);
      continue;
    }
    const ordered = line.match(/^\s*\d+\.\s+(.+)$/);
    if (ordered) {
      output.push(`<ol><li>${renderMarkdownInline(ordered[1])}</li></ol>`);
      continue;
    }
    if (!line.trim()) continue;
    output.push(`<p>${renderMarkdownInline(line)}</p>`);
  }

  if (inCode) output.push("</code></pre>");
  return output.join("") || '<p class="text-secondary">Nothing to preview yet.</p>';
}

function renderMarkdownInline(value) {
  let result = escapeMarkdownHtml(value);
  result = result.replace(/\[([^\]]+)\]\(((?:https?:\/\/|mailto:|\/|#)[^\s)]+)\)/g, '<a href="$2" target="_blank" rel="noreferrer">$1</a>');
  result = result.replace(/`([^`]+)`/g, "<code>$1</code>");
  result = result.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
  result = result.replace(/__([^_]+)__/g, "<strong>$1</strong>");
  result = result.replace(/(?<!\*)\*([^*]+)\*(?!\*)/g, "<em>$1</em>");
  result = result.replace(/(?<!_)_([^_]+)_(?!_)/g, "<em>$1</em>");
  return result;
}

function escapeMarkdownHtml(value) {
  return String(value || "").replace(/[&<>\"']/g, character => ({
    "&": "&amp;",
    "<": "&lt;",
    ">": "&gt;",
    '"': "&quot;",
    "'": "&#39;"
  })[character]);
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
    ["Monte Carlo simulated paths", "Resample the observed trades into simulated paths to estimate the range of plausible outcomes and the uncertainty around the recorded sequence."],
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
        ? `${day.date}: ${formatPnl(day.netPnl)}, ${day.tradeCount} ${day.tradeCount === 1 ? "trade" : "trades"}`
        : `${day.date}: no completed trades`);
      if (day.tradeCount === 0) dayNode.classList.add("tf-pnl-day-no-trades");
      dayNode.append(
        element("span", "tf-pnl-day-number", String(day.day)),
        element("strong", "tf-pnl-day-value", day.tradeCount > 0 ? formatPnl(day.netPnl) : "—"),
        element("small", "tf-pnl-day-count", day.tradeCount > 0 ? `${day.tradeCount} ${day.tradeCount === 1 ? "trade" : "trades"}` : "no trades")
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

function initializeEquityToggle() {
  document.querySelectorAll("[data-equity-toggle-form]").forEach(form => {
    if (form.dataset.equityToggleInitialized === "true") return;

    const checkbox = form.querySelector("input[name=daily]");
    const card = form.closest(".tf-chart-card");
    const host = card?.querySelector("[data-equity-chart-host]");
    if (!checkbox || !host) return;

    form.dataset.equityToggleInitialized = "true";
    form.dataset.equityCurrentDaily = String(checkbox.checked);
    form.addEventListener("submit", event => {
      event.preventDefault();
      updateEquityChart(form, checkbox, host);
    });
  });
}

async function updateEquityChart(form, checkbox, host) {
  if (form.dataset.equityToggleLoading === "true") return;

  const previousDaily = form.dataset.equityCurrentDaily === "true";
  const requestedDaily = checkbox.checked;
  const requestId = String((Number(form.dataset.equityToggleRequest || "0") || 0) + 1);
  form.dataset.equityToggleRequest = requestId;
  form.dataset.equityToggleLoading = "true";
  form.setAttribute("aria-busy", "true");
  checkbox.disabled = true;

  try {
    const url = new URL(form.action || window.location.href, window.location.href);
    const journalId = form.querySelector("input[name=journalId]")?.value;
    if (!journalId) throw new Error("The equity chart journal is missing.");
    url.searchParams.set("handler", "EquityChart");
    url.searchParams.set("journalId", journalId);
    if (requestedDaily) url.searchParams.set("daily", "true");
    else url.searchParams.delete("daily");

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
    checkbox.setAttribute("aria-label", requestedDaily ? "Show equity curve by trade" : "Show Daily equity curve");
    form.dataset.equityCurrentDaily = String(requestedDaily);

    const displayUrl = new URL(window.location.href);
    if (requestedDaily) displayUrl.searchParams.set("daily", "true");
    else displayUrl.searchParams.delete("daily");
    displayUrl.searchParams.delete("handler");
    window.history.replaceState(null, "", `${displayUrl.pathname}${displayUrl.search}${displayUrl.hash}`);
  } catch (error) {
    if (requestId === form.dataset.equityToggleRequest) {
      checkbox.checked = previousDaily;
      console.error("TradeFoundry equity chart update failed.", error);
    }
  } finally {
    if (requestId === form.dataset.equityToggleRequest) {
      form.dataset.equityToggleLoading = "false";
      form.removeAttribute("aria-busy");
      checkbox.disabled = false;
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
  const options = document.querySelector("[data-chart-export-options]");
  const body = document.body;
  return {
    owner: options?.querySelector("[data-chart-export-owner]")?.checked ? (body.dataset.chartExportOwnerName || "").trim() : "",
    journal: options?.querySelector("[data-chart-export-journal]")?.checked ? (body.dataset.chartExportJournalName || "").trim() : ""
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
