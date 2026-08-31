# Architecture

This document covers the decisions that are not obvious from the code, and the
handful of constraints that are imposed on us rather than chosen.

## The shape of the thing

```
                    ┌──────────────────────────────────────┐
  explorer.exe ────►│ prevhost.exe  (COM surrogate, STA)   │
                    │                                      │
                    │  MarkdownPreviewHandler  (COM class) │  ← Presentation
                    │            │                         │
                    │            ▼                         │
                    │  PreviewSession       (use case)     │  ← Application
                    │       │        │                     │
                    │       ▼        ▼                     │
                    │  Readers    WebView2PreviewSurface   │  ← Infrastructure
                    │  Registry        │                   │
                    │  Logging         ▼                   │
                    │            msedgewebview2.exe        │
                    │             └─ index.html            │
                    │                └─ preview.js         │
                    └──────────────────────────────────────┘
```

Dependencies point inward. `Domain` and `Application` target plain `net8.0` rather
than `net8.0-windows`, which makes a violation of the Dependency Rule a compile
error instead of something a reviewer has to notice.

| Project | Knows about | Deliberately does not know about |
| --- | --- | --- |
| `Domain` | Documents, settings, themes | Files, registry, COM, WebView2 |
| `Application` | The order of operations, abstractions | Anything Windows-specific |
| `Infrastructure` | File IO, registry, `ShellExecute` | COM, WebView2, WinForms |
| `Rendering` | WebView2, WinForms | COM, the shell |
| `Shell` | COM interfaces, HWND lifetime | Markdown, rendering |

The payoff is concrete: `PreviewSessionTests` exercises the whole orchestration —
cancellation on selection change, settings fallback, failure conversion — with no
COM, no browser, and no running Explorer.

