# Changelog

All notable changes to the Markdown Preview Handler are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.0] — 2026-08-30

The toolbar is there when you arrive, a document you wrote yourself can be let
onto the network, and a link to the file next door turns the page instead of
leaving the pane.

### Added

- **View source.** A toggle between the options gear and the expand control
  swaps the rendered document for the file exactly as it sits on disk — front
  matter included, nothing tidied — so you can see what a document actually
  contains rather than what it renders to. A **Syntax highlighting** option in
  the options row colours the raw Markdown; it is off by default, and reads as
  inert while the rendered view is showing, since that is the only place it
  applies.

  The setting is remembered for every document rather than per file: someone
  checking raw Markdown is usually checking several. Search still works, and
  finding a link or a heading in the raw file is much of the point. The
  controls that mean nothing against plain text — contents, checkbox editing,
  trust — disable themselves and say that source view is the reason, rather
  than blaming the document.

  The highlighting is the previewer's own, not highlight.js's Markdown
  grammar, which answered none of the questions a reader of raw Markdown
  actually has. Front matter is one colour end to end, delimiters included,
  with its keys picked out by weight rather than a second colour. A fence and
  its closing partner match, language tag included, and the body between them
  is handed to highlight.js under the language the fence declares — so a
  ```csharp block reads as C#, not as Markdown. Table pipes and the alignment
  row share one colour and the heading row is bold. Mathematics is coloured,
  display and inline, delimiters and all.

  Every colour is a palette token the chrome already uses, so all seven themes
  are covered without a single new value and a theme added later inherits it.
  Measured against each theme's own source background, the weakest token comes
  in at 4.57:1 — above the 4.5:1 needed for body text everywhere. Two things
  that measurement caught: the block now sets its own text colour instead of
  inheriting one this stylesheet does not own, which the weight-only tokens
  depended on; and front matter, HTML, quotes and rules moved off the
  chrome-label grey, which reads at 4.1:1 on the lighter palettes.

- **Expand the preview to the full pane width.** A toggle in the view controls
  gives the document every pixel the pane has: the centred reading measure goes
  and the reading gutter shrinks. Toggling back restores the previous width
  exactly, and the width keeps one definition in the stylesheet so there is no
  second copy to drift out of step. The glyph shows which way the next click
  goes, and the choice persists across documents.

  What it cannot do is make Explorer's preview pane itself any wider — that
  splitter belongs to Explorer. On a pane narrower than the reading measure the
  document already fills the width, so there is nothing to expand into — and
  the toggle knows it: it measures the actual margin and disables itself,
  saying so, until the pane is wide enough for expanding to visibly do
  something. Measured rather than compared against a hard-coded 980, so the
  stylesheet stays the only place the reading measure is defined.

- **Editable task checkboxes.** A toggle in the toolbar's options row makes GFM
  task lists clickable. Checking or unchecking a box writes the change to the
  file immediately, with no prompt and no confirmation — the toggle's tooltip is
  where that is disclosed, before it is turned on. The preference is per user,
  not per document.

  This is the previewer's only write path, and it is deliberately the narrowest
  one available. The file is never regenerated from the rendered page: on each
  click the host re-reads it from disk, decodes it with its own encoding,
  re-encodes it unchanged to prove the round trip is byte-faithful, verifies
  that the named line really is a task marker in the state the page believed,
  and only then flips that single character. Everything else — encoding, BOM,
  CRLF or LF, trailing whitespace, the bytes either side of the marker — comes
  through untouched, and the write goes via a temp file and an atomic replace.

  Anything unexpected refuses rather than guesses: a file whose bytes would not
  survive the round trip, a line that is not a task, a marker already in the
  requested state (the file changed under the preview), a read-only or vanished
  file. A refusal re-renders from what is actually on disk, so the checkbox
  snaps back to the truth instead of showing a change that never landed. The
  toggle disables itself, and says why, for documents with no file behind them
  and for truncated ones.

  Checkbox positions are resolved by source line rather than by counting boxes,
  so a `- [ ] like this` inside a fenced code block neither becomes a checkbox
  nor shifts the real ones below it, and front matter the parser never sees is
  counted back in.

- **Follow local links in Explorer.** A mode switch in the toolbar's options
  row decides where a click on a link to a nearby file goes. The default is to
  steer File Explorer itself: the hosting tab navigates to the file's folder
  and selects it, which makes the linked file the new preview — so a README's
  link to `CHANGELOG.md` reads like a page turn rather than a detour through
  another program. The other mode is the previous behaviour, opening the file
  in its default application. The glyph shows what a click will do right now —
  a crosshair for reveal, an arrow leaving a box for open — because neither
  mode is the "off" one. The choice persists.

  Revealing works for any file that exists, extension or not, since selecting a
  file executes nothing. Opening in the default app keeps its inert-type
  allowlist: a document must not be one click away from running a script it
  shipped alongside itself. A link to a folder navigates into it; a link to
  something that is not there does nothing, as before.

  The hosting tab is identified by two facts together — the Explorer window our
  preview pane lives inside, and the folder that window is currently showing —
  which is what picks the right tab out of a Windows 11 window that has several.
  When no Explorer tab hosts the preview (the dev harness, Outlook's reading
  pane, a document inside a .zip), a folder window is opened with the file
  selected instead. A link to a file in the folder already on screen skips
  navigation entirely and just moves the selection.

- **Trust external links.** A globe toggle in the toolbar's options row lets a
  single document load images from the internet, which are otherwise blocked
  for everyone. Turning it on requires answering a dialog that names the file
  and says plainly what the grant costs — remote servers learn the file was
  opened, along with the reader's IP address, which is how tracking pixels
  work. Cancel is the focused default and `Esc` chooses it. Turning trust off
  asks nothing: confirming the removal of a permission only teaches people to
  click through the dialog that matters.

  The grant is recorded per file, by full path, under
  `HKCU\Software\MarkdownPreviewer\TrustedDocuments`, and survives restarts —
  one value per document, so the list can be read and cleared with regedit
  alone. It is deliberately HKCU-only: trust is a personal judgement about a
  specific file, not something an administrator grants on a user's behalf. An
  item with no file behind it (a stream pulled from a zip, a search-index hit)
  cannot be trusted; the toggle disables itself and says why.

  The grant is narrow. It lifts exactly one restriction — the host's refusal to
  serve http(s) image requests. Scripts, frames, forms and outbound
  connections stay blocked by CSP for trusted and untrusted documents alike.

  While a document is not allowed onto the network, its remote images are never
  given a `src` at all, so no request is issued and nothing can be answered from
  the browser's cache. That is what makes withdrawing trust take effect on the
  spot rather than leaving already-fetched images on screen. Each one renders as
  a dashed amber placeholder holding its alt text, which says how to undo it —
  distinct from the grey placeholder used for an image whose path simply cannot
  be resolved. The host's refusal is untouched and remains the security
  boundary; it still covers routes the page never sees, such as a remote URL in
  a `style` attribute.

### Changed

- **The trust toggle says what it actually grants.** Its tooltips now name
  external *image* links — the only thing trust unlocks — and the toggle
  disables itself, saying so, for a document that has no external image links
  at all: with nothing to grant, an enabled switch is a promise the click
  cannot keep. Images withheld pending trust count as present, since they are
  exactly what trusting would reveal.

- **Tooltips are part of the document, not the operating system.** The native
  `title` could not take the theme, broke lines wherever it liked, and waited
  on a delay outside our control — which showed most on the checkbox toggle,
  whose disclosure ran to a single unbroken sentence. Every toolbar control now
  uses a themed tooltip drawn from the same palette tokens as the rest of the
  chrome, with a label on the first line and the detail beneath it. Keyboard
  focus raises them as well as hover, and `Esc` dismisses them.

- **The options and expand controls moved to the right of the bar**, between
  the Contents toggle and the close button, where the view controls belong —
  they were trailing the search field, which read as though they were part of
  it. The gear's tooltip is now just "Options": the row it opens has held
  document and view settings as well as search ones for a while.

- **Previous/next match moved inside the search field**, as a split pair at its
  right edge beside the match count, and they appear only when there are at
  least two matches — with one there is no "next" to reach, and with none they
  were two dead controls. The field reserves exactly as much room as the count
  and buttons occupy, so a long count like `1/1247` can never slide underneath
  them.

- **Checkbox editing is remembered per document rather than per user**, under
  `HKCU\Software\MarkdownPreviewer\TaskEditDocuments`. The answer genuinely
  differs by file: a personal checklist is one to tick straight from the
  preview, a README you are only reading is one to leave alone. The toggle also
  stays disabled — and says which reason applies — for a document with no task
  list in it at all, as well as for one with no file behind it.

- **The toolbar is visible by default.** It is the only way to reach search,
  the contents rail, the theme selector and the trust toggle, so a document no
  longer opens with all of that hidden behind `Ctrl+F`. `Esc` or the `×` still
  dismisses it, scoped to the document on screen; the next selection brings it
  back. (Closing genuinely closes — the substance of issue #1 — it is simply
  not remembered as a standing preference.)
- **The close button sits at the far right of the bar**, and the Contents
  button's left edge now lands exactly on the contents rail's left edge. Both
  widths come off one `--mdp-toc-width` token so they cannot drift apart.
- **The search field says whether it found anything.** Its border is green
  while there are matches and red while there are none — including when the
  reason for none is an unfinished or invalid regular expression, which used
  to look identical to a search that simply missed. Focus alone is now
  neutral: the accent colour is orange in some palettes, and an orange ring
  around a search box reads as an error.
- **Toolbar glyphs are 24px stroked icons** with 2px of padding, replacing the
  Unicode characters, so the controls are a uniform size and optically match
  each other rather than the font they were drawn in. The Contents chevron is
  one icon rotated rather than two glyphs swapped.

### Fixed

- **The uninstaller now removes the per-user registry tree.** It deliberately
  left `HKCU\Software\MarkdownPreviewer` behind so a reinstall would inherit
  settings, which was unnecessary — upgrades install straight over the top and
  never uninstall first — and that key is where the document lists live: the
  paths of every file trusted for remote content and every file with checkbox
  editing turned on. A record of what was opened is not a preference worth
  surviving an uninstall. It reaches the account running the uninstaller;
  other users on the same machine keep their own copy until they remove it.

- **Clicking any in-page anchor silently killed the page's voice.** The host
  accepts messages only from its own render page, but the comparison demanded
  the page's exact URL — and following a heading link or a contents-rail entry
  gives that URL a `#fragment`, which WebView2 reports as part of the message
  source. From that click onward every message the page sent was rejected:
  the trust toggle did nothing, and every subsequent render died on its
  30-second timeout, leaving the pane stuck until the shell rebuilt the
  handler. It presented as "trust works in some folders and not others",
  because whether it worked depended on whether the reader had used the table
  of contents yet in that pane. The source check now strips the fragment;
  scheme, host and path still have to match exactly, and a query string is
  still refused.

