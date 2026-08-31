# Troubleshooting

Start here:

```powershell
& "$env:ProgramFiles\MarkdownPreviewer\scripts\Test-MarkdownPreviewHandler.ps1"
```

It walks the prerequisites and registry entries in the order the shell resolves
them and names the first thing that is wrong. The sections below explain what each
finding means.

## The preview pane is empty

**Restart Explorer properly.** `prevhost.exe` caches the loaded handler DLL and
outlives the Explorer window that spawned it, so closing a window is not enough:

```powershell
& "$env:ProgramFiles\MarkdownPreviewer\scripts\Restart-Explorer.ps1"
```

**Check the pane is actually on and preview handlers are enabled.** `Alt+P` toggles
the pane. Separately, Folder Options → View has *Show preview handlers in preview
pane*, which some optimisation guides turn off.

**Enable logging and look.** Off by default, because the handler runs on every file
selection and an always-on log would record the path of every Markdown file you
click.

```powershell
New-Item 'HKCU:\SOFTWARE\MarkdownPreviewer' -Force | Out-Null
Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name LogLevel -Value 0 -Type DWord
# restart Explorer, select a .md file, then:
Get-Content "$env:LOCALAPPDATA\MarkdownPreviewer\logs\preview.log" -Tail 40 -Wait
```

## "Markdown preview could not start" — with an HRESULT

The pane shows the message and a code. The two seen in the field:

**`0x8007139F` — "The group or resource is not in the correct state".** The
browser environment was cached for the life of the preview host, but a cached
environment is only as alive as the browser process behind it. When that process
went away — a WebView2 runtime update, a crash, or the shell reaping it while no
preview was open — every later preview in that host failed the same way, which
is why closing every Explorer window (killing the host) fixed it until the next
time. Fixed in v1.1.0: the handler watches for the browser exiting, discards the
cached environment, and retries once with a fresh one.

**`0x800401F0` — "CoInitialize has not been called".** The shell's calls arrive
on threads that are not an STA and pump no messages, which WebView2 cannot start
on. Fixed in v1.1.0: the handler owns a dedicated STA thread with a message loop
and marshals every browser call onto it.

