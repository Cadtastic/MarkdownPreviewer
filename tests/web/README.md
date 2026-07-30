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
| Relative image resolution | The `doc.mdpreview.invalid` virtual host mapping is the only reason local screenshots appear. |
| Dangerous URL schemes | `javascript:`, `vbscript:`, `file:`, non-image `data:`. |
| Raw HTML sanitiser | Only runs when the user opts in, which makes it the least-exercised and highest-risk path. |
| Host ↔ page protocol | Token echo, completion reporting, external-link routing. |
| Theme switching | Stylesheets toggle via `media`, not the `disabled` attribute. |
| Lazy asset loading | Mermaid is 3.5 MB and MathJax 2.1 MB. Loading either unnecessarily is a visible regression. |
| Graceful degradation | A failed asset load must still complete the render, or the host waits out its 30 s timeout. |

## Two things the tests exist to prevent recurring

**The token echo bug.** The page must reply with the token the *host* sent, not a
counter of its own. An earlier version kept a page-local sequence; the two
diverged immediately, the host discarded every reply as stale, and every single
preview died on the 30-second timeout. `host token N echoed verbatim` guards this.

**Silent asset hangs.** A `<script>` that fires neither `load` nor `error` used to
leave the render promise unsettled forever. `loadAsset` is now bounded, and
`render still reports completion` proves a failed bundle degrades to a warning.

## Known limitations

jsdom has no layout engine and cannot execute Mermaid or MathJax, so the tests
verify that those bundles are *requested* under the right conditions and that
failures degrade correctly — not that the resulting SVG is correct. Use the
Harness project (`src/MarkdownPreviewer.Harness`) against `samples/kitchen-sink.md`
for that.
