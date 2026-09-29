import {CodeNode} from "@lexical/code";
import {createEmptyHistoryState, registerHistory} from "@lexical/history";
import {$toggleLink, LinkNode} from "@lexical/link";
import {
  INSERT_ORDERED_LIST_COMMAND,
  INSERT_UNORDERED_LIST_COMMAND,
  ListItemNode,
  ListNode,
  registerList
} from "@lexical/list";
import {registerMarkdownShortcuts, TRANSFORMERS} from "@lexical/markdown";
import {$setBlocksType} from "@lexical/selection";
import {$createHeadingNode, $createQuoteNode, HeadingNode, QuoteNode, registerRichText} from "@lexical/rich-text";
import {$applyNodeReplacement, $createParagraphNode, $getNodeByKey, $getRoot, $getSelection, $isRangeSelection, $setSelection, createEditor, ElementNode, FORMAT_TEXT_COMMAND, REDO_COMMAND, UNDO_COMMAND} from "lexical";

class DailyJournalImageNode extends ElementNode {
  static getType() {
    return "daily-journal-image";
  }

  static clone(node) {
    return new DailyJournalImageNode(node.__attachmentId, node.__src, node.__altText, node.__width, node.__key);
  }

  static importJSON(serializedNode) {
    return $createDailyJournalImageNode(serializedNode.attachmentId, serializedNode.src, serializedNode.altText, serializedNode.width)
      .updateFromJSON(serializedNode);
  }

  static importDOM() {
    return {
      img: domNode => {
        if (!(domNode instanceof HTMLImageElement)) return null;
        const src = normalizeDailyJournalImageUrl(domNode.getAttribute("src") || "");
        const attachmentId = new URL(src, window.location.href).searchParams.get("imageId") || "";
        if (!src || !attachmentId) return null;
        return {
          conversion: () => ({ node: $createDailyJournalImageNode(attachmentId, src, domNode.alt || "Pasted image", parseImageWidth(domNode.style.width)) }),
          priority: 2
        };
      }
    };
  }

  constructor(attachmentId, src, altText, width = 0, key) {
    super(key);
    this.__attachmentId = String(attachmentId || "");
    this.__src = normalizeDailyJournalImageUrl(src);
    this.__altText = String(altText || "Pasted image");
    this.__width = normalizeImageWidth(width);
  }

  createDOM() {
    const figure = document.createElement("figure");
    figure.className = "tf-lexical-image-node";
    figure.contentEditable = "false";
    figure.dataset.lexicalNodeKey = this.__key;
    figure.dataset.dailyJournalImageId = this.__attachmentId;
    const image = document.createElement("img");
    image.src = this.__src;
    image.alt = this.__altText;
    image.loading = "lazy";
    image.draggable = false;
    if (this.__width > 0) image.style.width = `${this.__width}px`;
    const removeButton = document.createElement("button");
    removeButton.type = "button";
    removeButton.className = "tf-lexical-image-remove";
    removeButton.dataset.dailyJournalImageRemove = "";
    removeButton.setAttribute("aria-label", `Remove image ${this.__altText}`);
    removeButton.textContent = "×";
    const resizeButton = document.createElement("button");
    resizeButton.type = "button";
    resizeButton.className = "tf-lexical-image-resize";
    resizeButton.dataset.dailyJournalImageResize = "";
    resizeButton.dataset.dailyJournalImageWidth = this.__width > 0 ? String(this.__width) : "";
    resizeButton.setAttribute("aria-label", `Resize image ${this.__altText}. Drag or use the left and right arrow keys.`);
    resizeButton.title = "Drag or use the left and right arrow keys to resize";
    resizeButton.textContent = "⤡";
    figure.append(image, removeButton, resizeButton);
    return figure;
  }

  updateDOM(previousNode, dom) {
    const image = dom.querySelector("img");
    if (!image) return true;
    if (previousNode.__src !== this.__src) image.src = this.__src;
    if (previousNode.__altText !== this.__altText) image.alt = this.__altText;
    if (previousNode.__width !== this.__width) {
      if (this.__width > 0) image.style.width = `${this.__width}px`;
      else image.style.removeProperty("width");
      const resizeButton = dom.querySelector("[data-daily-journal-image-resize]");
      if (resizeButton) resizeButton.dataset.dailyJournalImageWidth = this.__width > 0 ? String(this.__width) : "";
    }
    return false;
  }

