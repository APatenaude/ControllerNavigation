"use strict";
// On-screen keyboard. OnScreenKeyboard shows this page in a window that never takes the focus,
// over the app in front, and sends it the controller's buttons; what it types goes to that app
// through Windows input.
//   From the host: {type:'open'}  {type:'input', button: up|down|left|right|a|x|y|lt|rt|start|tab}
//   To the host:   {type:'ready'}  {type:'type', text}  {type:'combo', keys: 'Ctrl+C'}  {type:'close'}
//                  {type:'down', keys: 'Alt'}  {type:'up', keys: 'Alt'}   (a modifier held in Windows)
// Three pages: letters, symbols, and Keys (Esc, arrows, F1..F12, the modifier toggles, shortcuts).

const host = window.chrome && window.chrome.webview;
const send = (msg) => {
  if (host) host.postMessage(msg);
  else console.log("to host", msg);
};

const kb = document.getElementById("kb");
const BAND = 440;
// The 1920x440 band scaled to its window: by the width, or by the height on a screen wider than
// 16:9 (an ultrawide: centred, the sides the same colour).
function fit() {
  const s =
    Math.min(innerWidth / 1920, innerHeight / BAND) || innerWidth / 1920;
  kb.style.transform = `translate(${Math.max(0, (innerWidth - 1920 * s) / 2)}px, ${Math.max(0, innerHeight - BAND * s)}px) scale(${s})`;
}
addEventListener("resize", fit);
fit();

const ICONS = {
  shift: '<path d="M12 4l8 9h-5v7H9v-7H4l8-9z"/>',
  backspace:
    '<path d="M9 5h11a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H9l-6-7 6-7z"/><path d="M13 10l5 4M18 10l-5 4"/>',
  left: '<path d="M15 5l-7 7 7 7"/>',
  right: '<path d="M9 5l7 7-7 7"/>',
  up: '<path d="M5 15l7-7 7 7"/>',
  down: '<path d="M5 9l7 7 7-7"/>',
};
const icon = (name) =>
  `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">${ICONS[name]}</svg>`;
