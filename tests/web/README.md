# Render page tests

`preview.js` is where most of the behaviour actually lives — Markdown parsing, URL
resolution, the security posture, and the lazy-loading decisions that determine
whether a preview takes 40 ms or 900 ms. None of that is reachable from the .NET
test project, so it is covered here instead, under jsdom.

```bash
cd tests/web
npm install
npm test
```

## What is covered

| Area | Why it is tested here |
| --- | --- |
| Markdown, tables, anchors, highlighting | The visible output. Anchor slugs must match GitHub's so hand-written `[](#heading)` links work. |
| Task lists | Implemented as a DOM pass, including the loose-list case where markdown-it inserts a `<p>`. |
| Front matter | Rendering it as Markdown turns `---` into an `<hr>` and the first key into an `<h2>`. |
| Relative image resolution | Rewriting to the `doc.mdpreview.invalid` host is the only reason local screenshots appear. |
| Document link routing | A link to a sibling file must reach the host as `openDocument`, not open a dead virtual-host URL. |
| Dangerous URL schemes | `javascript:`, `vbscript:`, `file:`, non-image `data:`. |
| Raw HTML sanitiser | On by default, so it stands between every previewed file and the DOM. |
| Find in page | Match counting, wrap-around cycling, highlight removal, and the exclusion of invisible `<style>` text. |
| Panel visibility | Whether the find bar, options panel and contents panel are *actually* hidden — computed display, not the `hidden` property. |
| Search options | Case, whole word and regex independently and combined, plus an invalid pattern reporting rather than throwing. |
| Diagram search | Labels drawn inside an `<svg>` are matched, boxed by an overlay, and reachable with the cycle arrows. |
| Table of contents | Built per render, collapse and hide states, and reshow via the host message. |
| Host ↔ page protocol | Token echo, completion reporting, external-link routing. |
| Theme switching | Stylesheets toggle via `media`, not the `disabled` attribute. |
| Lazy asset loading | Mermaid is 3.5 MB and MathJax 2.1 MB. Loading either unnecessarily is a visible regression. |
| Graceful degradation | A failed asset load must still complete the render, or the host waits out its 30 s timeout. |

## Three things the tests exist to prevent recurring

**The token echo bug.** The page must reply with the token the *host* sent, not a
counter of its own. An earlier version kept a page-local sequence; the two
diverged immediately, the host discarded every reply as stale, and every single
preview died on the 30-second timeout. `host token N echoed verbatim` guards this.

**Silent asset hangs.** A `<script>` that fires neither `load` nor `error` used to
leave the render promise unsettled forever. `loadAsset` is now bounded, and
`render still reports completion` proves a failed bundle degrades to a warning.

**Asserting the attribute instead of the effect.** The suite used to check
`element.hidden === true`, which was true throughout the life of issue #1 while
both panels stayed on screen — author CSS was overriding the user-agent rule
that `hidden` relies on. The suite now inlines `preview.css` into the jsdom
document and asserts `getComputedStyle(el).display`, so the cascade is part of
the test rather than an assumption. Reverting the fix turns five assertions red.

**Counting text nobody can see.** Find walked every text node, including the
`<style>` block a rendered mermaid diagram injects — so searching for "mermaid"
reported 146 matches on a document containing two. `style/svg internals excluded
from matches` guards the filter that fixed it.

## Known limitations

jsdom has no layout engine and cannot execute Mermaid or MathJax, so the tests
verify that those bundles are *requested* under the right conditions and that
failures degrade correctly — not that the resulting SVG is correct. Use the
Harness project (`src/MarkdownPreviewer.Harness`) against `samples/kitchen-sink.md`
for that.