  exportDOM() {
    const image = document.createElement("img");
    image.src = this.__src;
    image.alt = this.__altText;
    image.dataset.dailyJournalImageId = this.__attachmentId;
    if (this.__width > 0) image.style.width = `${this.__width}px`;
    return { element: image };
  }

  exportJSON() {
    return {
      ...super.exportJSON(),
      type: "daily-journal-image",
      version: 1,
      attachmentId: this.__attachmentId,
      src: this.__src,
      altText: this.__altText,
      width: this.__width
    };
  }

  setWidth(width) {
    const writable = this.getWritable();
    writable.__width = normalizeImageWidth(width);
    return writable;
  }

  getTextContent() {
    return this.__altText ? `[Image: ${this.__altText}]` : "[Image]";
  }

  isInline() {
    return false;
  }

  canBeEmpty() {
    return true;
  }
}

const EDITOR_NODES = [HeadingNode, QuoteNode, ListNode, ListItemNode, LinkNode, CodeNode, DailyJournalImageNode];

function $createDailyJournalImageNode(attachmentId, src, altText, width = 0) {
  return $applyNodeReplacement(new DailyJournalImageNode(attachmentId, src, altText, width));
}

function parseImageWidth(value) {
  const match = String(value || "").match(/^\s*(\d+(?:\.\d+)?)px\s*$/i);
  return match ? normalizeImageWidth(Number(match[1])) : 0;
}

function normalizeImageWidth(value) {
  const width = Number(value);
  return Number.isFinite(width) && width > 0 ? Math.round(width) : 0;
}

function normalizeDailyJournalImageUrl(value) {
  if (!value) return "";
  try {
    const url = new URL(value, window.location.href);
    if (url.origin !== window.location.origin
      || url.searchParams.get("handler")?.toLowerCase() !== "image"
      || !url.pathname.startsWith("/journal/")
      || !url.pathname.endsWith("/daily-journal")
      || !url.searchParams.get("date")
      || !/^[0-9a-f-]{36}$/i.test(url.searchParams.get("imageId") || "")) return "";
    return `${url.pathname}${url.search}`;
  } catch {
    return "";
  }
}

export function initializeLexicalEditors(scope = document) {
  const containers = [];
  if (scope instanceof Element && scope.matches("[data-lexical-editor]")) containers.push(scope);
  scope.querySelectorAll?.("[data-lexical-editor]").forEach(container => containers.push(container));
  containers.forEach(initializeEditor);
}

