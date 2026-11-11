import {
  ArrowMarker,
  CalloutMarker,
  CaptionFrameMarker,
  CheckImageMarker,
  CoverMarker,
  CurveMarker,
  CustomImageMarker,
  EllipseFrameMarker,
  EllipseMarker,
  FrameMarker,
  FreehandMarker,
  HighlighterMarker,
  HighlightMarker,
  ImageMarkerEditor,
  LineMarker,
  MarkerArea,
  MeasurementMarker,
  PolygonMarker,
  Renderer,
  TextMarker,
  XImageMarker
} from "@markerjs/markerjs3";

// --- Tool catalogue -------------------------------------------------------
//
// colorMode says which marker property the palette drives:
//   stroke  - outline/line color          fill - solid shape color
//   text    - text color                  boxed - box color with contrasting text
//   none    - the color is fixed (emoji)
// props lists the extra settings shown for the tool.

const EMOJI = ["👍", "👎", "🎯", "⚠️", "💰", "🚀", "🔥", "⭐", "🤔", "😬", "✅", "❌"];
const EMOJI_FONT = "'Segoe UI Emoji','Apple Color Emoji','Noto Color Emoji',sans-serif";
// The marker's visual group sets fill="transparent" and the marker stroke, which the
// text would otherwise inherit (invisible glyph, outlined emoji), so set both here.
const emojiSvg = emoji => `<svg xmlns="http://www.w3.org/2000/svg" width="24" height="24" viewBox="0 0 24 24"><text x="12" y="13" fill="#000000" stroke="none" font-size="20" font-family="${EMOJI_FONT}" text-anchor="middle" dominant-baseline="central">${emoji}</text></svg>`;
const [EMOJI_SVG_PREFIX, EMOJI_SVG_SUFFIX] = emojiSvg("\u0000").split("\u0000");
// Only emoji code points (plus joiners, variation selectors, keycaps, tags and skin
// tones) may appear, so a stored emoji can never smuggle markup into the SVG.
const EMOJI_PATTERN = /^(?:\p{Extended_Pictographic}|\p{Emoji_Presentation}|\p{Emoji_Component}|‍|️|⃣)+$/u;
const isEmoji = value => typeof value === "string" && value.length > 0 && value.length <= 32 && EMOJI_PATTERN.test(value);

/** Returns the emoji inside an annotator emoji SVG, or null if it is anything else. */
function emojiFromSvg(svgString) {
  if (typeof svgString !== "string" || !svgString.startsWith(EMOJI_SVG_PREFIX) || !svgString.endsWith(EMOJI_SVG_SUFFIX)) return null;
  const emoji = svgString.slice(EMOJI_SVG_PREFIX.length, svgString.length - EMOJI_SVG_SUFFIX.length);
  return isEmoji(emoji) ? emoji : null;
}

// Extra search words so trading terms find the obvious emoji.
const EMOJI_SYNONYMS = {
  money: "💰💵💲🤑💸", profit: "💰📈🤑✅🏆", win: "🏆✅🥇🎉", loss: "📉💸❌🩸😭", up: "📈⬆️🚀🔼", down: "📉⬇️🔽",
  bull: "🐂📈", bear: "🐻📉", stop: "🛑⛔✋", warning: "⚠️🚨", danger: "⚠️🚨☠️", target: "🎯", idea: "💡🤔", time: "⏰⌛⏳🕐",
  news: "📰📢", question: "❓🤔", good: "👍✅👌", bad: "👎❌", entry: "➡️🟢", exit: "🚪🔴", wait: "⏳✋", patience: "⏳🧘",
  mistake: "🤦❌😬", fomo: "😱🏃", tilt: "😡🤬", calm: "🧘😌", luck: "🍀🎲", gamble: "🎲🎰", trend: "📈📉", chart: "📈📉📊"
};

const EMOJI_DATA_URL = (() => {
  // Resolve relative to this bundle so the data file is found under any base path.
  try { return new URL("../lib/unicode-emoji/emoji.json", document.currentScript?.src || new URL("/js/", location.href)).href; }
  catch { return "/lib/unicode-emoji/emoji.json"; }
})();
let emojiDataPromise = null;
function loadEmojiData() {
  emojiDataPromise ??= fetch(EMOJI_DATA_URL, {credentials: "same-origin"})
    .then(response => {
      if (!response.ok) throw new Error("The emoji list could not be loaded.");
      return response.json();
    })
    .then(data => ({
      groups: Array.isArray(data.groups) ? data.groups.map(String) : [],
      emoji: (Array.isArray(data.emoji) ? data.emoji : [])
        .filter(entry => Array.isArray(entry) && isEmoji(entry[0]))
        .map(([emoji, name, group]) => ({emoji, name: String(name || ""), search: String(name || "").toLowerCase(), group: Number(group) || 0}))
    }))
    .catch(error => {
      emojiDataPromise = null;
      throw error;
    });
  return emojiDataPromise;
}

// marker.js 3.10 builds the check/X visual before restoring the saved stroke color,
// so restored (and rasterized) stamps fall back to their default color. These keep
// the same typeName and re-apply the color once the state is restored.
class ColoredCheckImageMarker extends CheckImageMarker {
  restoreState(state) {
    super.restoreState(state);
    if (state?.strokeColor) this.strokeColor = state.strokeColor;
  }
}
class ColoredXImageMarker extends XImageMarker {
  restoreState(state) {
    super.restoreState(state);
    if (state?.strokeColor) this.strokeColor = state.strokeColor;
  }
}
const PATCHED_IMAGE_MARKERS = [ColoredCheckImageMarker, ColoredXImageMarker];

