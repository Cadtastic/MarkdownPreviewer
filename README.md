# Markdown Preview Handler

**Rich Markdown previews inside the Windows File Explorer preview pane** — syntax
highlighting, Mermaid diagrams, LaTeX math, and automatic light/dark theming.

[![License: MIT](https://img.shields.io/badge/License-MIT-green.svg)](LICENSE)
[![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4.svg)](https://dotnet.microsoft.com/download/dotnet/8.0)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011%20x64-0078D4.svg)](#requirements)
[![Renderer](https://img.shields.io/badge/renderer-WebView2-blue.svg)](https://developer.microsoft.com/microsoft-edge/webview2/)

Select a `.md` file, press `Alt+P`, and read the rendered document without opening
an editor. This is a real shell `IPreviewHandler`, so the output appears in
Explorer's own preview pane (and runs out-of-process, so a fault can never take
Explorer down with it). It uses the same client-side rendering stack QuickLook
does, but integrated where you already are.

![Preview pane rendering kitchen-sink.md](samples/images/sample.svg)

## Features

- **GitHub-flavoured rendering** — markdown-it with tables, task lists,
  autolinks, footnote-safe typography, and `github-markdown-css` styling
- **Raw HTML, sanitised** — GitHub-style `<p align="center">`, badges,
  `<details>`, `<kbd>` render like they do on GitHub; scripts, frames, forms and
  event handlers are stripped, with CSP behind the sanitiser
- **Syntax highlighting** — highlight.js, 64 curated languages
- **Mermaid diagrams** — ` ```mermaid ` fences render as real diagrams
- **LaTeX math** — MathJax SVG output for `$$…$$` (and `$…$`, opt-in)
- **Seven themes** — System follows the Windows colour mode; Paper, Arctic and
  Ledger (light) and Harbor, Midnight and Carbon (dark) re-tint the document
  and the chrome together, picked from the toolbar and remembered
- **One toolbar, there when you arrive** — a top bar holding search, its
  options row (match case, whole word, regex, the trust toggle and the theme
  selector), and the contents toggle. The document always starts below it; the
  × at the far right (or `Esc`) dismisses it for the document on screen, and
  the next one brings it back
- **Search sees pictures** — text drawn inside Mermaid diagrams is highlighted
  in place; SVG images get their text extracted by the host and matched; every
  image's alt and title text counts too, with matches boxing the image
- **Docked table of contents** — a contents rail under the toolbar on the
  right, toggled from the toolbar, remembered across documents
- **Share the document, not a dead link** — right-click → "Share document…"
  opens the Windows share sheet with the actual file; "Copy document" puts the
  file itself on the clipboard for pasting into mail or chat
- **Relative images resolve** — served from the document's own folder over a
  virtual host, never `file://`
- **Local links turn the page** — click `docs/architecture.md` in a README and
  Explorer navigates to that file and selects it, so the preview follows the
  link; a toolbar switch opens links in their default app instead
- **Fast where it counts** — Mermaid (3.5 MB) and MathJax (2.1 MB) are injected
  only when a document actually uses them, so arrow-keying through a folder of
  plain READMEs stays instant
- **Private by default** — strict CSP, no network access; remote images
  (badges) are blocked because a passive previewer should not announce what
  you clicked
- **Trust a document you wrote** — a globe toggle in the options row lets a
  single file load remote images, after a dialog that names the file and says
  what the grant costs. Recorded per file and remembered

## Why not just use PowerToys?

PowerToys' File Explorer add-ons include a Markdown preview handler, and if all
you need is headings and code blocks it is the shorter path. This project exists
for the gap: **no Mermaid, no MathJax, and no per-document theming.** If your
Markdown is full of architecture diagrams and equations, PowerToys renders them
as literal text.

| | This project | PowerToys |
| --- | --- | --- |
| Renderer | markdown-it in WebView2 (client-side) | Markdig → static HTML |
| Mermaid diagrams | Yes, lazy-loaded | No |
| LaTeX math | Yes, MathJax SVG, lazy-loaded | No |
| Relative images | Yes, served from the document's folder | Limited |
| Raw HTML | On by default, always sanitised | Off |
| Install footprint | ~7 MB plus the WebView2 runtime | Part of PowerToys |

## Requirements

- Windows 10 1809 or later, **64-bit**
- **.NET 8 Desktop Runtime (x64)** — required, and not negotiable. .NET does not
  support COM hosting from a self-contained deployment (`regsvr32` fails with
  `0x80008093`), so the shared runtime must be present. See
  [docs/architecture.md](docs/architecture.md#why-the-net-runtime-is-a-hard-dependency).
- **Microsoft Edge WebView2 Evergreen Runtime** — preinstalled on current
  Windows 11 and most Windows 10 installs.

## Install

Download the setup executable from
[Releases](https://github.com/Cadtastic/MarkdownPreviewer/releases) (or build it
yourself — see below), run it, and restart Explorer when prompted. What changed
in each release is in [CHANGELOG.md](CHANGELOG.md).

Then in Explorer: **View → Show → Preview pane** (or `Alt+P`) and select a `.md`
file.

If the pane is blank, run the bundled diagnostic — it checks every prerequisite
and registry entry in the order the shell itself resolves them, and tells you
which one is wrong:

```powershell
& "$env:ProgramFiles\MarkdownPreviewer\scripts\Test-MarkdownPreviewHandler.ps1"
```

More failure modes are catalogued in
[docs/troubleshooting.md](docs/troubleshooting.md).

## Building from source

```powershell
git clone https://github.com/Cadtastic/MarkdownPreviewer.git
cd MarkdownPreviewer

# Build, test, publish, and package the installer (requires NSIS 3.11)
.\build.ps1 -Test -Package

# Or: build and register the output in place, for development
.\build.ps1 -InstallLocal      # requires an elevated session
```

The installer lands in `artifacts\`.

### The development loop

Do **not** iterate by registering the handler and clicking files in Explorer.
The cycle is minutes long, `prevhost.exe` caches the loaded DLL and outlives the
Explorer window that spawned it, and you cannot attach a debugger before the
interesting code has already run.

Use the harness instead. It drives the identical object graph — same
`WebView2PreviewSurface`, same `PreviewSession`, same asset tree — with only the
COM shim absent, and it re-renders on save:

```powershell
dotnet run --project src\MarkdownPreviewer.Harness -- samples\kitchen-sink.md
```

For the render page itself (`assets/web/js/preview.js`), which is where most of
the behaviour lives:

```powershell
cd tests\web
npm install
npm test          # 175 assertions, no browser required
```

When you do need to test in Explorer, `scripts\Restart-Explorer.ps1` clears the
cached preview hosts as well as Explorer — skipping that step is the most common
reason a developer concludes their fix did nothing.

## Project layout

```
src/
  MarkdownPreviewer.Domain/          net8.0        Entities and value objects. No Windows dependency.
  MarkdownPreviewer.Application/     net8.0        Use case + abstractions. No Windows dependency.
  MarkdownPreviewer.Infrastructure/  net8.0-win    File IO, registry, logging, ShellExecute.
  MarkdownPreviewer.Rendering/       net8.0-win    The WebView2 surface. WinForms confined here.
  MarkdownPreviewer.Shell/           net8.0-win    COM entry point + composition root.
  MarkdownPreviewer.Harness/         net8.0-win    Development host.
assets/web/                                        The render page and vendored JS/CSS.
installer/                                         NSIS script and branded graphics.
scripts/                                           Register, diagnose, restart Explorer.
tests/MarkdownPreviewer.Tests/                     Domain, Application, Infrastructure.
tests/web/                                         The render page, under jsdom.
samples/                                           kitchen-sink.md and hostile.md.
```

`Domain` and `Application` target plain `net8.0` on purpose: it makes the
Dependency Rule a compiler error rather than a code-review convention. The
design decisions — and the shell-integration traps they route around — are
written up in [docs/architecture.md](docs/architecture.md).

## Settings

`HKCU\Software\MarkdownPreviewer` (per user) overrides
`HKLM\SOFTWARE\MarkdownPreviewer` (machine defaults). All values are `DWORD`
unless noted. Bad values are clamped, never fatal.

| Value | Default | Effect |
| --- | --- | --- |
| `Highlight` | 1 | Syntax-highlight fenced code |
| `Mermaid` | 1 | Render ` ```mermaid ` fences as diagrams |
| `Math` | 1 | Typeset LaTeX |
| `SingleDollarMath` | **0** | Treat `$…$` as inline math — see below |
| `AllowRawHtml` | 1 | Render embedded HTML (always sanitised — see below) |
| `AllowRemoteImages` | **0** | Load images from http(s) hosts (badges) — see below |
| `TaskLists` | 1 | `- [ ]` / `- [x]` as checkboxes |
| `ShowFrontMatter` | 1 | Show YAML/TOML front matter in a collapsed block |
| `FollowSystemTheme` | 1 | Follow the Windows apps colour mode (System theme only) |
| `FixedTheme` | `Light` | `REG_SZ`. Used when `FollowSystemTheme` is 0 |
| `FontScalePercent` | 100 | Base font size, clamped to 50–300 |
| `MaximumBytes` | 4194304 | Read cap; larger files are truncated with a notice |
| `LogLevel` | *(absent)* | 0=Debug…3=Error. Absent disables logging entirely |

The toolbar's theme selector (System plus six named palettes) is a per-user
choice stored in the preview's own browser profile. `FollowSystemTheme` and
`FixedTheme` decide what **System** means; a named theme carries its own
lightness and ignores them.

### Defaults that are deliberate decisions

**`SingleDollarMath` = 0.** GitHub and QuickLook both enable `$…$`. The cost is
that ordinary prose about money — "costs $5, sometimes $10" — renders as
mathematics. In a previewer the user did not opt into, silently mangling prose
is worse than math not rendering. Turn it on if you write more LaTeX than
invoices.

**`AllowRemoteImages` = 0.** Remote images are how tracking pixels work: with
them on, previewing a file can tell a third-party server that you looked at it.
Off, badge images (shields.io and friends) show as labelled placeholders. Turn
it on if you preview a lot of badge-heavy READMEs and accept the network
traffic — or leave it off and trust individual documents instead (below).

**`AllowRawHtml` = 1 — but what renders is the sanitised form.** GitHub-style
READMEs lean heavily on raw HTML (`<p align="center">`, badge rows,
`<details>`), and showing that as escaped source reads as broken. Before
anything reaches the DOM, a sanitiser strips `<script>`, `<iframe>`, `<form>`,
every `on*` handler, and any `javascript:`/`file:` URL — and CSP forbids inline
script and network access independently. Set it to 0 for strictly-Markdown
rendering.

### Where local links open

A link to a file next to the document — `CHANGELOG.md`, `docs/architecture.md` —
can go one of two ways, chosen with the crosshair/arrow switch in the toolbar's
options row and remembered per user.

**Reveal in Explorer** (the default) navigates the Explorer tab hosting the
preview to the file's folder and selects the file, which makes it the new
preview. Reading a set of linked documents becomes page-turning rather than a
trip through another program, and the back button still works, because it is
ordinary Explorer navigation. Any file that exists qualifies, extension or not:
selecting a file runs nothing.

**Open in the default app** is the older behaviour, and keeps its allowlist of
inert document types. That asymmetry is deliberate — a previewed document you
did not write must not be one click away from launching a `.bat` it shipped
beside itself, whereas merely selecting that file in Explorer is harmless.

A link to a folder navigates into it. A link to a file that is not there does
nothing. When the preview is not hosted by Explorer at all — the dev harness,
Outlook's reading pane, a document inside a `.zip` — a folder window opens with
the file selected instead.

### Trusting a single document

`AllowRemoteImages` is all-or-nothing, and most people want the opposite: a
couple of their own documents allowed onto the network and everything else
still blocked. The globe toggle in the toolbar's options row does that. Turning
it on asks first — a dialog that names the file and says what the grant costs
— and turning it off asks nothing.

Grants live in `HKCU\Software\MarkdownPreviewer\TrustedDocuments`, one value per
document, named by full path and set to `1`. That is a readable list on
purpose: deleting a value revokes one document, deleting the key revokes
everything, and neither needs this program. There is no machine-wide
equivalent, because trust here is a judgement about a specific file that only
the person reading it can make.

An item with no file behind it — a stream pulled out of a zip, a search-index
hit — cannot be trusted, since there is no path to record the grant against.
The toggle disables itself and says so.

The grant is narrower than the name suggests. It lifts exactly one restriction:
the host's refusal to serve http(s) image requests. Scripts, frames, forms and
outbound connections stay blocked by CSP for trusted and untrusted documents
alike, so the most a trusted document can do is tell a remote server that it
was opened.

Until a document is allowed onto the network, its remote images are never given
a URL, so no request is made at all — they render as dashed amber placeholders
carrying their alt text. That also means turning trust off takes effect
immediately, rather than leaving already-loaded images on screen until the
browser's cached copies expire.

Logging is off unless `LogLevel` is set, because the handler runs on every file
selection and an always-on log would record the path of every Markdown file you
so much as clicked on.

## What it renders

`samples/kitchen-sink.md` exercises everything; open it in the harness. Bundled,
all local, nothing fetched at runtime:

| Component | Version | Licence |
| --- | --- | --- |
| markdown-it | 14.3.0 | MIT |
| markdown-it-anchor | 9.2.1 | Unlicense |
| highlight.js (curated 64-language build) | 11.11.1 | BSD-3-Clause |
| Mermaid | 11.16.0 | MIT |
| MathJax (`tex-mml-svg`) | 3.2.2 | Apache-2.0 |
| github-markdown-css | 5.9.0 | MIT |

## Security posture

A previewed file is untrusted input, and the handler treats it that way:

- The page runs under a strict CSP with `connect-src 'none'` — a Markdown file
  cannot phone home. Remote images are additionally blocked host-side unless
  the user opts in, either through `AllowRemoteImages` or by trusting the
  individual document (below).
- Raw HTML renders only after sanitisation: scripts, frames, forms, event
  handlers, and dangerous URL schemes never reach the DOM.
- The document's folder is exposed through a virtual host rather than `file://`,
  so it cannot read outside its own directory.
- Navigation is blocked at the WebView2 level as well as in the page; link
  clicks are routed to the host, which re-validates the scheme before handing
  anything to `ShellExecute`. Links to sibling files open only if they resolve
  inside the document's folder tree and match an allowlist of inert document
  types — a README cannot phrase "click here" as a process launch.
- The handler runs out-of-process in the shell's `prevhost.exe` surrogate, so a
  crash never takes Explorer down.

`samples/hostile.md` is the regression case — every item in it must render as
inert text or a dead link.

## License

[MIT](LICENSE). Bundled third-party components keep their own licences, listed
above and in `installer/resources/license.txt`.