function initializeEditor(container) {
  if (container.dataset.lexicalInitialized === "true") return;

  const source = container.parentElement?.querySelector("[data-lexical-source]") ??
    container.querySelector("[data-lexical-source]");
  const root = container.querySelector("[data-lexical-root]");
  if (!source || !root) return;

  const limitStatus = container.querySelector("[data-lexical-limit-status]");
  const maxLength = Number(container.dataset.maxLength || source.maxLength || 0);
  const editor = createEditor({
    namespace: "TradeFoundryJournal",
    nodes: EDITOR_NODES,
    theme: {
      heading: {h1: "tf-lexical-heading", h2: "tf-lexical-heading", h3: "tf-lexical-heading"},
      quote: "tf-lexical-quote",
      list: {ul: "tf-lexical-list", ol: "tf-lexical-list", listitem: "tf-lexical-list-item"},
      code: "tf-lexical-code",
      text: {
        bold: "tf-lexical-bold",
        italic: "tf-lexical-italic",
        strikethrough: "tf-lexical-strikethrough",
        code: "tf-lexical-inline-code"
      }
    },
    onError(error) {
      console.error("Lexical editor error.", error);
    }
  });

  editor.setRootElement(root);
  registerRichText(editor);
  registerList(editor);
  registerHistory(editor, createEmptyHistoryState(), 1000);
  registerMarkdownShortcuts(editor, TRANSFORMERS);
  restoreLexicalState(editor, source.value);
  ensureTrailingParagraphAfterImage(editor);

  let lastValidState = editor.getEditorState();
  let lastDocumentJson = JSON.stringify(lastValidState.toJSON().root);

  editor.registerUpdateListener(({editorState}) => {
    const nextState = editorState.toJSON();
    const nextDocumentJson = JSON.stringify(nextState.root);
    if (nextDocumentJson === lastDocumentJson) return;

    const serialized = JSON.stringify(nextState);
    if (maxLength > 0 && serialized.length > maxLength) {
      if (limitStatus) limitStatus.textContent = "The editor has reached its maximum size.";
      editor.setEditorState(lastValidState);
      return;
    }

    lastValidState = editorState;
    lastDocumentJson = nextDocumentJson;
    if (limitStatus) limitStatus.textContent = "";
    if (source.value === serialized) return;

    source.value = serialized;
    source.dispatchEvent(new Event("input", {bubbles: true}));
  });

  container.querySelectorAll("[data-lexical-action]").forEach(button => {
    button.addEventListener("mousedown", event => event.preventDefault());
    button.addEventListener("click", () => runToolbarAction(editor, button.dataset.lexicalAction || "", limitStatus));
  });

  container.addEventListener("click", event => {
    const target = event.target instanceof Element ? event.target : null;
    const selectedFigure = target?.closest(".tf-lexical-image-node");
    root.querySelectorAll(".tf-lexical-image-node.is-selected").forEach(figure => {
      if (figure !== selectedFigure) figure.classList.remove("is-selected");
    });
    if (selectedFigure && root.contains(selectedFigure)) selectedFigure.classList.add("is-selected");
  });

  root.addEventListener("blur", () => {
    source.dispatchEvent(new Event("blur"));
  }, true);
  root.addEventListener("click", event => {
    const removeButton = event.target.closest("[data-daily-journal-image-remove]");
    if (!removeButton) return;
    event.preventDefault();
    event.stopPropagation();
    const nodeKey = removeButton.closest("[data-lexical-node-key]")?.dataset.lexicalNodeKey;
    if (!nodeKey) return;
    editor.update(() => {
      const node = $getNodeByKey(nodeKey);
      if (node?.getType() === DailyJournalImageNode.getType()) node.remove();
    });
  });
  registerDailyJournalImageResize(editor, root);
  registerDailyJournalImagePaste(editor, container, root, limitStatus, maxLength);

  container.dataset.lexicalInitialized = "true";
  source.hidden = true;
  container.hidden = false;
  root.setAttribute("contenteditable", "true");
}