const ICON_PATHS = {
  select: '<path d="M5 3l11 8-5 1 3 6-2 1-3-6-4 4z" fill="currentColor" stroke="none"/>',
  frame: '<rect x="3" y="5" width="14" height="10" rx="1.5"/>',
  ellipseFrame: '<ellipse cx="10" cy="10" rx="7" ry="5"/>',
  highlight: '<rect x="3" y="6" width="14" height="8" fill="currentColor" opacity=".45" stroke="none"/>',
  cover: '<rect x="3" y="5" width="14" height="10" rx="1.5" fill="currentColor"/>',
  ellipse: '<ellipse cx="10" cy="10" rx="7" ry="5" fill="currentColor"/>',
  arrow: '<path d="M3 10h13M12 6l4 4-4 4"/>',
  doubleArrow: '<path d="M3 10h14M7 6l-4 4 4 4M13 6l4 4-4 4"/>',
  line: '<path d="M3 15L17 5"/>',
  curve: '<path d="M3 15C6 3 14 3 17 15"/>',
  measure: '<path d="M3 10h14M3 6v8M17 6v8"/>',
  text: '<path d="M4 5h12M10 5v11M7 16h6"/>',
  callout: '<path d="M3 4h14v9H9l-4 3v-3H3z"/>',
  caption: '<rect x="3" y="4" width="14" height="12" rx="1"/><path d="M3 8h14"/>',
  freehand: '<path d="M3 14c2-6 4-8 5-5s2 6 4 3 3-7 5-6"/>',
  highlighter: '<path d="M3 13c3-3 8-3 14-6" stroke-width="5" opacity=".5"/>',
  polygon: '<path d="M10 3l7 5-3 8H6L3 8z"/>',
  check: '<path d="M4 10l4 4 8-8"/>',
  cross: '<path d="M5 5l10 10M15 5L5 15"/>'
};
const icon = name => `<svg class="tf-annotator-icon" viewBox="0 0 20 20" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">${ICON_PATHS[name] || ""}</svg>`;

const TOOLS = {
  frame: {group: "shapes", label: "Box", type: FrameMarker, key: "b", colorMode: "stroke", props: ["width", "dash", "fill"]},
  ellipseFrame: {group: "shapes", label: "Ellipse", type: EllipseFrameMarker, key: "e", colorMode: "stroke", props: ["width", "dash", "fill"]},
  highlight: {group: "shapes", label: "Highlight", type: HighlightMarker, key: "h", colorMode: "fill", props: [], defaultColor: "#f59e0b"},
  cover: {group: "shapes", label: "Solid box", type: CoverMarker, colorMode: "fill", props: []},
  ellipse: {group: "shapes", label: "Solid ellipse", type: EllipseMarker, colorMode: "fill", props: []},
  arrow: {group: "lines", label: "Arrow", type: ArrowMarker, key: "a", colorMode: "stroke", props: ["width", "dash"], arrowType: "end"},
  doubleArrow: {group: "lines", label: "Double arrow", type: ArrowMarker, colorMode: "stroke", props: ["width", "dash"], arrowType: "both"},
  line: {group: "lines", label: "Line", type: LineMarker, key: "l", colorMode: "stroke", props: ["width", "dash"]},
  curve: {group: "lines", label: "Curve", type: CurveMarker, colorMode: "stroke", props: ["width", "dash"]},
  measure: {group: "lines", label: "Measure", type: MeasurementMarker, key: "m", colorMode: "stroke", props: ["width", "dash"]},
  text: {group: "text", label: "Text", type: TextMarker, key: "t", colorMode: "text", props: ["font"]},
  callout: {group: "text", label: "Callout", type: CalloutMarker, colorMode: "boxed", props: ["font"]},
  caption: {group: "text", label: "Captioned box", type: CaptionFrameMarker, colorMode: "boxed", props: ["width", "font"]},
  freehand: {group: "draw", label: "Pen", type: FreehandMarker, key: "d", colorMode: "stroke", props: ["width"]},
  highlighter: {group: "draw", label: "Highlighter", type: HighlighterMarker, colorMode: "stroke", props: [], defaultColor: "#f59e0b"},
  polygon: {group: "draw", label: "Polygon (double-click to finish)", type: PolygonMarker, key: "p", colorMode: "stroke", props: ["width", "dash", "fill"]},
  check: {group: "stamps", label: "Check mark", type: ColoredCheckImageMarker, colorMode: "stroke", props: []},
  cross: {group: "stamps", label: "X mark", type: ColoredXImageMarker, colorMode: "stroke", props: []},
  ...Object.fromEntries(EMOJI.map(emoji => [`emoji:${emoji}`, emojiTool(emoji)]))
};

function emojiTool(emoji, label = emoji) {
  return {group: "stamps", label, type: CustomImageMarker, colorMode: "none", props: [], emoji};
}

/** Returns the tool id for any valid emoji, adding a tool entry on first use. */
function ensureEmojiTool(emoji, label) {
  if (!isEmoji(emoji)) return null;
  const id = `emoji:${emoji}`;
  TOOLS[id] ??= emojiTool(emoji, label || emoji);
  return id;
}

const GROUPS = [
  {id: "shapes", label: "Shapes"},
  {id: "lines", label: "Arrows and lines"},
  {id: "text", label: "Text"},
  {id: "draw", label: "Freehand"},
  {id: "stamps", label: "Stamps and emoji"}
];

// Saved state may only contain the marker types offered above.
const ALLOWED_TYPE_NAMES = new Set(Object.values(TOOLS).map(tool => tool.type.typeName));
const TYPE_TOOL = new Map();
Object.entries(TOOLS).forEach(([id, tool]) => { if (!TYPE_TOOL.has(tool.type.typeName)) TYPE_TOOL.set(tool.type.typeName, id); });
const ALLOWED_FILTERS = new Set(["url(#dropShadow)", "url(#outline)", "url(#glow)"]);

