// Bridge between MainWindow.xaml.cs and this page. Called via WebView2's ExecuteScriptAsync
// with JSON-encoded string arguments, so C# never has to build HTML — every message is set
// with textContent (never innerHTML), which the browser escapes automatically. KaTeX's
// auto-render then walks the already-safe text nodes and swaps $...$-delimited spans for
// rendered math; nothing here ever parses model output as HTML.
//
// balanceMath / renderMathIn / KATEX_DELIMITERS come from math-render.js, which the
// composer's live preview also loads — see there for why they are shared and not copied.

function addMessage(role, text) {
  const container = document.getElementById("messages");
  const bubble = document.createElement("div");
  bubble.className = role === "user" ? "msg msg-user" : "msg msg-tutor";
  bubble.textContent = balanceMath(text);
  container.appendChild(bubble);
  renderMathIn(bubble);
  window.scrollTo(0, document.body.scrollHeight);
}

// ── Streaming a reply in as it arrives ───────────────────────────────────────────────
//
// The raw text is accumulated here rather than read back off the element, because the
// element's contents stop being the source of truth the moment KaTeX rewrites them into
// spans — appending to that would corrupt every formula already rendered.
//
// Math is deliberately NOT rendered per delta. A formula arrives a few characters at a
// time, so mid-stream it is usually an unclosed "$" — exactly the state that makes KaTeX
// swallow the following prose into one italic run. Rendering once, at the end, on text that
// has been through balanceMath is what keeps a half-arrived reply readable.

let streamingBubble = null;
let streamingRaw = "";

function beginStreamingMessage() {
  const container = document.getElementById("messages");
  streamingBubble = document.createElement("div");
  streamingBubble.className = "msg msg-tutor";
  streamingRaw = "";
  container.appendChild(streamingBubble);
  window.scrollTo(0, document.body.scrollHeight);
}

function appendToStreamingMessage(delta) {
  if (!streamingBubble) {
    beginStreamingMessage();
  }

  streamingRaw += delta;
  streamingBubble.textContent = streamingRaw; // plain text while in flight
  window.scrollTo(0, document.body.scrollHeight);
}

/// `finalText` is the authoritative reply as the app persisted it. Preferring it over the
/// locally accumulated text means the bubble always ends up showing exactly what was saved,
/// so a dropped delta or a transport that fell back to a non-streaming call self-corrects
/// here instead of leaving the transcript quietly disagreeing with the database.
function finishStreamingMessage(finalText) {
  if (!streamingBubble) {
    return;
  }

  const text = typeof finalText === "string" && finalText.length > 0 ? finalText : streamingRaw;
  streamingBubble.textContent = balanceMath(text);
  renderMathIn(streamingBubble);
  streamingBubble = null;
  streamingRaw = "";
  window.scrollTo(0, document.body.scrollHeight);
}

/// Drops the in-flight bubble — used when a turn is refused (over budget) or fails, so an
/// empty grey box is not left sitting in the transcript.
function cancelStreamingMessage() {
  if (streamingBubble) {
    streamingBubble.remove();
    streamingBubble = null;
    streamingRaw = "";
  }
}

function clearMessages() {
  streamingBubble = null;
  streamingRaw = "";
  document.getElementById("messages").replaceChildren();
}