- **The search belongs to the document.** Explorer reuses one page for every
  selection, so a query typed against one file used to follow the reader to the
  next — still in the box, still showing a match count, describing a document
  they had already left. Moving to another file now clears the query, the count
  and the state colour. Redrawing the *same* document keeps them, so changing
  the theme or the trust setting no longer wipes out a search in progress; the
  host distinguishes the two by document identity rather than the page
  guessing.

## [1.3.0] — 2026-08-30

The chrome grew up: one toolbar instead of floating panels, seven themes, and
a search that can read pictures.

### Added

- **Seven themes.** System (follows the host's light/dark resolution, stock
  GitHub palettes) plus six named palettes — Paper, Arctic and Ledger (light),
  Harbor, Midnight and Carbon (dark) — that re-tint the document and the
  chrome together from one seventeen-token contract. Picked from a selector at
  the right edge of the toolbar's options row; the choice persists. Named
  palettes carry their own lightness, so the Windows colour mode stops
  mattering while one is active. Syntax colours inside code fences keep the
  GitHub light/dark set matching the palette's lightness.
- **Search reads pictures.** SVG images embedded with `<img>` — previously
  opaque, since such an SVG is a separate document the page cannot see into —
  now have their visible text extracted by the host (locked-down XML reader:
  DTDs prohibited, style/script/defs skipped, size-capped, cached by
  write-time) and matched like any other text. Every image's alt and title
  text counts too. A match boxes the whole image and joins the cycle order.