const COLORS = [
  {value: "#22c55e", label: "Green"},
  {value: "#ef4444", label: "Red"},
  {value: "#f59e0b", label: "Amber"},
  {value: "#3b82f6", label: "Blue"},
  {value: "#a855f7", label: "Purple"},
  {value: "#ffffff", label: "White"},
  {value: "#111827", label: "Black"}
];
const isHexColor = value => typeof value === "string" && /^#[0-9a-f]{6}$/i.test(value);
const MAX_CUSTOM_COLORS = 6;
const STROKE_WIDTHS = [2, 4, 6, 10];
const DASHES = [{value: "", label: "Solid"}, {value: "8 6", label: "Dashed"}, {value: "2 5", label: "Dotted"}];
const FONT_SIZES = [{value: 1, label: "S"}, {value: 1.5, label: "M"}, {value: 2.25, label: "L"}, {value: 3.25, label: "XL"}];
const MAX_MARKERS = 200;
const MAX_IMAGE_BYTES = 10 * 1024 * 1024;
const PREFERENCES_KEY = "tradefoundry.annotator";

export function sanitizeAnnotationState(value) {
  if (!value || typeof value !== "object") return null;
  const width = Number(value.width);
  const height = Number(value.height);
  if (!(width > 0) || !(height > 0) || !Array.isArray(value.markers)) return null;
  const markers = value.markers
    .filter(marker => marker && typeof marker === "object" && ALLOWED_TYPE_NAMES.has(marker.typeName))
    .map(marker => {
      // Image markers parse svgString into the page, so arbitrary markup or remote
      // image URLs must never be restored. Check/X stamps keep their built-in SVG
      // and custom images must be an annotator emoji SVG, rebuilt from the emoji alone.
      const {imageSrc, svgString, imageType, ...rest} = marker;
      if (marker.typeName !== CustomImageMarker.typeName) return rest;
      const emoji = emojiFromSvg(svgString);
      return emoji ? {...rest, svgString: emojiSvg(emoji), imageType: "svg"} : null;
    })
    .filter(Boolean)
    .slice(0, MAX_MARKERS);
  if (markers.length === 0) return null;
  const state = {...value, width, height, markers};
  if (!ALLOWED_FILTERS.has(state.defaultFilter)) delete state.defaultFilter;
  return state;
}

function loadPreferences() {
  try {
    const saved = JSON.parse(localStorage.getItem(PREFERENCES_KEY) || "{}");
    return saved && typeof saved === "object" ? saved : {};
  } catch {
    return {};
  }
}

function savePreferences(preferences) {
  try { localStorage.setItem(PREFERENCES_KEY, JSON.stringify(preferences)); } catch { /* Preferences are optional. */ }
}

function contrastColor(hex) {
  const value = /^#([0-9a-f]{6})$/i.exec(hex)?.[1];
  if (!value) return "#ffffff";
  const [r, g, b] = [0, 2, 4].map(offset => parseInt(value.slice(offset, offset + 2), 16));
  return (r * 299 + g * 587 + b * 114) / 1000 > 150 ? "#111827" : "#ffffff";
}

