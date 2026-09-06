// Math rendering shared by the transcript (chat.js) and the composer's preview
// (composer.js).
//
// Shared rather than copied because the preview's whole claim is "this is what the tutor's
// chat will show". A second copy of the delimiter list or the balancing rule would be one
// edit away from quietly making the preview a liar — which is worse than having no preview,
// since the student would trust it.

const KATEX_DELIMITERS = [
  { left: "$$", right: "$$", display: true },
  { left: "\\[", right: "\\]", display: true },
  { left: "$", right: "$", display: false },
  { left: "\\(", right: "\\)", display: false },
];

// Drops delimiters that never got a partner, so half-finished math cannot eat the prose.
//
// A reply cut off by the output-token limit routinely ends mid-formula, leaving an opening
// "$" with no closing one. KaTeX's auto-render then pairs it with a "$" further back in the
// message and typesets everything between as math: the words lose their spaces and go
// italic, and raw \int and \le show through. One truncated formula garbles the whole answer,
// so an unmatched opener is worth more as a literal dollar sign than as a delimiter.
//
// The composer needs exactly the same rule for a different reason: a formula is unbalanced
// for as long as it is being typed, and the preview must not reflow the sentence around it
// on every keystroke between the opening "$" and the closing one.
function balanceMath(text) {
  let out = "";
  let lastSingle = -1;
  let singles = 0;
  let i = 0;

  while (i < text.length) {
    if (text[i] === "\\" && i + 1 < text.length) {
      out += text[i] + text[i + 1]; // escaped char, never a delimiter
      i += 2;
      continue;
    }

    if (text.startsWith("$$", i)) {
      out += "$$";
      i += 2;
      continue;
    }

    if (text[i] === "$") {
      lastSingle = out.length;
      singles++;
    }

    out += text[i];
    i++;
  }

  // Odd count means exactly one opener is stranded: the most recent one.
  if (singles % 2 === 1 && lastSingle >= 0) {
    out = out.slice(0, lastSingle) + out.slice(lastSingle + 1);
  }

  return out;
}

function renderMathIn(element) {
  if (typeof renderMathInElement !== "function") {
    return; // KaTeX failed to load — plain text is still readable, just unrendered.
  }

  try {
    renderMathInElement(element, {
      delimiters: KATEX_DELIMITERS,
      throwOnError: false,
      errorColor: "#c9822a",
    });
  } catch {
    // A malformed delimiter pair shouldn't take down the whole message.
  }
}

/// Whether text contains anything the renderer would treat as math. The composer uses this
/// to stay out of the way: with no maths in the box there is nothing to preview, and a
/// duplicate of the sentence you are already looking at is just noise.
function containsMath(text) {
  return /\$|\\\(|\\\[/.test(text);
}
