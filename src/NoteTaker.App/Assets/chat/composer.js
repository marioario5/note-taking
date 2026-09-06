// The chat composer: a MathLive field that types prose as prose and mathematics as
// mathematics, with "$" switching between the two.
//
// An earlier version of this file was a plain textarea, written after testing that appeared to
// show MathLive could not do this. That testing was wrong: it drove the page through an
// automation harness that delivers text but NOT real keydown events, and every one of
// MathLive's mode switches and editing commands is keydown-driven. So "/" looked like it
// produced a literal slash, "$" looked like it was silently eaten, and smart mode looked dead.
// With a real key event all three behave: "/" builds a fraction with the caret in the
// denominator, and "$" switches text into math.
//
// What was genuinely wrong, and is fixed here and in composer.css:
//
//   * The field opened in MATH mode, so ordinary words rendered as italic, letter-spaced
//     variables — "is this right" came out as i·s·t·h·i·s. defaultMode below opens it in text.
//   * Two separate MathLive highlights tinted the box blue. See composer.css.
//
// Mode switching is owned here rather than left to smartMode. smartMode guesses from what you
// have typed, and guessing is what produced italic prose in the first place; "$" is a decision
// the student makes, and it matches both the notation the tutor prompt asks the model for and
// the delimiters the transcript's KaTeX renders.
//
// Talks to MainWindow.xaml.cs over WebView2's postMessage bridge: getText / clear / focus /
// setEnabled in, and ready / input / submit / pointer-focus / resize out.

// Static properties, read while a mathfield renders itself, so they must be set BEFORE the
// first one exists. That is why the element is created here rather than written into the
// markup — an element in the markup upgrades the moment the library loads, which can beat
// this code and leave the field hunting for fonts on a path that doesn't exist.
//
// Resolved relative to mathlive.min.js, NOT to this document: this means "the fonts folder
// beside the library". Writing the document-relative "mathlive/fonts" here instead silently
// asks for mathlive/mathlive/fonts and every glyph 404s.
MathfieldElement.fontsDirectory = "fonts";

// MathLive ships keypress click sounds and plays them by default. Not downloading the .wav
// files at all would also produce a 404 on every keystroke, so this is quieter and cleaner.
MathfieldElement.soundsDirectory = null;

const field = new MathfieldElement();

// Prose is the common case in a chat and mathematics is the interruption, so the field opens
// in text: letters stay upright, spaces are kept, and nothing is italicised until asked for.
field.defaultMode = "text";

// Off deliberately. Its job is to guess which mode you meant from what you typed, and the
// guess is both unnecessary now that "$" is an explicit switch and the origin of the italic
// prose this composer is being fixed for.
field.smartMode = false;

// No on-screen keyboard: this is a desktop app with a real keyboard, and the virtual one
// would cover the transcript being asked about.
field.mathVirtualKeyboardPolicy = "manual";

document.getElementById("host").appendChild(field);
field.mode = "text";

// Only settable once mounted — it throws "Mathfield not mounted" otherwise, which would take
// the rest of this file down with it and leave window.composer undefined.
field.menuItems = [];

// ── "$" toggles between prose and mathematics ────────────────────────────────────────────
//
// Intercepted in the capture phase rather than registered as a keybinding. MathLive already
// switches text to math on "$" through a path that is not in the keybinding table, so an added
// binding does not replace that — both fire for one keypress and the mode ends up back where
// it started. Handling the key here, before MathLive sees it, makes the toggle the only thing
// that runs and keeps it symmetric in both directions.
field.addEventListener(
  "keydown",
  (event) => {
    if (event.key !== "$") {
      return;
    }

    event.preventDefault();
    event.stopImmediatePropagation();
    field.executeCommand(["switchMode", field.mode === "math" ? "text" : "math"]);
  },
  true);

// ── Turning the field's value into what the student meant to say ─────────────────────────

/// LaTeX escapes MathLive emits for ordinary characters typed in TEXT mode. Sending these
/// verbatim would show the tutor "50\%" and "x\textasciicircum2" rather than "50%" and "x^2".
const TEXT_ESCAPES = [
  ["\\textasciicircum", "^"],
  ["\\textasciitilde", "~"],
  ["\\textbraceleft", "{"],
  ["\\textbraceright", "}"],
  ["\\textbackslash", "\\"],
  ["\\_", "_"],
  ["\\%", "%"],
  ["\\&", "&"],
  ["\\#", "#"],
  ["\\$", "$"],
];

function unescapeProse(text) {
  let out = text;
  for (const [escaped, plain] of TEXT_ESCAPES) {
    out = out.split(escaped).join(plain);
  }
  return out;
}

/// Finds the index of the "}" that closes a "{" at `open`, honouring nesting and backslash
/// escapes. Returns -1 when the brace is never closed.
function matchBrace(latex, open) {
  let depth = 0;

  for (let i = open; i < latex.length; i++) {
    if (latex[i] === "\\") {
      i++; // an escaped character is never a brace
      continue;
    }

    if (latex[i] === "{") {
      depth++;
    } else if (latex[i] === "}") {
      depth--;
      if (depth === 0) {
        return i;
      }
    }
  }

  return -1;
}