function registerDailyJournalImagePaste(editor, container, root, limitStatus, maxLength) {
  const uploadUrl = container.dataset.imageUploadUrl;
  const form = container.closest("form[data-daily-journal-form]");
  const status = container.querySelector("[data-lexical-image-status]");
  if (!uploadUrl || !form || !status) return;

  const supportedTypes = new Set(["image/png", "image/jpeg", "image/jpg", "image/webp"]);
  const setStatus = text => {
    status.textContent = text;
  };

  root.addEventListener("paste", async event => {
    const items = Array.from(event.clipboardData?.items || []).filter(item => item.kind === "file" && String(item.type || "").toLowerCase().startsWith("image/"));
    if (items.length === 0) return;
    event.preventDefault();

    const files = [];
    for (const item of items) {
      const type = String(item.type || "").toLowerCase();
      if (!supportedTypes.has(type)) {
        setStatus("Only PNG, JPEG, and WebP images are supported.");
        return;
      }
      const blob = item.getAsFile();
      if (!blob) continue;
      const canonicalType = type === "image/jpg" ? "image/jpeg" : type;
      const extension = canonicalType === "image/jpeg" ? "jpg" : canonicalType.slice("image/".length);
      const fileName = blob.name?.trim() || `pasted-image.${extension}`;
      const file = new File([blob], fileName, { type: canonicalType, lastModified: Date.now() });
      if (file.size <= 0 || file.size > 10 * 1024 * 1024) {
        setStatus("Images must be between 1 byte and 10 MB.");
        return;
      }
      files.push(file);
    }
    if (files.length === 0) {
      setStatus("The clipboard image could not be read. Try copying it again.");
      return;
    }

    const currentLength = JSON.stringify(editor.getEditorState().toJSON()).length;
    if (currentLength + files.length * 1024 > maxLength) {
      setStatus("There is not enough room in this journal entry for another image.");
      if (limitStatus) limitStatus.textContent = "The editor has reached its maximum size.";
      return;
    }

    const token = form.querySelector('input[name="__RequestVerificationToken"]');
    const date = form.closest("[data-daily-journal-entry]")?.dataset.entryDate || "";
    if (!token?.value || !date) {
      setStatus("The image upload could not be started. Reload this journal and try again.");
      return;
    }

    const savedSelection = editor.getEditorState().read(() => {
      const selection = $getSelection();
      return $isRangeSelection(selection) ? selection.clone() : null;
    });

    const uploadedImages = [];
    let uploadError = "";
    for (let index = 0; index < files.length; index++) {
      const file = files[index];
      setStatus(`Uploading image ${index + 1} of ${files.length}…`);
      const body = new FormData();
      body.append("image", file, file.name);
      body.append("date", date);
      body.append(token.name, token.value);
      try {
        const response = await fetch(uploadUrl, {
          method: "POST",
          body,
          credentials: "same-origin",
          headers: { "X-Requested-With": "XMLHttpRequest" }
        });
        let result = {};
        try { result = await response.json(); } catch { /* The error status below is enough. */ }
        if (!response.ok || !result.saved || !result.image?.id || !result.image?.src) {
          throw new Error(result.message || "The image could not be uploaded.");
        }
        const src = normalizeDailyJournalImageUrl(result.image.src);
        if (!src) throw new Error("The uploaded image URL was not valid.");
        uploadedImages.push({ ...result.image, src });
      } catch (error) {
        uploadError = error instanceof Error ? error.message : "The image could not be uploaded.";
        break;
      }
    }

    if (uploadedImages.length > 0) {
      editor.update(() => {
        if (savedSelection) $setSelection(savedSelection);
        const selection = $getSelection();
        const nodes = uploadedImages.map(image => $createDailyJournalImageNode(image.id, image.src, image.altText));
        if ($isRangeSelection(selection)) selection.insertNodes(nodes);
        else $getRoot().append(...nodes);
        placeCaretAfterImages(nodes);
      });
      root.focus({ preventScroll: true });
    }

    if (uploadError) {
      setStatus(uploadedImages.length > 0
        ? `${uploadedImages.length} image${uploadedImages.length === 1 ? "" : "s"} added. ${uploadError}`
        : uploadError);
    } else {
      setStatus(`${uploadedImages.length} image${uploadedImages.length === 1 ? "" : "s"} added to the journal.`);
    }
  });
}

function ensureTrailingParagraphAfterImage(editor) {
  editor.update(() => {
    const root = $getRoot();
    const last = root.getLastChild();
    if (last?.getType() !== DailyJournalImageNode.getType()) return;
    const paragraph = $createParagraphNode();
    last.insertAfter(paragraph);
    paragraph.selectStart();
  }, { discrete: true });
}

function placeCaretAfterImages(nodes) {
  const lastImage = nodes[nodes.length - 1];
  if (!lastImage) return;
  const next = lastImage.getNextSibling();
  if (next?.getType() === "paragraph") {
    next.selectStart();
    return;
  }

  const paragraph = $createParagraphNode();
  lastImage.insertAfter(paragraph);
  paragraph.selectStart();
}