One thing that diagram does not show, and should: `PreviewSession` orchestrates
only *read → theme → render*. The interactive paths added since — the checkbox
write, the trust grant, revealing a linked file in Explorer — do not pass
through it at all. They run from `WebView2PreviewSurface`'s message handler
straight to Infrastructure services injected into its constructor. The
Dependency Rule still holds (Rendering depends inward on Application's
abstractions, never on Infrastructure's types), but the use-case layer is no
longer the single place where the order of operations lives, and a reader
looking for "what happens when the user clicks a checkbox" will not find it in
`Application`. If a fourth interactive path appears, that is the moment to move
them behind use cases rather than the moment after.

## Constraints we did not choose

### Why the .NET runtime is a hard dependency

.NET's COM hosting produces a native `*.comhost.dll` that `regsvr32` registers, and
**it cannot be deployed self-contained**. Publishing with `--self-contained true`
yields a DLL whose `DllRegisterServer` fails with `0x80008093`. This is documented
behaviour, not a bug we can work around.

The consequence is a real deployment requirement: the target machine needs the
.NET 8 **Desktop** Runtime (x64). The installer detects it and refuses to proceed
without it, because the alternative is a handler that registers cleanly and then
does nothing.

The escape hatch, if this dependency ever becomes unacceptable, is NativeAOT with
source-generated COM (`[GeneratedComClass]` plus a hand-written
`DllGetClassObject` export). That produces a genuinely standalone DLL. It also
rules out WinForms, which NativeAOT does not support — so the WebView2 host would
have to be rebuilt against the raw `ICoreWebView2Controller` on a plain Win32
window. That is a substantial piece of work for a dependency most machines already
satisfy, which is why it is a note here and not a branch.

### Why the AppID matters

`HKCR\CLSID\{…}\AppID` must point at `{6d2b5079-2f0b-48dd-ab7f-97cec514d30b}`, the
`prevhost.exe` surrogate. Without it the shell loads the handler **in-process**, and
an unhandled fault takes Explorer down with it. With it, the handler runs in an
isolated process the shell can restart.

This is also why every COM method in `MarkdownPreviewHandler` returns an HRESULT
and never throws: a managed exception crossing the boundary terminates the
surrogate, and the shell's response to a surrogate that keeps dying is to stop
showing previews at all.

### Why ThreadingModel must be Apartment — and why it is not enough

`regsvr32` on the .NET comhost writes `ThreadingModel = Both`. Preview handlers are
documented as `Apartment`, so the installer overwrites the value after
registration.

But for a managed handler that registration is a formality, not a threading
guarantee. Managed CCWs are **apartment-agile**: the runtime hands them across
apartments without proxies, so once `prevhost.exe` marshals `IPreviewHandler` back
to the shell, the calls arrive on arbitrary RPC worker threads — different threads
call by call, no STA, no promise `CoInitialize` ever ran, and nothing pumping
messages. WebView2 cannot even be created on such a thread
(`CO_E_NOTINITIALIZED` / `RPC_E_CHANGED_MODE`), and WinForms continuations posted
there queue against a window nothing pumps.

That is why the handler owns `PreviewUiThread`: a dedicated STA thread running a
real message loop, which the window, the session, and WebView2 all live on. The
COM entry points stay on whatever thread the RPC runtime chose and marshal every
UI touch across. This is the canonical managed preview-handler shape (the pattern
from the original MSDN managed preview-handler articles).

### Why `SetRegView 64` is in the installer

`makensis` produces a 32-bit executable. Without `SetRegView 64`, every
`HKLM\SOFTWARE\Classes` write is redirected into `WOW6432Node`, where 64-bit
Explorer will never look. The handler registers without error and is never called
— which is the single most confusing failure mode in this whole project.

## Decisions we did choose

### Client-side rendering in WebView2, not server-side Markdig

Markdig would render faster and drop the WebView2 dependency. It cannot render
Mermaid or MathJax, both of which are JavaScript engines with no .NET equivalent
of comparable fidelity. Since diagrams and math were the point, the renderer has to
be a browser, and once it is a browser, doing the Markdown parsing there too keeps
one pipeline instead of two.

### Lazy-loading Mermaid and MathJax

The always-on core is ~360 KB (markdown-it, markdown-it-anchor, highlight.js).
Mermaid is 3.5 MB and MathJax 2.1 MB. Loading all of it per document would make
arrow-keying through a folder of READMEs feel broken.

`preview.js` scans the source and injects each bundle only when the document
actually needs it, then caches it for the life of the process — so the *second*
diagram-bearing document is fast. A failed load is dropped from the cache so a
later document can retry.

Detection is deliberately conservative on the math side: single-`$` delimiters are
off by default because a document about pricing would otherwise render as
mathematics.

### Two virtual hosts, no `file://`

The page is served from `https://assets.mdpreview.invalid/` via
`SetVirtualHostNameToFolderMapping`, established before the first navigation.

The previewed document's own folder is `https://doc.mdpreview.invalid/`, and it
is **not** a folder mapping: a `SetVirtualHostNameToFolderMapping` issued after
`index.html` has committed never applies to the already-loaded document
(verified empirically), and this pipeline keeps one page alive across
selections precisely so Mermaid/MathJax stay warm. Document images are instead
answered by a `WebResourceRequested` handler that reads the current document
directory per request — image context only, GET only, canonicalised and
confined to that directory — so a selection change takes effect instantly with
no re-navigation.

Serving the page from `file://` would either grant it read access across the disk
or, with local-file restrictions on, break relative images. The virtual host gives
exactly the scope we want: the document can reference its own siblings and nothing
above them.

`.invalid` is reserved by RFC 2606 and can never resolve in DNS. That matters for
the failure case: if a mapping is missing — a stream-initialised document with no
folder — the request fails locally and instantly instead of leaking a lookup onto
the network.

### `IInitializeWithFile` as the primary path

Microsoft's guidance prefers `IInitializeWithStream` for isolation. We implement
both but register file initialisation first, because the stream has no path and
therefore no folder, and a Markdown previewer that cannot show the screenshots
sitting next to the document is not much of a previewer.

Stream initialisation is the fallback for documents with no path at all — inside a
compressed folder, a Search result, an Outlook attachment. Those degrade to broken-
image markers, which `DocumentLocation.Unknown` makes an explicit case rather than
an empty string someone forgets to check.

### `DoPreview` returns immediately

`DoPreview` queues the render onto the preview UI thread and returns `S_OK`
without waiting. It has to: WebView2 initialisation completes on a posted
callback, so waiting for the render would stall the shell's call for seconds.
Preview handlers are documented to render asynchronously and keep the host
responsive.

The visible consequence is that the pane is briefly empty on the first selection
after Explorer starts, while the browser process spins up. Subsequent selections
reuse it.

### The browser environment is cached, but never trusted to stay alive

`CoreWebView2Environment` is created once per process because `prevhost.exe` is
reused across selections and hosts several handler instances at once. The trap
is that a cached environment is only as alive as the browser process behind it:
when that process goes away — a WebView2 runtime update swapping the
installation, a crash, or the shell reaping it — every subsequent
`CreateCoreWebView2Controller` fails with `ERROR_INVALID_STATE` (`0x8007139F`),
and keeps failing for the life of the host because nothing invalidates the
cache. In the field this looked like "previews die until I close every Explorer
window", because closing them all is what finally killed the host.

Three things make the cache self-healing, and all three matter:

1. The provider subscribes to `BrowserProcessExited` and discards the cached
   environment. This event fires even with **no controller open**, which is
   exactly the window a `ProcessFailed` handler on a live surface cannot see.
2. Initialisation catches `0x8007139F`, discards, and retries **once** with a
   fresh environment. One retry, not a loop: if a freshly created environment
   also fails, something bigger is wrong and the error should surface.
3. A browser-process failure no longer latches the surface permanently
   unusable. The dead control is dropped so the next selection rebuilds it.

### One toolbar, tokens all the way down

Search, its options row, the theme selector and the contents toggle live in a
single top toolbar; the body is padded by `--mdp-bar-offset` (measured from the
real toolbar height, with a nominal fallback for the pre-layout frame) so the
document is never covered. The contents rail docks below the same offset and is
allowed to hang over the document — that one is chrome the reader summoned.

Every colour in the chrome comes from a seventeen-token theme contract
(`--ground`, `--surface`, `--ink`, `--accent`, …) defined per palette in
`themes.css` and selected by a `data-mdp-theme` attribute. System mode maps the
host's light/dark signal onto the stock GitHub palettes; the six named palettes
carry a fixed lightness and ignore the host signal.

The wrinkle worth remembering: the vendored github-markdown stylesheets are
FLATTENED builds — their colours are hard-coded, not `var()`-driven — so a named
palette cannot re-tint the document through variables alone. `themes.css`
therefore also overrides the flattened rules directly (links, borders, tables,
code backgrounds) under a `data-mdp-named` marker. Syntax highlighting keeps the
GitHub light or dark colour set matching the palette's lightness; re-deriving a
syntax palette per theme is not worth the drift.

### Find in page is ours, not the browser's

`AreBrowserAcceleratorKeysEnabled` is off, which disables the browser's own
find UI along with printing, DevTools and the rest. The DOM still receives the
keystroke, so `Ctrl+F` is handled in `preview.js`, which also buys a match
counter and highlight styling that match the document's theme.

The tree walker's filter is the fiddly part, and it has been wrong in both
directions. A rendered mermaid diagram injects a `<style>` block full of
`#mermaid-…` selectors and MathJax emits similar machinery; counting those
made a search for "mermaid" report 146 matches on a document that visibly
contains two. Excluding all of `<svg>` fixed that and created the opposite
bug — a diagram's *visible* labels are `<text>` inside that `<svg>`, so
searching for a term plainly on screen in a chart found nothing. The filter now
skips only the wrappers that hold no visible text (`style`, `script`, `title`,
`desc`, `defs`, `metadata`, and MathJax containers).

A match inside a diagram cannot be highlighted the way prose is: an HTML
`<mark>` spliced into an `<svg>` does not render, and would make the label
vanish. Those matches are measured with a `Range` and boxed by an absolutely
positioned overlay in document coordinates, which scrolls with the diagram. The
overlay is what goes into the match list, so counting, cycling and
scroll-into-view need no special case. Two consequences worth knowing: the
overlays live on `<body>` rather than under `#content`, so a re-render has to
remove them by hand or they leak one set per selection; and diagrams scale with
the pane, so a resize re-runs the search rather than leaving the boxes behind.

### The chrome hides with `hidden`, and the CSS has to allow it

The toolbar, its options row, and the contents rail are shown and hidden by
setting the `hidden` attribute. That works only because each panel is followed
by a `[hidden] { display: none }` rule of its own.

The HTML `hidden` attribute takes effect through the user-agent stylesheet rule
`[hidden] { display: none }`, and **author declarations outrank user-agent ones
however weak their specificity**. So `.mdp-find-bar { display: flex }` silently
defeated `hidden`: the attribute was set, `element.hidden` read `true`, and the
panel rendered anyway. That shipped as issue #1 — the find bar sat over every
document and its close button appeared dead.

Two rules follow from it:

1. Never give either panel a `display` without a matching `[hidden]` override
   directly below it. Below, not above: a real browser honours the override
   anywhere thanks to specificity, but jsdom — which the render tests run on —
   resolves ties by document order, so only the later rule works in both.
2. Test **computed display**, never `element.hidden`. The property was correct
   the entire time the bug was on screen, which is why 125 passing assertions
   never noticed.

### Search reads pictures with the host's help

Text drawn inside an inline diagram is walkable DOM. Text inside an `<img>` is
not: an SVG loaded that way is a separate, non-scriptable document, and a
raster image has no DOM at all. The host, however, is already the thing serving
every document-relative image, so for SVGs it also extracts their `<text>`
content (a locked-down `XmlReader` — DTDs prohibited, invisible containers
skipped, size-capped, cached by write-time) and posts it to the page after each
render (`imageTextRequest` → `imageText`). The search treats that text — plus
every image's alt and title — as the image's haystack, and a match boxes the
whole image: the exact word position inside a replaced element is not knowable
from outside it. OCR for raster images was considered and rejected: the
tesseract-class engines cost more megabytes than every other asset combined,
seconds per image, and a CSP hole, for mediocre accuracy on UI screenshots.

### The source view is a second renderer, and the failure path for the first

Showing the raw file is not "skip the parser": it is a separate paint of the
same message. `paintSource` puts the text in through `textContent` — never
`innerHTML` — so a file's own angle brackets can never become markup on the way
in, and highlighting is layered on afterwards as spans. That ordering makes the
colouring a nicety rather than a trust boundary: a tokeniser bug falls back to
`pre.textContent = source` and shows a correct, plain document.

That property is what lets the source view double as the parser's failure path.
If `markdown-it` itself throws — a damaged install, a corrupt asset — the render
paints the source, says why in a notice above it, and reports `failed` to the
host **directly** rather than through `fail()`, because `fail()` blanks the pane
and a blank pane is the one outcome this path exists to avoid.

The highlighter is the previewer's own rather than highlight.js's Markdown
grammar, which answers none of the questions a reader of raw Markdown has: it
paints front matter as several unrelated things, cannot bold a key or a table
heading, separates a fence from the language it names, and shows the inside of a
` ```csharp ` block as Markdown rather than as C#. Ours tokenises line by line
and hands each fence body to highlight.js under the language the fence declares.
Highlighting stops above 300,000 characters, where tokenising costs more than
the colour is worth.

Two placement decisions look cosmetic and are not:

- **The copy button lives outside `#content`.** Inside it, the in-page search
  would match its caption in the reader's own file. For the same reason its
  "Copied to clipboard" confirmation is a child of the button, not the document.
- **Chrome is `user-select: none`.** Ctrl+A and the context menu's Select All
  must yield the Markdown and not the theme name, the button captions or the
  tooltip text. The search field opts back in, since text typed there has to
  stay selectable.

Source view is remembered for every document rather than per file — someone
checking raw Markdown is usually checking several — while the controls that mean
nothing against plain text (contents, checkbox editing, trust) disable
themselves and name source view as the reason, rather than blaming the document.

### The one write path: a single character, or nothing

Checkbox editing is the only thing in this program that writes to a previewed
file, and `MarkdownTaskListEditor` is built so that the narrowness is structural
rather than a promise. Every click starts from `File.ReadAllBytes` — **the
document is never reconstructed from the rendered page**, because the page holds
sanitised HTML derived from the file, not the file.

The step that makes the rest safe is the round trip. `MarkdownTextDecoder` has
two decoders on purpose: `Decode` normalises CRLF and CR to LF for rendering and
is deliberately *not* reversible, while `DecodeForEditing` preserves everything
so that it is. The editor then asserts
`encoding.GetBytes(text) == original[preambleLength..]` before touching
anything. Without it, a file containing an invalid byte sequence would decode to
U+FFFD and re-encode as replacement bytes — corrupting content nowhere near the
checkbox the reader clicked. The BOM is held out of that comparison and
re-attached verbatim.

Four conditions refuse the write outright, and the third is the one that reads
backwards until you see it:

1. The round trip does not reproduce the original bytes.
2. The line index does not exist, or the line is not a GFM task marker.
3. **The marker is already in the state being asked for.** The page sends the
   state it wants, so agreement means the file changed under the preview.
4. The file is locked, read-only or gone (the catch-all).

The edit is one character — `x` or a space, never an empty box — and lands via a
temp file in the *same directory* (so `File.Replace` stays on one volume) with no
backup file left behind.

The refusal path has a wrinkle worth knowing before trusting it too far: it
re-renders `_lastRequest`, the text the preview last read, **not** a fresh read
from disk. For a wrong line or a failed round trip that text is still accurate
and the checkbox snaps back correctly. In the file-changed-underneath case it
snaps back to the stale text, so the pane stays behind until the next selection.
The write is still correctly refused; only the redraw is.

Positions are resolved by source line rather than by counting checkboxes, which
is what keeps a `- [ ] like this` inside a fenced block from both becoming a
checkbox and shifting the real ones below it. That makes line numbering a
contract spanning both languages — the page adds back the front-matter lines the
parser never saw, and the host scans un-normalised text while counting CRLF as
one terminator so the indices agree. Both halves are pinned by tests, one in
`MarkdownTaskListEditorTests` and one in `tests/web/render.test.mjs`, so drift on
either side fails a suite rather than corrupting a file.

### Trust is per document, and the host is the gate

Two registry lists under `HKCU\Software\MarkdownPreviewer` — `TrustedDocuments`
and `TaskEditDocuments` — record per-document choices, one value per full path.
Both are **HKCU-only with no machine-wide counterpart**, because each is a
personal judgement about a specific file rather than something an administrator
grants on a user's behalf. Paths are stored readable rather than hashed so the
lists can be inspected and revoked with regedit alone, and withdrawal *deletes*
the value rather than writing `0`, keeping each key a list of documents that
opted in.

`DocumentPathKey` guards both. It canonicalises, so one file cannot own two
settings under two spellings, and it **rejects an unrooted path before
canonicalising** — otherwise `Path.GetFullPath` would cheerfully root the bare
display name a stream-fed item carries (`notes.md`) against the current
directory, and one grant would stand for every file with that name.

The enforcement point for trust is `OnWebResourceRequested`, and its shape is
the inverse of what the CSP suggests:

```csharp
if (!_documentTrusted && _lastRequest?.Settings.AllowRemoteImages != true)
    e.Response = environment.CreateWebResourceResponse(
        null, 403, "External resources are blocked for this document", string.Empty);
```

- The page's CSP `img-src` is **deliberately broad** so that this handler, not
  the CSP, is the privacy gate. The filter is registered for the Image context
  only; script, style, font and XHR contexts never arrive here because CSP
  refuses them outright and independently.
- The refusal is a synthesised 403 with no body rather than a cancellation, so
  the page draws a placeholder instead of hanging.
- It **fails closed on a null `_lastRequest`**: `?.` makes the comparison `true`
  when there is no request, so a cleared surface refuses everything.

Trust is re-read per render rather than cached across selections, since Explorer
reuses one surface for every file and the previous document's grant must not
carry over. The page's own confirmation dialog runs *before* the host hears
anything: `trustDocument` is the recorded result, not a request for permission.

### Following a link means steering Explorer, not launching a program

Both link modes share one validation — absolute URL, `doc.mdpreview.invalid`
host, and a path that `TryMapDocumentUrl` confines under the current document's
folder — and then diverge deliberately:

| | Reveal in Explorer (default) | Open in default app |
| --- | --- | --- |
| File types | Any file that exists | An allowlist of inert formats |
| Why | Selecting a file executes nothing | A document must not be one click from running a `.bat` it shipped beside itself |

Finding the tab to steer is the interesting part. `ShellWindowsDocumentRevealer`
enumerates `Shell.Application`'s `Windows()` collection — late-bound through
reflection, the same calls a PowerShell one-liner would make, so there is no
interop assembly to ship — and matches on **two facts together**: the entry's
root HWND must equal our own (via `GetAncestor(…, GA_ROOT)`), and its
`LocationURL` must resolve to the folder being previewed. Neither alone is
enough, and the reason is Windows 11: **every tab of a window shares the same
root HWND**, so the HWND ties the entry to the window and only the folder ties
it to the specific tab.

Three behaviours follow from the same insight that Explorer is being *steered*
rather than queried:

- **`Reveal` runs on a background STA thread and returns immediately.** Changing
  Explorer's selection is the whole point of the call, and the instant it lands
  Explorer tears down the preview handler whose click started it. The thread is
  a background one so it cannot keep `prevhost.exe` alive by itself.
- **A target already in the shown folder skips navigation entirely** and only
  moves the selection — the README-links-to-a-sibling case, which is most of
  them.
- **Selection is retried and read back.** The view keeps initialising for a
  moment after `LocationURL` already reports the new folder, and a `SelectItem`
  in that window is silently ignored.

When no tab hosts the preview — the dev harness, Outlook's reading pane, a
document inside a `.zip`, or any virtual view whose `LocationURL` is not a file
path — it falls back to `SHOpenFolderAndSelectItems`, which opens a folder
window with the item selected. That fallback is *not* used when navigation
succeeded but selection did not: opening a second window at that point would be
worse than the miss.

### The host ↔ page protocol

One `postMessage` channel, JSON, camelCase, serialised through a
source-generated context (reflection-based serialisation would cost startup time
on a path that runs on every arrow-key press, and would rule out a NativeAOT
build later).

| Host → page | Page → host |
| --- | --- |
| `render`, `theme`, `imageText`, `find`, `toc` | `ready`, `rendered`, `failed`, `openExternal`, `openDocument`, `imageTextRequest`, `setTaskEdit`, `toggleTask`, `trustDocument`, `rerenderRequested`, `scriptError` |

Unknown kinds are logged and dropped rather than throwing. Two fields on the
`render` message carry more weight than their size suggests:

- **`token`** is the render ordering described below.
- **`documentGeneration`** increments only when the document changes *identity*,
  and repeats its previous value on a redraw (a theme flip, a trust change, a
  post-toggle refresh). Since both arrive as a `render` message, this is the
  page's only way to tell "the reader moved on" from "we drew this again" — and
  it is what lets a search query survive a theme change but not survive moving
  to the next file.

Capability and choice are also sent as separate fields — `taskEditable` beside
`taskEditOn`, `trustable` beside `trusted` — so the page can disable a control
and say *why* rather than showing a switch that silently forgets.

**Every inbound message is checked against the page's own URL, comparing
everything up to the query and forgiving only the fragment.** That precision is
scar tissue: WebView2 reports the fragment in `Source`, a reader clicking any
heading anchor or contents-rail entry adds one, and an exact-match comparison
then rejected everything the page said for the rest of its life. The trust
toggle did nothing and every later render died on its 30-second timeout. It
presented as "trust works in some folders and not others", because what actually
predicted it was whether the reader had used the table of contents yet.

### Render ordering by host token

Explorer changes selection faster than a document with a diagram can render. Every
render carries a monotonic token minted by the host; the page echoes it back, and
a reply whose token is stale is discarded.

**The page must echo the host's token, not a counter of its own.** An earlier
version kept a page-local sequence. The two diverged immediately, the host
discarded every reply as stale, and every preview died on the 30-second timeout.
The `tests/web` suite asserts this explicitly, because the symptom (everything
times out) points nowhere near the cause.

### Hand-wired composition, no container

`PreviewComposition` builds the graph by hand. A DI container would add an assembly
load and reflection to a path that runs on every arrow-key press, for convenience
we do not need at seven types. Worth revisiting past a dozen.

## Failure modes and where they surface

A preview handler's native failure mode is total silence — no error, no event log
entry, an empty rectangle. Each of these is turned into something readable:

| Failure | What the user sees |
| --- | --- |
| WebView2 runtime missing | A sentence in the pane naming the runtime and what to install |
| Render assets missing | The expected path and the list of missing files |
| Document too large | The document, truncated at a line boundary, with a notice at the top |
| Broken relative image | A dashed placeholder showing the path, not a 0×0 box |
| Mermaid or MathJax fails | The diagram source as a code block, plus a warning bar |
| Bad diagram syntax | That diagram falls back to source; the others still render |
| The Markdown parser cannot run | The raw file, in the source view, with the parse error above it — never a blank pane |
| A checkbox write is refused | The document is drawn again from the last-read text, so the box snaps back rather than showing a change that never landed |
| Browser process crash | "Markdown preview stopped unexpectedly. Select the file again." — and the next selection genuinely rebuilds it |
| Stale browser environment | Retried once with a fresh environment before any message is shown |
| Registry misconfiguration | `Test-MarkdownPreviewHandler.ps1` names the specific key |

`InstallDirectoryAssetCatalog` exists mainly to make one of these loud: inside
`prevhost.exe` the working directory is arbitrary (often `System32`), so any
relative asset probing silently resolves somewhere wrong. It resolves from
`Assembly.Location` and reports exactly what is missing.

## Threading rules

1. `IPreviewHandler` methods arrive on arbitrary RPC worker threads (managed CCWs
   are apartment-agile — see above). Nothing UI-bound may run there.
2. The window, the session, and WebView2 live on `PreviewUiThread` — a dedicated
   STA thread with a WinForms message loop. COM entry points marshal onto it:
   synchronously (`Invoke`) when the caller needs the result or the ordering
   guarantee, fire-and-forget (`Post`) for renders and other work the shell must
   not wait on.
3. That thread is **one per process, not one per handler**. The cached
   `CoreWebView2Environment` is affine to the thread that created it, and
   `prevhost.exe` hosts several handler instances over its life; giving each its
   own thread meant the second handler failed with "CoreWebView2Environment
   members can only be accessed from the UI thread".
4. Every `await` in the render path uses `ConfigureAwait(true)` so continuations
   return to that thread's WinForms synchronisation context.
   `ConfigureAwait(false)` anywhere in that chain produces a cross-thread WebView2
   exception.
5. `PreviewHostWindow` is a `Control`, not a `Form`, and injects `WS_CHILD` and
   the parent HWND through `CreateParams` *before* the handle exists. A `Form`
   with `WS_CHILD` grafted on looks equivalent and is not: its top-level
   visibility machinery never applies `WS_VISIBLE` and it offsets bounds by
   non-client margins it does not have, which renders perfectly into a window
   nobody ever sees.

## Things deliberately not done

- **No Markdig fallback.** Two rendering pipelines to keep consistent, for a case
  the WebView2 path already handles.
- **No thumbnail provider.** `IThumbnailProvider` is a separate interface with a
  separate registration and a much tighter time budget. Reasonable next step, out
  of scope here.
- **No scroll-position memory across selections.** Cheap to add, unclear whether it
  helps or annoys; wants real use before deciding.
- **No settings UI.** Registry values with documented defaults, so Group Policy
  works without us writing policy plumbing.
- **No finalizer on the COM class.** See the comment in `MarkdownPreviewHandler`:
  `Dispose` touches WinForms controls and waits on `DisposeAsync`, neither of which
  is legal on the finalizer thread. `prevhost.exe` exiting is what reclaims the
  browser process.