- The context menu's *Find…* and *Toggle table of contents* now open the
  toolbar as a whole.

### Changed

- **One toolbar replaces the floating panels.** Search, its options row
  (match case, whole word, regex, theme selector) and the Contents toggle live
  in a fixed bar at the top; the document always starts below it. The contents
  panel is now a rail docked under the toolbar on the right (it may hang over
  the document — it is chrome the reader summoned), toggled from the toolbar,
  disabled for documents with fewer than two headings. One `×` (or `Esc`)
  closes everything. The find bar's drag grip, the separate close buttons and
  the collapse-to-pill behaviour are gone with the panels they belonged to.
- **The WebView2 profile is partitioned per host executable.** The
  shared-browser compatibility check includes the host executable's identity,
  so two different host programs (prevhost and Outlook's reading pane, or the
  dev harness) could collide on one profile and fail with `0x8007139F`
  regardless of the 1.1.0 retry. Explorer's prevhosts still share one browser;
  different hosts can no longer collide at all.

### Fixed

- Searching while an image was still loading could box it at the wrong place;
  overlays are repositioned when images finish loading and when the pane is
  resized.

## [1.2.0] — 2026-08-30

Search grew the switches people expect from an editor, and learned to look
inside rendered diagrams.

### Added

- **Search options**, behind a gear in the find bar: **match case**, **match
  whole word**, and **regular expression**. The three are independent and
  combine freely, and the choices persist. Combining whole word with a regular
  expression wraps the pattern as `\b(?:pattern)\b`, so a pattern that begins
  or ends with a non-word character — `\d+\.`, `-foo` — cannot match; that is
  inherent to word boundaries and matches how editors with both switches
  behave. An unfinished or invalid pattern says `bad pattern` on the bar
  instead of silently reporting nothing found.