A third cause of `0x8007139F` was found later: WebView2's shared-browser
compatibility check includes the HOST EXECUTABLE's identity, so two different
host programs (prevhost and Outlook's reading pane, say) could collide on one
browser profile. The profile is now partitioned per host executable, which
removes that collision outright.

All of these are fixed in the current release, so seeing any of them means an
older build is still loaded. Confirm which binary is answering — the log records its full path
at startup — and restart Explorer with the script above, since `prevhost.exe`
caches the DLL:

```powershell
Get-Content "$env:LOCALAPPDATA\MarkdownPreviewer\logs\preview.log" -Tail 40
```

## "Registering the preview handler failed (0x80008093)"

The .NET 8 Desktop Runtime (x64) is missing, or the build was published
self-contained.

.NET does not support COM hosting from a self-contained deployment — this is
documented behaviour, not a packaging mistake you can fix with a flag. The runtime
has to be installed:

```powershell
winget install Microsoft.DotNet.DesktopRuntime.8
```

If you are building from source, confirm `SelfContained` is `false` in
`MarkdownPreviewer.Shell.csproj`.

## The handler is registered but never loads

Almost always one of three things.

**1. Registry redirection.** If you registered with a 32-bit tool, the entries may
be under `WOW6432Node`, where 64-bit Explorer never looks. Check both:

```powershell
$clsid = '{5B54A6AB-8765-4A71-8732-EA187093A239}'
Get-Item "HKLM:\SOFTWARE\Classes\CLSID\$clsid" -ErrorAction SilentlyContinue
Get-Item "HKLM:\SOFTWARE\WOW6432Node\Classes\CLSID\$clsid" -ErrorAction SilentlyContinue
```

Only the first should exist. If the second does, unregister and re-run the
installer.

**2. A missing or wrong AppID.** Without
`AppID = {6d2b5079-2f0b-48dd-ab7f-97cec514d30b}` the shell tries to load the
handler in-process, which fails for a .NET COM server hosted this way:

```powershell
(Get-ItemProperty "HKLM:\SOFTWARE\Classes\CLSID\$clsid").AppID
```

**3. Another application owns the ProgID.** The shell resolves the file
association's ProgID before falling back to the bare extension key. An editor that
claimed `.md` *and* registers its own preview handler will win. Find out what owns
it and register under that ProgID too:

```powershell
$progId = (Get-ItemProperty 'HKCU:\SOFTWARE\Classes\.md' -ErrorAction SilentlyContinue).'(default)'
$progId
& '.\scripts\Register-MarkdownPreviewHandler.ps1' -InstallPath '<install dir>' -ProgId $progId
```

## "Markdown preview needs the Microsoft Edge WebView2 runtime"

```powershell
winget install Microsoft.EdgeWebView2Runtime
```

Then restart Explorer. WebView2 ships with Windows 11 and most current Windows 10
installs, so this usually means a stripped or heavily managed image.

## Diagrams show as plain code

Mermaid is lazy-loaded, so a missing bundle degrades to source rather than an
error. Check the asset is there, and that the feature is on:

```powershell
Test-Path "$env:ProgramFiles\MarkdownPreviewer\assets\web\js\mermaid.min.js"
(Get-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -ErrorAction SilentlyContinue).Mermaid
```

A single malformed diagram falls back to its source while the rest of the document
renders — that is intentional. With `LogLevel` at 0 the parse error appears in the
log, and a warning bar appears above the document.

## Math shows as literal `$$…$$`

Same lazy-loading logic; check `assets\web\js\tex-mml-svg.js` exists and `Math` is
`1`.

If it is **single-dollar** `$x$` that is not rendering, that is the default. `$…$`
is off because prose about money ("costs $5, sometimes $10") otherwise renders as
mathematics. Opt in:

```powershell
Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name SingleDollarMath -Value 1 -Type DWord
```

## Local images do not appear

Relative images are served by the handler from the document's own folder. They
will not resolve if:

- The path points **above** the document's folder. Out of scope by design.
- The document was opened without a path — inside a zip, from a Search result, as
  a mail attachment. There is no folder to serve from. The log records
  `Initialised from an item stream (…); relative images will not resolve.`
- The file is genuinely missing or misnamed.

A broken path renders as a dashed placeholder showing the path, so you can see
which one failed rather than hunting a 0×0 box.

## Badge images (shields.io and similar) do not appear

Remote images are off by default: they are how tracking pixels work, and a
previewer should not tell a third-party server which files you open. Turn them
on if you accept that:

```powershell
Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name AllowRemoteImages -Value 1 -Type DWord
```

## HTML in the document shows as escaped source

`AllowRawHtml` has been set to `0`. It defaults to `1`, and what renders is
always the sanitised form — `<script>`, `<iframe>`, `<form>`, event handlers and
`javascript:`/`file:` URLs are stripped before display:

```powershell
Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name AllowRawHtml -Value 1 -Type DWord
```

## The toolbar, search, or table of contents is missing

The toolbar is visible by default, so a missing one means it was closed for the
document on screen — `Esc` or the `×` does that. Selecting another file brings
it back; so do `Ctrl+F` and the right-click menu's *Find…*, which is the one to
try if a host application swallows the keystroke.

The contents rail is toggled from the toolbar's **Contents** button and is
unavailable — button disabled — for documents with fewer than two headings; it
is also dropped on panes narrower than 640 px, where it would cover more than it
navigates. Open/closed choices for the rail and the options row persist across
documents.

## A toolbar control is greyed out

Every disabled control says why in its tooltip, and the reason is usually the
document rather than a fault:

| Control | Disabled when |
| --- | --- |
| Contents | Fewer than two headings, or source view is showing |
| Expand | The pane is narrower than the reading measure, so there is nothing to expand into |
| Syntax highlighting | The rendered view is showing — it only colours the source |
| Edit task checkboxes | No task list in the document, no file behind the item, or the document was truncated; also while source view is showing |
| Trust | No external image links to grant, no file behind the item, or source view is showing |

"No file behind the item" means the document reached the preview as a stream
rather than a path — inside a `.zip`, from a Search result, as a mail
attachment. There is no path to record a setting against.

## Clicking a checkbox does nothing, or it snaps back

Checkbox editing is off until you turn it on, per document, from the toolbar's
options row. With it off the boxes render but do not accept clicks.

With it on, a box that flips and then snaps back means the host **refused** the
write rather than losing it. The refusal is deliberate and happens whenever the
file does not match what the page believes: the bytes would not survive a
decode/re-encode round trip, the named line is not a task marker, the marker is
already in the state you asked for (the file changed underneath), or the file is
read-only or gone. After a refusal the document is drawn again from the text the
preview last read, which is why the box returns to its previous state instead of
showing a change that never landed.

If the refusal reason was that the **file changed underneath**, that redraw uses
the older text, so the pane will still be behind — reselect the file to pick up
what is actually on disk now.

The list of documents with editing turned on is plain registry data:

```powershell
Get-Item 'HKCU:\SOFTWARE\MarkdownPreviewer\TaskEditDocuments'
```

## A local link opened the wrong way, or did nothing

The crosshair/arrow switch in the options row decides. **Reveal in Explorer**
(the default, crosshair) navigates the hosting Explorer tab to the file's folder
and selects it, making it the new preview. **Open in the default app** (arrow
leaving a box) is the older behaviour and keeps an allowlist of inert document
types — a `.bat` next to a README will not launch, by design, whereas revealing
it is harmless and works for any file.

A link to a file that is not there does nothing at all. When no Explorer tab is
hosting the preview — the dev harness, Outlook's reading pane, a document inside
a `.zip` — reveal opens a folder window with the file selected instead of
navigating in place.

## Remote images still do not load after trusting the document

Trust is recorded per file, by full path. Check the grant actually landed:

```powershell
Get-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer\TrustedDocuments'
```

A grant is keyed to the path, so moving or renaming the file drops it. Deleting
a value revokes one document; deleting the key revokes everything.

Trust lifts exactly one restriction — the host's refusal to serve http(s) image
requests. Scripts, frames, forms and outbound connections stay blocked by CSP
for trusted and untrusted documents alike, so a document that is still not
loading something other than an image is behaving correctly.

## Source view shows no colour

**Syntax highlighting** in the options row is off by default, and it only
applies to the source view — it reads as inert while the rendered document is
showing. Files over 300,000 characters are shown as plain text regardless,
because tokenising them would stall the pane.

If the preview drops into source view **by itself**, with an error above the
document, the Markdown parser could not run — a damaged install or a corrupt
asset. Confirm the core bundle is present and reinstall if it is not:

```powershell
Test-Path "$env:ProgramFiles\MarkdownPreviewer\assets\web\js\markdown-it.min.js"
```

## The theme selector did not change anything

The selector lives in the toolbar's options row (gear icon). **System** follows
the host's light/dark resolution — the Windows apps colour mode, or
`FollowSystemTheme`/`FixedTheme` from the registry. The six named palettes
carry their own lightness and ignore those settings entirely. The choice is
stored per user in the preview's browser profile, so it survives upgrades but
not a profile wipe.

## Large files show a truncation notice

Files over 4 MB are read up to the cap and truncated at a line boundary. Raise it
if you really want to:

```powershell
Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name MaximumBytes -Value 16777216 -Type DWord
```

Clamped to 64 KB–64 MB.

## The theme is wrong

`FollowSystemTheme` is on by default and reads the Windows apps colour mode.
Explorer does not always repaint the pane on a theme change — reselect the file.

To pin a theme:

```powershell
Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name FollowSystemTheme -Value 0 -Type DWord
Set-ItemProperty 'HKCU:\SOFTWARE\MarkdownPreviewer' -Name FixedTheme -Value 'Dark'
```

In a non-Explorer host such as Outlook's reading pane, the theme comes from the
background colour the host reports, whose luminance we threshold. It can disagree
with the host in unusual colour schemes; pin the theme if that bothers you.

## Explorer crashes or previews stop working entirely

The handler runs in `prevhost.exe`, out of process, so it should not be able to
take Explorer down. If Explorer is unstable, another shell extension is the more
likely cause — try [ShellExView] or [Autoruns] to bisect.

To remove ours from the picture:

```powershell
& '.\scripts\Register-MarkdownPreviewHandler.ps1' -Unregister -Scope Machine
& '.\scripts\Restart-Explorer.ps1'
```

[ShellExView]: https://www.nirsoft.net/utils/shexview.html
[Autoruns]: https://learn.microsoft.com/sysinternals/downloads/autoruns

## Reporting something else

Include:

1. `Test-MarkdownPreviewHandler.ps1` output
2. `%LOCALAPPDATA%\MarkdownPreviewer\logs\preview.log` with `LogLevel` at 0
3. `winver` output and whether the file previews correctly in the harness
   (`dotnet run --project src\MarkdownPreviewer.Harness -- <file>`)

Item 3 is the useful one: it separates a rendering bug from a shell-integration
bug, and they have almost nothing in common.