/// Converts the field's LaTeX into the chat text the tutor receives.
///
/// MathLive has two ways of writing the same mixed content, depending on the mode the value
/// was built in, and they are exact inverses:
///
///   text-first (what typing here produces)   prose bare, mathematics inside $...$
///   math-first (what setValue parses)        mathematics bare, prose inside a \text{} run
///
/// Reading one with the other's rule is not a cosmetic error. Read the math-first shape as if
/// it were text-first and a fraction leaves as a bare control sequence with no delimiters
/// around it: the tutor is handed raw LaTeX and the transcript renders none of it. Which shape
/// is in hand is settled by whether there is a \text{} run in it at all, so both are handled
/// rather than betting on one.
///
/// Output always uses "$...$" for mathematics — the notation the tutor prompt asks the model
/// for and exactly what the transcript's KaTeX draws, so what is sent, what the tutor reads,
/// and what appears on screen all agree.
function latexToChatText(latex) {
  const bareIsProse = !latex.includes("\\text{");

  let out = "";
  let buffer = "";
  let i = 0;

  const flushBare = () => {
    if (bareIsProse) {
      out += unescapeProse(buffer);
    } else {
      const math = buffer.trim();
      if (math.length > 0) {
        out += "$" + math + "$";
      } else if (buffer.length > 0) {
        out += " "; // whitespace between two runs is spacing, not an empty formula
      }
    }

    buffer = "";
  };

  const emitProse = (text) => {
    if (bareIsProse) {
      buffer += text;
    } else {
      flushBare();
      out += text;
    }
  };

  while (i < latex.length) {
    // A math zone, in either shape: kept with its delimiters, contents untouched.
    if (latex[i] === "$") {
      const close = latex.indexOf("$", i + 1);
      if (close < 0) {
        buffer += latex[i];
        i++;
        continue;
      }

      flushBare();
      const inner = latex.slice(i + 1, close).trim();
      if (inner.length > 0) {
        out += "$" + inner + "$";
      }

      i = close + 1;
      continue;
    }

    // An explicit prose run, unwrapped so the tutor is not shown the \text{} wrapper.
    if (latex.startsWith("\\text{", i)) {
      const open = i + "\\text".length; // index of the "{"
      const close = matchBrace(latex, open);
      if (close < 0) {
        buffer += latex[i];
        i++;
        continue;
      }

      emitProse(unescapeProse(latex.slice(open + 1, close)));
      i = close + 1;
      continue;
    }

    // An escape sequence has to be consumed whole, or a two-character one would be split and
    // its backslash treated as ordinary content.
    if (latex[i] === "\\") {
      const match = /^\\[a-zA-Z]+|^\\[\s\S]/.exec(latex.slice(i));
      if (match) {
        buffer += match[0];
        i += match[0].length;

        // In LaTeX a single space after a control WORD terminates the name rather than being
        // content, so "\textbraceleft y" means "{y", not "{ y". MathLive emits an explicit
        // "{}" when a real space has to follow one.
        if (/^\\[a-zA-Z]+$/.test(match[0]) && latex[i] === " ") {
          i++;
        }

        continue;
      }
    }

    buffer += latex[i];
    i++;
  }

  flushBare();
  return out;
}

function currentText() {
  return latexToChatText(field.getValue("latex")).trim();
}

function post(message) {
  if (window.chrome && window.chrome.webview) {
    window.chrome.webview.postMessage(JSON.stringify(message));
  }
}

const placeholder = document.getElementById("placeholder");

function syncPlaceholder() {
  const empty = field.getValue("latex").trim().length === 0;
  placeholder.classList.toggle("hidden", !empty);
  return empty;
}

let lastReportedHeight = 0;

/// The WPF host cannot measure a WebView's content, so the page says how tall it needs to be —
/// a field holding a fraction is taller than one holding a line of prose. Reported only on a
/// change, because setting the height re-lays out the page and would otherwise bounce straight
/// back through here.
function reportHeight() {
  const height = Math.ceil(field.getBoundingClientRect().height) + 4;
  if (height !== lastReportedHeight) {
    lastReportedHeight = height;
    post({ type: "resize", height });
  }
}

field.addEventListener("input", () => {
  post({ type: "input", empty: syncPlaceholder() });
  reportHeight();
});

// Enter sends, the way every chat box does. Captured on the way down because MathLive binds
// Enter itself (it commits the field), and letting it run first would swallow the key.
// Shift+Enter is left to MathLive so a multi-line expression is still possible.
field.addEventListener(
  "keydown",
  (event) => {
    if (event.key === "Enter" && !event.shiftKey) {
      event.preventDefault();
      event.stopPropagation();
      post({ type: "submit", text: currentText() });
    }
  },
  true);

// Called from C# via ExecuteScriptAsync.
window.composer = {
  getText: currentText,

  clear: () => {
    field.setValue("");
    field.mode = "text"; // a new question starts in prose, whatever the last one ended in
    syncPlaceholder();
    reportHeight();
  },

  focus: () => field.focus(),

  /// Mirrors the send button's disabled state, so a turn already in flight cannot be started
  /// again by typing into a field that still looks live.
  setEnabled: (enabled) => {
    field.readOnly = !enabled;
    document.body.style.opacity = enabled ? "1" : "0.55";
  },
};

// Clicking anywhere in the composer strip focuses the field, the way a chat box behaves: the
// field's own box is only as tall as its content, so the padding around it would be dead.
//
// The pointerType is forwarded because Windows will not raise its touch keyboard for a custom
// element inside a WebView the way it does for a native text box — the host has to ask for it.
// Only touch and pen qualify: someone on a mouse has a keyboard already, and popping one up
// over their work would be worse than not having it.
document.body.addEventListener("pointerdown", (event) => {
  if (event.target !== field && !field.contains(event.target)) {
    event.preventDefault();
    field.focus();
  }

  post({ type: "pointer-focus", pointerType: event.pointerType || "mouse" });
});

syncPlaceholder();
reportHeight();
post({ type: "ready" });