- **Text inside rendered diagrams is searchable.** A Mermaid diagram draws its
  labels as SVG, which the search skipped entirely — searching a document for
  `Domain` found nothing even with `Domain` plainly visible in the chart. Those
  labels are now matched, counted, and reachable with the cycle arrows like any
  other match. They cannot be wrapped in `<mark>` (an HTML element inside
  `<svg>` does not render), so each one is boxed by a highlight drawn over it,
  which follows the diagram as the page scrolls and is redrawn if the pane is
  resized.

### Changed

- **The close buttons on the find bar and the contents panel are larger.** At
  the size of the cycle arrows beside them they were an easy thing to miss.

## [1.1.1] — 2026-08-30

Fixes [#1](https://github.com/Cadtastic/MarkdownPreviewer/issues/1). The find
bar shipped in 1.1.0 could not be hidden, which made it a permanent overlay on
every document rather than something you opened when you needed it.

### Fixed

- **The find bar was visible on every document and could not be closed.** Both
  overlays are shown and hidden by setting the `hidden` attribute, but each
  panel's own `display: flex` outranked the user-agent rule that `hidden`
  relies on, so the attribute had no effect on screen. The close button, the
  `Esc` key and the "fewer than two headings" rule for the contents panel were
  all working the whole time — nothing was listening.
- **The find bar covered the start of the document.** While it sits in its
  default position the document now reserves a strip for it, so it cannot
  obscure the title.
- **The contents panel's `×` did not hide it**, and the panel appeared on
  documents with no headings. Same cause, same fix.
- **`Send tab to your devices` appeared in the context menu.** It syncs a page
  URL to another signed-in device, and ours is a process-local address that
  means nothing elsewhere. Also removed, when the click lands on a link: open
  in new window/tab and save-link-as. Copying a link address stays.
- **The Find entry rendered as `Find…→Ctrl+F`.** WebView2 draws a tab in a menu
  label literally instead of aligning an accelerator column.
- Stripping browser entries left the separators that framed them, so the menu
  opened with a stray rule and gaps. Leading, trailing and doubled separators
  are now collapsed.

### Added

- **The find bar can be moved.** Drag it by the grip at its left edge; the
  position is remembered for the next document and re-clamped if the pane is
  resized smaller than where it was left.
- With `LogLevel` at `0` the log now records the context menu both as the
  browser offered it and as it was shown, which is what made the stray entry
  identifiable rather than guesswork.

## [1.1.0] — 2026-08-29

The first release after 1.0.0's field testing. Three of the fixes below are for
failures that made the pane show nothing at all, so upgrading is worthwhile even
if none of the new features appeal.

### Added

- **Find in page.** `Ctrl+F` opens a find bar over the rendered document:
  case-insensitive, every match highlighted with the current one accented,
  `Enter` / `Shift+Enter` (and `F3`) to cycle with wrap-around, a live
  `n/total` counter, and `Esc` to close. Also reachable from the right-click
  menu. Matches inside collapsed front matter open their block automatically.
- **Floating table of contents.** Built from the document's headings, with its
  own scrollbar. Collapse it to a "Contents" pill with the chevron, hide it
  entirely with `×`, and bring it back from the right-click menu. Both states
  persist across documents. Suppressed for documents with fewer than two
  headings and on panes narrower than 640 px.
- **Share and copy the document.** Right-click offers *Share document…*, which
  opens the Windows share sheet with the actual file, and *Copy document*,
  which puts the file on the clipboard for pasting into mail or chat. The
  browser's built-in Share entry — which shared an internal URL meaningless
  outside the preview process — is gone.
- **Relative links open the real file.** Clicking `docs/architecture.md` in a
  README opens that file in its default application, instead of trying to
  navigate to a dead internal address. Restricted to files inside the
  document's own folder tree and to inert document types.
- **`AllowRemoteImages` setting** (default `0`), covered under *Changed*.
- **Verbose diagnostics.** With `LogLevel` set to `0`, the log now records the
  shell entry points, window geometry and visibility, which binary is serving
  the preview, and the identity of each document read — enough to diagnose a
  blank pane from the log alone.

### Changed

- **Raw HTML now renders by default** (`AllowRawHtml` defaults to `1`).
  GitHub-style READMEs lean heavily on raw HTML — `<p align="center">`, badge
  rows, `<details>` — and rendering that as escaped source read as broken. What
  reaches the page is still the sanitised form: `<script>`, `<iframe>`,
  `<form>`, every `on*` handler and any `javascript:`/`file:` URL are stripped
  before display, with CSP forbidding inline script and network access
  independently. Set the value to `0` for strictly-Markdown rendering.
- **Remote images are a per-user opt-in** (`AllowRemoteImages`, default `0`).
  Remote images are how tracking pixels work, and a passive previewer should
  not announce which files you looked at. With the setting off, badge images
  show as labelled placeholders; turn it on to load them.
- **The document folder is served per request** rather than through a WebView2
  folder mapping, so a folder change takes effect immediately without
  re-navigating the page.
- **The installer asks before replacing another preview handler.** If a
  Markdown extension is already handled by something else, setup names the
  incumbent and offers to keep it. Silent installs (`/S`) replace, as before.
- **The installer's README link and project URL** point at the correct
  repository, and the finish page opens the README on GitHub *before*
  restarting Explorer rather than after.

### Fixed

- **Preview pane stayed blank while rendering reported success.** The host
  window was a WinForms `Form` with `WS_CHILD` grafted on; its top-level
  visibility machinery never applied `WS_VISIBLE`, so WebView2 painted into a
  window that was never shown. It is now a plain child control.
- **"Markdown preview could not start" — `CoInitialize has not been called`
  (`0x800401F0`).** Managed COM objects are apartment-agile, so the shell's
  calls arrive on arbitrary RPC worker threads with no STA and no message
  pump — regardless of the registered `ThreadingModel`. The handler now owns a
  dedicated STA thread with a real message loop and marshals every window and
  browser call onto it.
- **"Markdown preview could not start" — `The group or resource is not in the
  correct state` (`0x8007139F`).** The browser environment was cached for the
  life of the preview host but was only ever as alive as the browser process
  behind it. When that process went away — a WebView2 runtime update, a crash,
  or the shell reaping it while no preview was open — every later preview in
  that host failed, which is why closing all Explorer windows fixed it until
  the next time. The cached environment is now discarded when the browser
  exits, and initialisation retries once with a fresh one.
- **A browser crash no longer disables the pane permanently.** The dead surface
  is discarded and the next selection rebuilds it.
- **Only the second preview in a host worked.** The browser environment is
  affine to the thread that created it, and each handler was creating its own
  UI thread; the second handler in a host failed outright. All handlers in a
  process now share one preview UI thread.
- **Relative images did not resolve, and documents had no filename.** Explorer
  prefers stream-based initialisation, which carries no path. The handler now
  initialises from the shell item, which yields the real path; stream-only
  items (inside a `.zip`, search results) still work and now recover their
  name for logs and messages.
- **Find counted invisible text.** Rendered Mermaid diagrams inject a
  stylesheet full of internal selectors; searching for "mermaid" reported 146
  matches on a document containing two. Text inside `<style>`, `<script>`,
  `<svg>` and MathJax containers is excluded.

## [1.0.0] — 2026-07-29

Initial release.

- `IPreviewHandler` shell extension rendering Markdown in the Explorer preview
  pane, hosted out-of-process in `prevhost.exe`.
- markdown-it rendering with GitHub-flavoured tables, task lists and autolinks,
  styled with `github-markdown-css`.
- Syntax highlighting (highlight.js, 64 languages), Mermaid diagrams and
  MathJax typesetting, with the two heavyweight bundles loaded only when a
  document needs them.
- Light/dark theming that follows the Windows apps colour mode.
- Per-user and machine-wide settings under `Software\MarkdownPreviewer`.
- NSIS installer with .NET and WebView2 prerequisite detection, plus
  registration, diagnostic and Explorer-restart scripts.

[1.4.0]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.4.0
[1.3.0]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.3.0
[1.2.0]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.2.0
[1.1.1]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.1.1
[1.1.0]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.1.0
[1.0.0]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.0.0
