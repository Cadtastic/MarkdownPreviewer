# Changelog

All notable changes to the Markdown Preview Handler are recorded here.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and
this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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

[1.1.1]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.1.1
[1.1.0]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.1.0
[1.0.0]: https://github.com/Cadtastic/MarkdownPreviewer/releases/tag/v1.0.0