function translucent(hex, alpha = 0.25) {
  const value = /^#([0-9a-f]{6})$/i.exec(hex)?.[1];
  if (!value) return "transparent";
  const [r, g, b] = [0, 2, 4].map(offset => parseInt(value.slice(offset, offset + 2), 16));
  return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

let activeSession = null;

/**
 * Opens the annotation dialog for an image.
 * Resolves with `{ state, blob }` when saved with markers, `{ state: null }` when all
 * markers were cleared, or `null` when cancelled.
 */
export function openImageAnnotator({src, state, title = "Annotate image"}) {
  if (activeSession) return Promise.resolve(null);
  return new Promise(resolve => {
    activeSession = createSession({src, state: sanitizeAnnotationState(state), title}, result => {
      activeSession = null;
      resolve(result);
    });
  });
}

const escapeHtml = value => String(value).replace(/[&<>"']/g, character => ({"&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"})[character]);

function toolButton(id, extraClass = "") {
  const tool = TOOLS[id];
  const content = tool.emoji ? `<span class="tf-annotator-emoji" aria-hidden="true">${escapeHtml(tool.emoji)}</span>` : icon(id);
  const label = tool.emoji && tool.label !== tool.emoji ? `${tool.emoji} ${tool.label}` : tool.label;
  return `<button type="button" class="tf-annotator-tool ${extraClass}" data-annotator-tool="${escapeHtml(id)}" aria-pressed="false" title="${escapeHtml(label)}${tool.key ? ` (${tool.key.toUpperCase()})` : ""}" aria-label="${escapeHtml(tool.emoji ? `Emoji ${label}` : label)}">${content}</button>`;
}

const MAX_RECENT_EMOJI = 14;
const MAX_EMOJI_RESULTS = 240;

function createSession({src, state, title}, finish) {
  const preferences = loadPreferences();
  const recentEmoji = (Array.isArray(preferences.recentEmoji) ? preferences.recentEmoji : [])
    .filter(entry => entry && isEmoji(entry.emoji))
    .slice(0, MAX_RECENT_EMOJI)
    .map(entry => ({emoji: entry.emoji, name: String(entry.name || "").slice(0, 80)}));
  recentEmoji.forEach(entry => ensureEmojiTool(entry.emoji, entry.name));
  const customColors = (Array.isArray(preferences.customColors) ? preferences.customColors : [])
    .filter(isHexColor)
    .map(color => color.toLowerCase())
    .filter((color, index, all) => all.indexOf(color) === index && !COLORS.some(entry => entry.value === color))
    .slice(0, MAX_CUSTOM_COLORS);
  const groupTool = Object.fromEntries(GROUPS.map(group => {
    let saved = preferences.groups?.[group.id];
    if (typeof saved === "string" && saved.startsWith("emoji:")) saved = ensureEmojiTool(saved.slice("emoji:".length));
    const fallback = Object.keys(TOOLS).find(id => TOOLS[id].group === group.id);
    return [group.id, TOOLS[saved]?.group === group.id ? saved : fallback];
  }));
  const settings = {
    color: isHexColor(preferences.color) ? preferences.color.toLowerCase() : COLORS[0].value,
    width: STROKE_WIDTHS.includes(preferences.width) ? preferences.width : STROKE_WIDTHS[1],
    dash: DASHES.some(dash => dash.value === preferences.dash) ? preferences.dash : "",
    fill: preferences.fill === true,
    font: FONT_SIZES.some(size => size.value === preferences.font) ? preferences.font : FONT_SIZES[1].value
  };

  const dialog = document.createElement("dialog");
  dialog.className = "tf-annotator";
  dialog.setAttribute("aria-label", title);
  dialog.innerHTML = `
    <div class="tf-annotator-toolbar" role="toolbar" aria-label="Annotation tools">
      <div class="tf-annotator-group" role="group" aria-label="Tools">
        <button type="button" class="tf-annotator-tool" data-annotator-tool="select" aria-pressed="true" title="Select and move (V)" aria-label="Select">${icon("select")}</button>
        ${GROUPS.map(group => `
          <div class="tf-annotator-split" data-annotator-group="${group.id}">
            <span data-annotator-group-current>${toolButton(groupTool[group.id])}</span>
            <button type="button" class="tf-annotator-more" data-annotator-menu-toggle="${group.id}" aria-haspopup="true" aria-expanded="false" aria-label="More ${group.label.toLowerCase()}" title="More ${group.label.toLowerCase()}">
              <svg viewBox="0 0 20 20" aria-hidden="true" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round"><path d="M6 8l4 4 4-4"/></svg>
            </button>
            ${group.id === "stamps" ? `
            <div class="tf-annotator-menu tf-annotator-stamps" data-annotator-menu="stamps" role="group" aria-label="${group.label}" hidden>
              <div class="tf-annotator-stamp-grid">${["check", "cross", ...EMOJI.map(emoji => `emoji:${emoji}`)].map(id => toolButton(id)).join("")}</div>
              <div class="tf-annotator-stamp-label" data-annotator-recent-label hidden>Recent</div>
              <div class="tf-annotator-stamp-grid" data-annotator-recent></div>
              <div class="tf-annotator-emoji-search">
                <input type="search" data-annotator-emoji-search placeholder="Search emoji (e.g. bull, money, rocket)" aria-label="Search emoji" autocomplete="off" spellcheck="false" />
                <select data-annotator-emoji-group aria-label="Emoji category"><option value="">All categories</option></select>
              </div>
              <div class="tf-annotator-emoji-results" data-annotator-emoji-results role="group" aria-label="Emoji results"></div>
              <p class="tf-annotator-emoji-status" data-annotator-emoji-status role="status" aria-live="polite"></p>
            </div>` : `
            <div class="tf-annotator-menu" data-annotator-menu="${group.id}" role="group" aria-label="${group.label}" hidden>
              ${Object.keys(TOOLS).filter(id => TOOLS[id].group === group.id).map(id => {
                const tool = TOOLS[id];
                return `<button type="button" class="tf-annotator-menu-item" data-annotator-tool="${id}" aria-pressed="false">${icon(id)}<span>${tool.label}</span>${tool.key ? `<kbd>${tool.key.toUpperCase()}</kbd>` : ""}</button>`;
              }).join("")}
            </div>`}
          </div>`).join("")}
      </div>
      <div class="tf-annotator-group" role="group" aria-label="Edit">
        <button type="button" data-annotator-action="delete" title="Delete selected (Delete)">Delete</button>
        <button type="button" data-annotator-action="undo" title="Undo (Ctrl+Z)">Undo</button>
        <button type="button" data-annotator-action="redo" title="Redo (Ctrl+Y)">Redo</button>
        <button type="button" data-annotator-action="clear" title="Remove all annotations">Clear all</button>
      </div>
      <div class="tf-annotator-group tf-annotator-commit">
        <button type="button" class="btn btn-outline-secondary btn-sm" data-annotator-action="cancel">Cancel</button>
        <button type="button" class="btn btn-primary btn-sm" data-annotator-action="save">Save</button>
      </div>
    </div>
    <div class="tf-annotator-properties" role="toolbar" aria-label="Annotation style">
      <div class="tf-annotator-group" role="group" aria-label="Color" data-annotator-prop="color">
        ${COLORS.map(color => `<button type="button" class="tf-annotator-swatch" data-annotator-color="${color.value}" aria-pressed="false" aria-label="${color.label}" title="${color.label}" style="--swatch:${color.value}"></button>`).join("")}
        <span class="tf-annotator-swatch-divider" data-annotator-custom-divider aria-hidden="true" hidden></span>
        <span style="display:contents" data-annotator-custom-swatches></span>
        <label class="tf-annotator-custom-color" title="Custom color" data-annotator-custom-picker>
          <input type="color" data-annotator-custom-color aria-label="Choose a custom color" />
        </label>
      </div>
      <div class="tf-annotator-group" role="group" aria-label="Line width" data-annotator-prop="width">
        ${STROKE_WIDTHS.map(width => `<button type="button" class="tf-annotator-width" data-annotator-width="${width}" aria-pressed="false" aria-label="Line width ${width}" title="Line width ${width}"><span style="height:${Math.min(width, 8)}px"></span></button>`).join("")}
      </div>
      <div class="tf-annotator-group" role="group" aria-label="Line style" data-annotator-prop="dash">
        ${DASHES.map(dash => `<button type="button" class="tf-annotator-dash" data-annotator-dash="${dash.value}" aria-pressed="false" aria-label="${dash.label}" title="${dash.label}"><svg viewBox="0 0 28 8" aria-hidden="true"><line x1="2" y1="4" x2="26" y2="4" stroke="currentColor" stroke-width="2.5" stroke-linecap="round" stroke-dasharray="${dash.value ? dash.value.split(" ").map(part => Number(part) / 2).join(" ") : "none"}"/></svg></button>`).join("")}
      </div>
      <div class="tf-annotator-group" role="group" aria-label="Fill" data-annotator-prop="fill">
        <button type="button" data-annotator-fill="false" aria-pressed="false" title="Outline only">No fill</button>
        <button type="button" data-annotator-fill="true" aria-pressed="false" title="Translucent fill">Fill</button>
      </div>
      <div class="tf-annotator-group" role="group" aria-label="Text size" data-annotator-prop="font">
        ${FONT_SIZES.map(size => `<button type="button" data-annotator-font="${size.value}" aria-pressed="false" title="Text size ${size.label}">${size.label}</button>`).join("")}
      </div>
    </div>
    <div class="tf-annotator-stage" data-annotator-stage></div>
    <p class="tf-annotator-status" data-annotator-status role="status" aria-live="polite">Loading image…</p>`;
  document.body.appendChild(dialog);

  const stage = dialog.querySelector("[data-annotator-stage]");
  const status = dialog.querySelector("[data-annotator-status]");
  const setStatus = text => { status.textContent = text; };
  let tool = "select";
  let closed = false;
  let saving = false;

  const image = document.createElement("img");
  const markerArea = new MarkerArea();
  markerArea.autoZoomIn = false;
  markerArea.autoZoomOut = true;
  PATCHED_IMAGE_MARKERS.forEach(type => markerArea.registerMarkerType(type, ImageMarkerEditor));

  const persist = () => savePreferences({...settings, groups: groupTool, recentEmoji, customColors});

  const close = result => {
    if (closed) return;
    closed = true;
    if (dialog.open) dialog.close();
    dialog.remove();
    finish(result);
  };

  // --- Toolbar state ------------------------------------------------------

  const setPressed = (selector, attribute, value) => {
    dialog.querySelectorAll(selector).forEach(button => {
      const active = button.getAttribute(attribute) === String(value);
      button.classList.toggle("is-active", active);
      button.setAttribute("aria-pressed", String(active));
    });
  };

  const toolForEditor = editor => {
    const typeName = editor?.marker?.typeName || Object.getPrototypeOf(editor?.marker || {})?.constructor?.typeName;
    if (typeName === ArrowMarker.typeName && editor.arrowType === "both") return "doubleArrow";
    return TYPE_TOOL.get(typeName) || null;
  };

  // Shows only the settings that apply to the active tool or the selection.
  const showProperties = toolId => {
    const definition = TOOLS[toolId];
    const props = new Set(definition ? definition.props : ["width", "dash", "fill", "font"]);
    if (!definition || definition.colorMode !== "none") props.add("color");
    dialog.querySelectorAll("[data-annotator-prop]").forEach(group => {
      group.hidden = !props.has(group.dataset.annotatorProp);
    });
  };

  const customPicker = dialog.querySelector("[data-annotator-custom-picker]");
  const customInput = dialog.querySelector("[data-annotator-custom-color]");

  const renderCustomColors = () => {
    dialog.querySelector("[data-annotator-custom-swatches]").innerHTML = customColors
      .map(color => `<button type="button" class="tf-annotator-swatch" data-annotator-color="${color}" aria-pressed="false" aria-label="Custom color ${color}" title="${color}" style="--swatch:${color}"></button>`)
      .join("");
    dialog.querySelector("[data-annotator-custom-divider]").hidden = customColors.length === 0;
  };

  const rememberCustomColor = color => {
    color = color.toLowerCase();
    if (!isHexColor(color) || COLORS.some(entry => entry.value === color)) return;
    const index = customColors.indexOf(color);
    if (index >= 0) customColors.splice(index, 1);
    customColors.unshift(color);
    customColors.length = Math.min(customColors.length, MAX_CUSTOM_COLORS);
    renderCustomColors();
    refreshProperties();
    persist();
  };

  const refreshProperties = () => {
    setPressed("[data-annotator-color]", "data-annotator-color", settings.color);
    const isSwatch = COLORS.some(entry => entry.value === settings.color) || customColors.includes(settings.color);
    customPicker.classList.toggle("is-active", !isSwatch);
    customPicker.style.background = isSwatch ? "" : settings.color;
    if (customInput.value !== settings.color) customInput.value = settings.color;
    setPressed("[data-annotator-width]", "data-annotator-width", settings.width);
    setPressed("[data-annotator-dash]", "data-annotator-dash", settings.dash);
    setPressed("[data-annotator-fill]", "data-annotator-fill", settings.fill);
    setPressed("[data-annotator-font]", "data-annotator-font", settings.font);
  };

  const highlightTool = id => {
    setPressed("[data-annotator-tool]", "data-annotator-tool", id);
    dialog.querySelectorAll("[data-annotator-group]").forEach(split => {
      split.classList.toggle("is-active", TOOLS[id]?.group === split.dataset.annotatorGroup);
    });
  };

  const setGroupTool = id => {
    const group = TOOLS[id].group;
    if (groupTool[group] === id) return;
    groupTool[group] = id;
    dialog.querySelector(`[data-annotator-group="${group}"] [data-annotator-group-current]`).innerHTML = toolButton(id);
  };

  const closeMenus = () => {
    dialog.querySelectorAll("[data-annotator-menu]").forEach(menu => { menu.hidden = true; });
    dialog.querySelectorAll("[data-annotator-menu-toggle]").forEach(toggle => toggle.setAttribute("aria-expanded", "false"));
  };

  const toggleMenu = groupId => {
    const menu = dialog.querySelector(`[data-annotator-menu="${groupId}"]`);
    const wasHidden = menu.hidden;
    closeMenus();
    if (!wasHidden) return;
    menu.hidden = false;
    dialog.querySelector(`[data-annotator-menu-toggle="${groupId}"]`).setAttribute("aria-expanded", "true");
    if (groupId === "stamps") {
      openEmojiSearch();
      return;
    }
    (menu.querySelector(".is-active") || menu.querySelector("button"))?.focus();
  };

  // --- Emoji search -------------------------------------------------------

  const emojiSearch = dialog.querySelector("[data-annotator-emoji-search]");
  const emojiGroup = dialog.querySelector("[data-annotator-emoji-group]");
  const emojiResults = dialog.querySelector("[data-annotator-emoji-results]");
  const emojiStatus = dialog.querySelector("[data-annotator-emoji-status]");
  let emojiData = null;

  const renderRecentEmoji = () => {
    const container = dialog.querySelector("[data-annotator-recent]");
    container.innerHTML = recentEmoji.map(entry => toolButton(ensureEmojiTool(entry.emoji, entry.name))).join("");
    container.hidden = recentEmoji.length === 0;
    dialog.querySelector("[data-annotator-recent-label]").hidden = recentEmoji.length === 0;
    highlightTool(tool);
  };

  const rememberEmoji = (emoji, name) => {
    const index = recentEmoji.findIndex(entry => entry.emoji === emoji);
    if (index >= 0) recentEmoji.splice(index, 1);
    recentEmoji.unshift({emoji, name: String(name || "").slice(0, 80)});
    recentEmoji.length = Math.min(recentEmoji.length, MAX_RECENT_EMOJI);
    renderRecentEmoji();
  };

  const findEmoji = () => {
    const query = emojiSearch.value.trim().toLowerCase();
    const group = emojiGroup.value === "" ? null : Number(emojiGroup.value);
    const words = query.split(/\s+/).filter(Boolean);
    const synonymsFor = word => word.length < 2 ? "" : Object.entries(EMOJI_SYNONYMS)
      .filter(([key]) => key.startsWith(word)).map(([, value]) => value).join("");
    const wordSynonyms = words.map(synonymsFor);
    return emojiData.emoji
      .filter(entry => group === null || entry.group === group)
      .map(entry => {
        if (words.length === 0) return {entry, score: 0};
        if (query === entry.emoji) return {entry, score: 3};
        let score = 0;
        for (let index = 0; index < words.length; index++) {
          const word = words[index];
          if (entry.search.startsWith(word) || entry.search.includes(` ${word}`)) score += 2;
          else if (entry.search.includes(word)) score += 1;
          else if (wordSynonyms[index].includes(entry.emoji)) score += 2;
          else return null;
        }
        return {entry, score};
      })
      .filter(Boolean)
      .sort((left, right) => right.score - left.score);
  };

  const renderEmojiResults = () => {
    if (!emojiData) return;
    const matches = findEmoji();
    const shown = matches.slice(0, MAX_EMOJI_RESULTS);
    const fragment = document.createDocumentFragment();
    shown.forEach(({entry}) => {
      const button = document.createElement("button");
      button.type = "button";
      button.className = "tf-annotator-tool";
      button.dataset.annotatorEmoji = entry.emoji;
      button.dataset.annotatorEmojiName = entry.name;
      button.title = entry.name;
      button.setAttribute("aria-label", `Emoji ${entry.name}`);
      const glyph = document.createElement("span");
      glyph.className = "tf-annotator-emoji";
      glyph.setAttribute("aria-hidden", "true");
      glyph.textContent = entry.emoji;
      button.append(glyph);
      fragment.append(button);
    });
    emojiResults.replaceChildren(fragment);
    emojiResults.scrollTop = 0;
    emojiStatus.textContent = matches.length === 0
      ? "No emoji match that search."
      : matches.length > shown.length ? `Showing ${shown.length} of ${matches.length}. Refine the search to see more.` : "";
  };

  const openEmojiSearch = async () => {
    emojiSearch.focus();
    if (emojiData) return;
    emojiStatus.textContent = "Loading emoji…";
    try {
      emojiData = await loadEmojiData();
      emojiData.groups.forEach((name, index) => {
        const option = document.createElement("option");
        option.value = String(index);
        option.textContent = name;
        emojiGroup.append(option);
      });
      renderEmojiResults();
    } catch (error) {
      emojiStatus.textContent = error instanceof Error ? error.message : "The emoji list could not be loaded.";
    }
  };

  const pickEmoji = (emoji, name) => {
    const id = ensureEmojiTool(emoji, name);
    if (!id) return;
    rememberEmoji(emoji, name);
    selectTool(id);
  };

  emojiSearch.addEventListener("input", renderEmojiResults);
  emojiGroup.addEventListener("change", renderEmojiResults);
  emojiSearch.addEventListener("keydown", event => {
    if (event.key === "Enter") {
      event.preventDefault();
      const first = emojiResults.querySelector("[data-annotator-emoji]");
      if (first) pickEmoji(first.dataset.annotatorEmoji, first.dataset.annotatorEmojiName);
    } else if (event.key === "ArrowDown") {
      event.preventDefault();
      emojiResults.querySelector("button")?.focus();
    }
  });
  emojiResults.addEventListener("keydown", event => {
    const buttons = Array.from(emojiResults.querySelectorAll("button"));
    const index = buttons.indexOf(document.activeElement);
    if (index < 0) return;
    const columns = Math.max(1, Math.round(emojiResults.clientWidth / (buttons[0].offsetWidth + 4)));
    const step = {ArrowRight: 1, ArrowLeft: -1, ArrowDown: columns, ArrowUp: -columns}[event.key];
    if (!step) return;
    event.preventDefault();
    if (index + step < 0) emojiSearch.focus();
    else buttons[Math.min(buttons.length - 1, index + step)]?.focus();
  });

  // --- Applying styles ------------------------------------------------------

  const applyColor = (editor, toolId) => {
    const definition = TOOLS[toolId];
    if (!definition) return;
    const color = settings.color;
    switch (definition.colorMode) {
      case "stroke":
        editor.strokeColor = color;
        if (definition.props.includes("fill")) editor.fillColor = settings.fill ? translucent(color) : "transparent";
        break;
      case "fill":
        editor.fillColor = color;
        break;
      case "text":
        editor.color = color;
        break;
      case "boxed":
        editor.strokeColor = color;
        editor.fillColor = color;
        editor.color = contrastColor(color);
        break;
    }
  };

  const applyStyle = (editor, toolId, changed) => {
    const definition = TOOLS[toolId];
    if (!definition) return;
    const props = new Set(definition.props);
    if (changed === "all" || changed === "color" || changed === "fill") applyColor(editor, toolId);
    if ((changed === "all" || changed === "width") && props.has("width")) editor.strokeWidth = settings.width;
    if ((changed === "all" || changed === "dash") && props.has("dash")) editor.strokeDasharray = settings.dash;
    if ((changed === "all" || changed === "font") && props.has("font")) editor.fontSize = {value: settings.font, units: "rem", step: 0.1};
  };

  const selectTool = id => {
    closeMenus();
    if (id !== "select" && !TOOLS[id]) return;
    tool = id;
    highlightTool(id);
    if (id === "select") {
      markerArea.switchToSelectMode();
      showProperties(null);
      return;
    }
    setGroupTool(id);
    highlightTool(id);
    persist();
    const definition = TOOLS[id];
    if (definition.defaultColor && !preferences.color) settings.color = definition.defaultColor;
    refreshProperties();
    showProperties(id);
    const editor = markerArea.createMarker(definition.type);
    if (!editor) return;
    if (definition.emoji) editor.marker.svgString = emojiSvg(definition.emoji);
    if (definition.group === "stamps") {
      // Stamps are placed with a click; the library default (24px) is tiny on chart screenshots.
      const side = Math.round(Math.min(160, Math.max(32, Math.min(image.naturalWidth, image.naturalHeight) * 0.08)));
      editor.marker.defaultSize = {width: side, height: side};
    }
    if (definition.arrowType) editor.arrowType = definition.arrowType;
    applyStyle(editor, id, "all");
    setStatus(definition.group === "stamps"
      ? "Click on the image to place the stamp, then drag its corners to resize."
      : id === "polygon"
        ? "Click to add points; double-click to finish."
        : definition.group === "text"
          ? "Drag to place the text box, then type. Click outside to finish."
          : "Drag on the image to draw. Shift keeps lines straight.");
  };

  const updateSettings = (key, value) => {
    settings[key] = value;
    if (key === "color") preferences.color = value;
    refreshProperties();
    persist();
    markerArea.selectedMarkerEditors.forEach(editor => applyStyle(editor, toolForEditor(editor), key));
  };

  // marker.js edits text in a <textarea> inside its (open) shadow root and does not
  // report an "edit" state while typing, so look at the real event target instead.
  const isTyping = event => event.composedPath().some(node =>
    node instanceof HTMLTextAreaElement
    || node instanceof HTMLInputElement
    || node instanceof HTMLSelectElement
    || (node instanceof HTMLElement && node.isContentEditable));

  const updateHistoryButtons = () => {
    dialog.querySelector('[data-annotator-action="undo"]').disabled = !markerArea.isUndoPossible;
    dialog.querySelector('[data-annotator-action="redo"]').disabled = !markerArea.isRedoPossible;
  };

  markerArea.addEventListener("markercreate", () => {
    // Return to select mode after each shape so the new shape can be adjusted right away.
    if (tool !== "select") {
      tool = "select";
      highlightTool("select");
    }
    updateHistoryButtons();
  });
  markerArea.addEventListener("areastatechange", updateHistoryButtons);
  markerArea.addEventListener("markerselect", event => {
    const editor = event.detail?.markerEditor;
    const toolId = toolForEditor(editor);
    if (!editor || !toolId) return;
    showProperties(toolId);
    const definition = TOOLS[toolId];
    const color = definition.colorMode === "fill" ? editor.fillColor
      : definition.colorMode === "text" ? editor.color
        : editor.strokeColor;
    if (isHexColor(color)) settings.color = color.toLowerCase();
    if (definition.props.includes("width") && STROKE_WIDTHS.includes(editor.strokeWidth)) settings.width = editor.strokeWidth;
    if (definition.props.includes("dash")) settings.dash = editor.strokeDasharray || "";
    if (definition.props.includes("fill")) settings.fill = Boolean(editor.fillColor) && editor.fillColor !== "transparent";
    if (definition.props.includes("font") && editor.fontSize?.value) settings.font = editor.fontSize.value;
    refreshProperties();
  });
  markerArea.addEventListener("markerdeselect", () => {
    if (tool === "select") showProperties(null);
  });

  dialog.addEventListener("click", async event => {
    const element = event.target instanceof Element ? event.target : null;
    const target = element?.closest("button");
    // Clicks inside an open menu (search box, category list, results) keep it open.
    if (!element?.closest("[data-annotator-menu], [data-annotator-menu-toggle]")) closeMenus();
    if (!target || saving) return;
    if (target.dataset.annotatorMenuToggle) toggleMenu(target.dataset.annotatorMenuToggle);
    else if (target.dataset.annotatorEmoji) pickEmoji(target.dataset.annotatorEmoji, target.dataset.annotatorEmojiName);
    else if (target.dataset.annotatorTool) selectTool(target.dataset.annotatorTool);
    else if (target.dataset.annotatorColor) updateSettings("color", target.dataset.annotatorColor);
    else if (target.dataset.annotatorWidth) updateSettings("width", Number(target.dataset.annotatorWidth));
    else if (target.dataset.annotatorDash !== undefined) updateSettings("dash", target.dataset.annotatorDash);
    else if (target.dataset.annotatorFill) updateSettings("fill", target.dataset.annotatorFill === "true");
    else if (target.dataset.annotatorFont) updateSettings("font", Number(target.dataset.annotatorFont));
    else {
      switch (target.dataset.annotatorAction) {
        case "delete": markerArea.deleteSelectedMarkers(); break;
        case "undo": markerArea.undo(); break;
        case "redo": markerArea.redo(); break;
        case "clear":
          markerArea.editors.slice().forEach(editor => markerArea.deleteMarker(editor));
          break;
        case "cancel": close(null); break;
        case "save": await save(); break;
      }
      updateHistoryButtons();
    }
  });

  dialog.addEventListener("keydown", event => {
    if (saving) return;
    const openMenu = dialog.querySelector("[data-annotator-menu]:not([hidden])");
    if (openMenu && event.key === "Escape") {
      event.preventDefault();
      const groupId = openMenu.dataset.annotatorMenu;
      closeMenus();
      dialog.querySelector(`[data-annotator-menu-toggle="${groupId}"]`)?.focus();
      return;
    }
    // Leave keys alone while text is being typed into a text marker; otherwise
    // letters trigger tool shortcuts and Backspace deletes the marker being edited.
    if (isTyping(event)) return;
    const key = event.key.toLowerCase();
    if ((event.ctrlKey || event.metaKey) && key === "z") {
      event.preventDefault();
      if (event.shiftKey) markerArea.redo(); else markerArea.undo();
      updateHistoryButtons();
    } else if ((event.ctrlKey || event.metaKey) && key === "y") {
      event.preventDefault();
      markerArea.redo();
      updateHistoryButtons();
    } else if ((event.key === "Delete" || event.key === "Backspace") && markerArea.selectedMarkerEditors.length > 0) {
      event.preventDefault();
      markerArea.deleteSelectedMarkers();
      updateHistoryButtons();
    } else if (!event.ctrlKey && !event.metaKey && !event.altKey && (event.target === markerArea || event.target === dialog)) {
      const shortcut = key === "v" ? "select" : Object.keys(TOOLS).find(id => TOOLS[id].key === key);
      if (shortcut) {
        event.preventDefault();
        selectTool(shortcut);
      }
    }
  });

  // Escape closes an open menu first, then the dialog without saving.
  dialog.addEventListener("cancel", event => {
    event.preventDefault();
    if (dialog.querySelector("[data-annotator-menu]:not([hidden])")) {
      closeMenus();
      return;
    }
    // Escape while typing finishes the text instead of discarding the whole annotation.
    const editingField = markerArea.shadowRoot?.activeElement;
    if (editingField instanceof HTMLTextAreaElement || editingField instanceof HTMLInputElement) {
      editingField.blur();
      return;
    }
    if (!saving) close(null);
  });

  const save = async () => {
    markerArea.switchToSelectMode();
    const current = sanitizeAnnotationState(markerArea.getState());
    if (!current) {
      close({state: null});
      return;
    }
    saving = true;
    dialog.classList.add("is-saving");
    setStatus("Saving annotated image…");
    try {
      const blob = await rasterize(image, current);
      close({state: current, blob});
    } catch (error) {
      saving = false;
      dialog.classList.remove("is-saving");
      setStatus(error instanceof Error ? error.message : "The annotated image could not be created.");
    }
  };

  showProperties(null);
  renderRecentEmoji();
  renderCustomColors();
  refreshProperties();
  highlightTool("select");

  // The native picker previews live while dragging; the final choice joins the custom swatches.
  customInput.addEventListener("input", () => {
    if (isHexColor(customInput.value)) updateSettings("color", customInput.value.toLowerCase());
  });
  customInput.addEventListener("change", () => rememberCustomColor(customInput.value));

  image.addEventListener("load", () => {
    if (closed) return;
    markerArea.targetImage = image;
    stage.appendChild(markerArea);
    if (state) markerArea.restoreState(state, false);
    updateHistoryButtons();
    setStatus("Choose a tool, then drag on the image. Use the arrows next to each tool for more options.");
    markerArea.focus({preventScroll: true});
  }, {once: true});
  image.addEventListener("error", () => {
    if (!closed) setStatus("The original image could not be loaded, so it cannot be annotated.");
  }, {once: true});
  image.src = src;

  dialog.showModal();
}

async function rasterize(image, state) {
  for (const type of ["image/png", "image/jpeg"]) {
    const renderer = new Renderer();
    PATCHED_IMAGE_MARKERS.forEach(type => renderer.registerMarkerType(type));
    renderer.targetImage = image;
    renderer.naturalSize = true;
    renderer.imageType = type;
    if (type === "image/jpeg") renderer.imageQuality = 0.9;
    const dataUrl = await renderer.rasterize(state);
    const blob = dataUrlToBlob(dataUrl, type);
    if (blob.size > 0 && blob.size <= MAX_IMAGE_BYTES) return blob;
  }
  throw new Error("The annotated image is larger than 10 MB.");
}

function dataUrlToBlob(dataUrl, fallbackType) {
  const [header, data = ""] = String(dataUrl).split(",", 2);
  const type = header.match(/^data:([^;,]+)/)?.[1] || fallbackType;
  const binary = atob(data);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index);
  return new Blob([bytes], {type});
}
