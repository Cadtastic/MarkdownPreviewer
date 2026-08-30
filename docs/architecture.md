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

### Find in page is ours, not the browser's

`AreBrowserAcceleratorKeysEnabled` is off, which disables the browser's own
find UI along with printing, DevTools and the rest. The DOM still receives the
keystroke, so `Ctrl+F` is handled in `preview.js`, which also buys a match
counter and highlight styling that match the document's theme.

The one non-obvious part is the tree walker's filter. A rendered mermaid diagram
injects a `<style>` block full of `#mermaid-…` selectors and MathJax emits
similar machinery; without excluding `<style>`, `<script>`, `<svg>` and MathJax
containers, searching for "mermaid" reported 146 matches on a document that
visibly contains two.

### The floating panels hide with `hidden`, and the CSS has to allow it

Both overlays — the find bar and the table of contents — are shown and hidden by
setting the `hidden` attribute. That works only because each panel is followed by
a `[hidden] { display: none }` rule of its own.

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
