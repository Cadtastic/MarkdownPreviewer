# Hostile input

This file is the security regression test. Every item below must render as inert
text or a dead link — nothing here should execute, fetch, or navigate.

## Script-bearing raw HTML

With `AllowRawHtml` at its default of `0`, all of this must appear as literal text:

<script>window.chrome.webview.postMessage({kind:'openExternal',url:'https://example.com/pwned'})</script>

<img src="x" onerror="alert('xss')">

<iframe src="https://example.com"></iframe>

<svg onload="alert(1)"></svg>

## Dangerous URL schemes

These links must be neutralised to `#`, not launched:

[javascript scheme](javascript:alert('xss'))

[vbscript scheme](vbscript:msgbox("xss"))

[file scheme reading the disk](file:///C:/Windows/win.ini)

[data URI with script](data:text/html;base64,PHNjcmlwdD5hbGVydCgxKTwvc2NyaXB0Pg==)

## Path traversal out of the document folder

The virtual host mapping is rooted at the document's directory; anything that
climbs above it must fail to load:

![traversal](../../../../Windows/System32/drivers/etc/hosts)

## Remote resources

CSP sets `connect-src 'none'`, so this image must not be fetched:

![remote tracker](https://example.com/tracking-pixel.png)

## Oversized content

A pathological table and a very long line should degrade, not hang:

| a | b |
|---|---|
| `$(1..500 | ForEach-Object { 'x' })` | see above |
