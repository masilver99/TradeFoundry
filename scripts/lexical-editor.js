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
import {$getSelection, $isRangeSelection, createEditor, FORMAT_TEXT_COMMAND, REDO_COMMAND, UNDO_COMMAND} from "lexical";

const EDITOR_NODES = [HeadingNode, QuoteNode, ListNode, ListItemNode, LinkNode, CodeNode];

export function initializeLexicalEditors() {
  document.querySelectorAll("[data-lexical-editor]").forEach(initializeEditor);
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

  root.addEventListener("blur", () => {
    source.dispatchEvent(new Event("blur"));
  }, true);

  container.dataset.lexicalInitialized = "true";
  source.hidden = true;
  container.hidden = false;
  root.setAttribute("contenteditable", "true");
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