const esc = (s) =>
  String(s).replace(
    /[&<>"']/g,
    (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[
        c
      ],
  );

const LETTERS = ["1234567890", "qwertyuiop", "asdfghjkl'", "zxcvbnm,."];
const SYMBOLS = ["1234567890", "!@#$%^&*()", "-_=+[]{};:", '"<>?/\\|~`'];

let page = "abc"; // abc | sym | keys
let shift = "off"; // off | once (next letter) | lock: capital letters typed as text
const mods = { Ctrl: false, Alt: false, Shift: false, Win: false }; // held down in Windows
let focus = { row: 1, col: 0 };

const heldAny = () => Object.values(mods).some(Boolean);

function keysPageRows() {
  const key = (label, keys, extra) => ({ id: "combo", keys, label, ...extra });
  const mod = (name) => ({
    id: "mod",
    mod: name,
    label: name,
    aria: name + " (hold)",
  });
  const gap = { spacer: true };
  return [
    [
      key("Esc", "Esc"),
      key("Tab", "Tab"),
      key("Enter", "Enter"),
      key("Backspace", "Backspace"),
      key("Delete", "Delete"),
      key("Insert", "Insert"),
      key("Home", "Home"),
      key("End", "End"),
      key("PageUp", "PageUp"),
      key("PageDown", "PageDown"),
    ],
    Array.from({ length: 12 }, (_, i) => key("F" + (i + 1), "F" + (i + 1))),
    [
      mod("Ctrl"),
      mod("Alt"),
      mod("Shift"),
      mod("Win"),
      gap,
      { ...key("", "Up", { aria: "Up" }), icon: "up" },
      gap, // seven slots like the row below, so that Up is exactly above Down
    ],
    [
      { id: "abc", label: "abc", aria: "Letters" },
      key("PrtSc", "PrintScreen", { aria: "Print screen" }),
      gap,
      gap,
      { ...key("", "Left", { aria: "Left" }), icon: "left" },
      { ...key("", "Down", { aria: "Down" }), icon: "down" },
      { ...key("", "Right", { aria: "Right" }), icon: "right" },
    ],
    [
      key("Alt+Tab", "Alt+Tab", { aria: "Previous window" }),
      key("Desktop", "Win+D"),
      key("Copy", "Ctrl+C"),
      key("Paste", "Ctrl+V"),
      key("Cut", "Ctrl+X"),
      key("Undo", "Ctrl+Z"),
      key("Select all", "Ctrl+A"),
      key("Find", "Ctrl+F"),
      key("Close window", "Alt+F4"),
    ].map((k) => ({ ...k, extra: true })),
  ].map((row) => row.map((k) => ({ ...k, compact: true }))); // the longer labels need 138 px keys
}

function rows() {
  if (page === "keys") return keysPageRows();
  const symbols = page === "sym";
  const set = symbols ? SYMBOLS : LETTERS;
  const upper = shift !== "off" && !symbols;
  const chars = (s) =>
    s.split("").map((c) => ({ id: "char", text: upper ? c.toUpperCase() : c }));
  return [
    chars(set[0]),
    chars(set[1]),
    chars(set[2]),
    [
      { id: "shift", w: 180, icon: "shift", aria: "Shift" },
      ...chars(set[3]),
      { id: "back", w: 180, icon: "backspace", aria: "Delete" },
    ],
    [
      {
        id: "symbols",
        w: 130,
        label: symbols ? "abc" : "#+=",
        aria: symbols ? "Letters" : "Symbols",
      },
      { id: "keys", w: 130, label: "Keys", aria: "Keys page" },
      { id: "char", w: 130, text: ".com" },
      { id: "char", w: 440, text: " ", aria: "Space" },
      { id: "left", icon: "left", aria: "Cursor left" },
      { id: "right", icon: "right", aria: "Cursor right" },
      { id: "enter", w: 200, label: "Enter", primary: true },
    ],
    // Keys the controller has no button for, tapped as shortcuts.
    [
      { id: "combo", keys: "Tab", label: "Tab" },
      { id: "combo", keys: "Esc", label: "Esc" },
      { id: "combo", keys: "Ctrl+A", label: "Select all" },
      { id: "combo", keys: "Ctrl+C", label: "Copy" },
      { id: "combo", keys: "Ctrl+V", label: "Paste" },
      { id: "combo", keys: "Ctrl+Z", label: "Undo" },
      { id: "combo", keys: "F5", label: "Refresh" },
      { id: "combo", keys: "F11", label: "Full screen" },
      { id: "combo", keys: "VolumeDown", label: "Vol -", aria: "Volume down" },
      { id: "combo", keys: "VolumeUp", label: "Vol +", aria: "Volume up" },
      { id: "combo", keys: "VolumeMute", label: "Mute" },
    ].map((k) => ({ ...k, extra: true })),
  ];
}

function render() {
  const layout = rows();
  focus.row = Math.min(focus.row, layout.length - 1);
  focus.col = Math.min(focus.col, layout[focus.row].length - 1);
  // The keys are drawn again only when they change (shift, page, held modifiers); a move only
  // moves the ring, so a key's press flash is seen and nothing else redraws.
  const rowsEl = document.getElementById("kb-rows");
  const html = layout
    .map(
      (row, r) =>
        `<div class="kb-row">${row
          .map((k, c) => {
            if (k.spacer)
              return `<div class="kb-spacer${k.compact ? " compact" : ""}"></div>`;
            const cls = ["kb-key"];
            if (k.w) cls.push("w" + k.w);
            if (k.primary) cls.push("primary");
            if (k.extra) cls.push("extra");
            if (k.compact) cls.push("compact");
            if (k.id === "shift" && shift === "once") cls.push("latched");
            if (k.id === "shift" && shift === "lock") cls.push("locked");
            if (k.id === "mod" && mods[k.mod]) cls.push("held");
            const face = k.icon
              ? icon(k.icon)
              : esc(k.label || (k.text === " " ? "" : k.text));
            return `<div class="${cls.join(" ")}" data-r="${r}" data-c="${c}" role="button" aria-label="${esc(k.aria || k.label || k.text)}">${face}</div>`;
          })
          .join("")}</div>`,
    )
    .join("");
  if (rowsEl.dataset.html !== html) {
    rowsEl.innerHTML = html;
    rowsEl.dataset.html = html;
  }
  for (const el of rowsEl.querySelectorAll(".kb-key.on"))
    el.classList.remove("on");
  const on = rowsEl.querySelector(
    `.kb-key[data-r="${focus.row}"][data-c="${focus.col}"]`,
  );
  if (on) on.classList.add("on");
  const held = Object.keys(mods).filter((m) => mods[m]);
  document.getElementById("kb-state").textContent = [
    page === "sym" ? "Symbols" : page === "keys" ? "Keys" : "",
    shift === "lock" ? "CAPS" : shift === "once" ? "Shift" : "",
    held.length ? "Holding " + held.join("+") : "",
  ]
    .filter(Boolean)
    .join("   ");
  const list = [
    ["A", "Type"],
    ["X", "Delete"],
    ["Y", "Space"],
    [["LT", "RT"], "Cursor"],
    [["LB", "RB"], "Volume"],
    ["Start", "Enter"],
    ["View", "Tab"],
    ["B", "Close"],
  ];
  document.getElementById("kb-hints").innerHTML = list
    .map(
      ([btn, label]) =>
        '<div class="hint">' +
        (Array.isArray(btn) ? btn : [btn])
          .map((b) => `<span class="key">${esc(b)}</span>`)
          .join("") +
        `<span>${esc(label)}</span></div>`,
    )
    .join("");
}

function flash(r, c) {
  const el = document.querySelector(`.kb-key[data-r="${r}"][data-c="${c}"]`);
  if (!el) return;
  el.classList.add("hit");
  setTimeout(() => el.classList.remove("hit"), 90);
}

// A character as the key presses that make it: with a modifier held Windows combines virtual-key
// taps with it, a Unicode character it does not ("Ctrl" and "c" is Ctrl+C, not a typed "c").
const PLAIN = {
  "-": "Minus",
  "=": "Equal",
  ",": "Comma",
  ".": "Period",
  "/": "Slash",
  ";": "Semicolon",
  "'": "Quote",
  "[": "BracketLeft",
  "]": "BracketRight",
  "\\": "Backslash",
  "`": "Backquote",
  " ": "Space",
};
const SHIFTED = {
  "!": "1",
  "@": "2",
  "#": "3",
  $: "4",
  "%": "5",
  "^": "6",
  "&": "7",
  "*": "8",
  "(": "9",
  ")": "0",
  _: "Minus",
  "+": "Equal",
  "{": "BracketLeft",
  "}": "BracketRight",
  ":": "Semicolon",
  '"': "Quote",
  "<": "Comma",
  ">": "Period",
  "?": "Slash",
  "|": "Backslash",
  "~": "Backquote",
};

function charCombo(c) {
  if (/^[a-z]$/i.test(c)) return c.toUpperCase();
  if (/^[0-9]$/.test(c)) return c;
  if (PLAIN[c]) return PLAIN[c];
  if (SHIFTED[c]) return mods.Shift ? SHIFTED[c] : "Shift+" + SHIFTED[c];
  return null;
}

function typeText(text) {
  const keys = heldAny() && text.length === 1 ? charCombo(text) : null;
  if (keys) send({ type: "combo", keys });
  else send({ type: "type", text });
  if (shift === "once") shift = "off";
}

const combo = (keys) => send({ type: "combo", keys });
const cycleShift = () => {
  shift = shift === "off" ? "once" : shift === "once" ? "lock" : "off";
};

function toggleMod(name) {
  mods[name] = !mods[name];
  send({ type: mods[name] ? "down" : "up", keys: name });
}

function setPage(next) {
  page = next;
  focus = next === "keys" ? { row: 0, col: 0 } : { row: 1, col: 0 };
}

function press(k) {
  switch (k.id) {
    case "char":
      typeText(k.text);
      break;
    case "shift":
      cycleShift();
      break;
    case "back":
      combo("Backspace");
      break;
    case "symbols":
      setPage(page === "sym" ? "abc" : "sym");
      break;
    case "keys":
      setPage("keys");
      break;
    case "abc":
      setPage("abc");
      break;
    case "mod":
      toggleMod(k.mod);
      break;
    case "left":
      combo("Left");
      break;
    case "right":
      combo("Right");
      break;
    case "enter":
      combo("Enter");
      send({ type: "close" });
      break;
    case "combo":
      combo(k.keys);
      break;
  }
}

// The next key along a row, skipping the gaps, wrapping round.
function stepCol(row, col, dir) {
  const n = row.length;
  for (let i = 1; i <= n; i++) {
    const c = (col + dir * i + n * i) % n;
    if (!row[c].spacer) return c;
  }
  return col;
}

// Up and down go to the key nearest in the row above or below (rows differ in width), wrapping
// round past the top and the bottom.
function moveVertical(dir) {
  const layout = rows();
  const target = (focus.row + dir + layout.length) % layout.length;
  const from = document
    .querySelector(`.kb-key[data-r="${focus.row}"][data-c="${focus.col}"]`)
    .getBoundingClientRect();
  const x = from.left + from.width / 2;
  let best = 0;
  let bestDistance = Infinity;
  document.querySelectorAll(`.kb-key[data-r="${target}"]`).forEach((el) => {
    const b = el.getBoundingClientRect();
    const d = Math.abs(b.left + b.width / 2 - x);
    if (d < bestDistance) {
      best = +el.dataset.c;
      bestDistance = d;
    }
  });
  focus = { row: target, col: best };
}

function onButton(button) {
  const layout = rows();
  const row = layout[focus.row];
  switch (button) {
    case "up":
      moveVertical(-1);
      break;
    case "down":
      moveVertical(1);
      break;
    // A row wraps round: left from its first key, its last; right from its last, its first.
    case "left":
      focus.col = stepCol(row, focus.col, -1);
      break;
    case "right":
      focus.col = stepCol(row, focus.col, 1);
      break;
    case "a":
      flash(focus.row, focus.col);
      press(row[focus.col]);
      break;
    case "x":
      combo("Backspace");
      break;
    case "y":
      typeText(" ");
      break;
    case "lt":
      combo("Left");
      break;
    case "rt":
      combo("Right");
      break;
    case "start":
      combo("Enter");
      send({ type: "close" });
      return;
    case "tab":
      combo("Tab");
      break;
    case "b":
      send({ type: "close" });
      return;
    default:
      return;
  }
  render();
}

function onHost(m) {
  if (m.type === "open") {
    // The host let go of every modifier when the keyboard last closed.
    for (const name of Object.keys(mods)) mods[name] = false;
    page = "abc";
    shift = "off";
    focus = { row: 1, col: 0 };
    render();
  } else if (m.type === "input") {
    onButton(m.button);
  }
}

// A mouse or touch press on a key (the window never takes the focus, so the app keeps its field).
document.getElementById("kb-rows").addEventListener("pointerdown", (e) => {
  const el = e.target.closest(".kb-key");
  if (!el) return;
  e.preventDefault();
  focus = { row: +el.dataset.r, col: +el.dataset.c };
  onButton("a");
});

if (host) {
  host.addEventListener("message", (e) => onHost(e.data));
} else {
  // A normal browser: the computer's keyboard stands in for the controller.
  const keys = {
    ArrowUp: "up",
    ArrowDown: "down",
    ArrowLeft: "left",
    ArrowRight: "right",
    Enter: "a",
    Escape: "b",
    Backspace: "x",
    " ": "y",
    PageUp: "lt",
    PageDown: "rt",
    F10: "start",
    Tab: "tab",
  };
  addEventListener("keydown", (e) => {
    if (keys[e.key]) {
      e.preventDefault();
      onButton(keys[e.key]);
    }
  });
}
render();
send({ type: "ready" });