function registerDailyJournalImageResize(editor, root) {
  const minWidth = 80;
  const maxWidth = 1600;
  let activeResize = null;

  const clampWidth = width => {
    const editorWidth = root.clientWidth || maxWidth;
    return Math.max(minWidth, Math.min(maxWidth, editorWidth, Math.round(width)));
  };

  const getImageParts = target => {
    const handle = target.closest("[data-daily-journal-image-resize]");
    const figure = handle?.closest("[data-lexical-node-key]");
    const image = figure?.querySelector("img");
    const nodeKey = figure?.dataset.lexicalNodeKey;
    return handle && image && nodeKey ? { handle, image, nodeKey } : null;
  };

  const previewWidth = (parts, width) => {
    const nextWidth = clampWidth(width);
    parts.image.style.width = `${nextWidth}px`;
    parts.handle.dataset.dailyJournalImageWidth = String(nextWidth);
    parts.handle.setAttribute("aria-label", `Resize image to ${nextWidth} pixels wide. Drag or use the left and right arrow keys.`);
    return nextWidth;
  };

  const saveWidth = (nodeKey, width) => {
    editor.update(() => {
      const node = $getNodeByKey(nodeKey);
      if (node?.getType() === DailyJournalImageNode.getType()) node.setWidth(width);
    });
  };

  root.addEventListener("pointerdown", event => {
    const parts = getImageParts(event.target);
    if (!parts) return;
    const startWidth = parts.image.getBoundingClientRect().width || parts.image.naturalWidth || minWidth;
    activeResize = {
      ...parts,
      pointerId: event.pointerId,
      startX: event.clientX,
      startWidth,
      width: startWidth
    };
    event.preventDefault();
    event.stopPropagation();
    parts.handle.focus({ preventScroll: true });
    try { parts.handle.setPointerCapture(event.pointerId); } catch { /* Pointer capture is optional for older browsers. */ }
  });

  root.addEventListener("pointermove", event => {
    if (!activeResize || activeResize.pointerId !== event.pointerId) return;
    event.preventDefault();
    activeResize.width = previewWidth(activeResize, activeResize.startWidth + event.clientX - activeResize.startX);
  });

  const finishResize = event => {
    if (!activeResize || activeResize.pointerId !== event.pointerId) return;
    const finished = activeResize;
    activeResize = null;
    saveWidth(finished.nodeKey, finished.width);
    root.focus({ preventScroll: true });
  };
  root.addEventListener("pointerup", finishResize);
  root.addEventListener("pointercancel", finishResize);

  root.addEventListener("keydown", event => {
    if (event.key !== "ArrowLeft" && event.key !== "ArrowRight") return;
    const parts = getImageParts(event.target);
    if (!parts) return;
    event.preventDefault();
    const storedWidth = Number(parts.handle.dataset.dailyJournalImageWidth);
    const currentWidth = storedWidth > 0 ? storedWidth : (parts.image.getBoundingClientRect().width || parts.image.naturalWidth || minWidth);
    const step = event.shiftKey ? 50 : 10;
    const nextWidth = previewWidth(parts, currentWidth + (event.key === "ArrowRight" ? step : -step));
    saveWidth(parts.nodeKey, nextWidth);
  });
}

function restoreLexicalState(editor, value) {
  const serialized = String(value || "").trim();
  if (!serialized) return;

  try {
    const state = editor.parseEditorState(serialized);
    editor.setEditorState(state);
  } catch {
    // Previous Markdown notes are intentionally not converted to Lexical state.
  }
}

function runToolbarAction(editor, action, status) {
  switch (action) {
    case "bold":
    case "italic":
    case "strikethrough":
    case "code":
      editor.dispatchCommand(FORMAT_TEXT_COMMAND, action);
      break;
    case "heading":
      editor.update(() => {
        const selection = $getSelection();
        if ($isRangeSelection(selection)) $setBlocksType(selection, () => $createHeadingNode("h3"));
      });
      break;
    case "quote":
      editor.update(() => {
        const selection = $getSelection();
        if ($isRangeSelection(selection)) $setBlocksType(selection, () => $createQuoteNode());
      });
      break;
    case "unordered-list":
      editor.dispatchCommand(INSERT_UNORDERED_LIST_COMMAND);
      break;
    case "ordered-list":
      editor.dispatchCommand(INSERT_ORDERED_LIST_COMMAND);
      break;
    case "link": {
      const entered = window.prompt("Link URL", "https://");
      if (entered === null) return;
      const url = normalizeLink(entered);
      if (!url) {
        if (status) status.textContent = "Use an http, https, mailto, or relative link.";
        return;
      }
      editor.update(() => $toggleLink(url));
      break;
    }
    case "undo":
      editor.dispatchCommand(UNDO_COMMAND);
      break;
    case "redo":
      editor.dispatchCommand(REDO_COMMAND);
      break;
  }
}

function normalizeLink(value) {
  const trimmed = String(value || "").trim();
  if (!trimmed) return null;
  const candidate = /^[a-z][a-z0-9+.-]*:/i.test(trimmed) ? trimmed : "https://" + trimmed;
  return /^(https?:\/\/|mailto:|\/|#)/i.test(candidate) ? candidate : null;
}
