/*
 * preview.js — render controller for the Markdown Explorer preview handler.
 *
 * Contract with the host (WebView2 <-> page, JSON over postMessage):
 *
 *   host -> page  { kind: "render", token, markdown, theme, docBase, settings,
 *                                     documentGeneration, documentName,
 *                                     trusted, trustable, taskEditable,
 *                                     taskEditOn }
 *   host -> page  { kind: "theme",  theme }
 *   host -> page  { kind: "settings", settings }
 *
 *   page -> host  { kind: "ready", version }
 *   page -> host  { kind: "rendered", token, elapsedMs, usedMermaid, usedMath, warnings[] }
 *   page -> host  { kind: "failed", token, message }
 *   page -> host  { kind: "openExternal", url }
 *   page -> host  { kind: "trustDocument", trusted }
 *   page -> host  { kind: "openDocument", url, mode }   mode: "navigate" | "app"
 *   page -> host  { kind: "toggleTask", line, checked }
 *   page -> host  { kind: "setTaskEdit", enabled }
 *
 * Design notes worth knowing before editing:
 *
 *  - The document being rendered is UNTRUSTED. The user only clicked a file in
 *    Explorer; they did not consent to running its content. Raw HTML is off by
 *    default, every URL passes through resolveDocumentUrl(), and CSP forbids
 *    outbound connections. Keep it that way.
 *
 *  - Renders are token-ordered. Explorer fires selection changes faster than a
 *    3.5 MB mermaid bundle can load, so every async continuation re-checks
 *    isCurrent(token) before touching the DOM.
 *
 *  - Mermaid and MathJax are injected on first need and then cached for the
 *    life of the process. prevhost.exe is reused across selections, so the
 *    second document with a diagram in it is fast.
 */
(function () {
  'use strict';

  // Reported to the host in the "ready" handshake and logged, so a mismatch
  // between the installed binaries and the render assets is visible.
  var VERSION = '1.4.0';

  /* Generous: mermaid is 3.5 MB and MathJax 2.1 MB, both parsed from disk. */
  var ASSET_LOAD_TIMEOUT_MS = 15000;
  var DOC_ORIGIN = 'https://doc.mdpreview.invalid';
  var ASSET_ORIGIN = 'https://assets.mdpreview.invalid';

  // ---------------------------------------------------------------- state ---

  var state = {
    token: 0,
    docBase: DOC_ORIGIN + '/',
    theme: 'light',
    settings: defaultSettings(),
    md: null,
    mdSignature: null,
    tocVisible: true,
    themeName: 'system',      // 'system' or a named palette from THEME_LIGHTNESS
    appearance: 'light',      // effective lightness after resolving themeName
    imageText: Object.create(null),  // img src -> text the host extracted from it
    documentName: '',         // shown in the trust dialog so it can name its subject
    documentGeneration: 0,    // changes only when the reader moves to another file
    linkMode: 'navigate',     // where local links go: 'navigate' Explorer, or 'app'ly default app
    editTasks: false,         // the reader turned checkbox editing on for THIS document
    taskEditable: false,      // ...and this document has a file that can take the edit
    hasTasks: false,          // ...and there is at least one checkbox to edit
    remoteImages: 0,          // http(s) images this document refers to, allowed or not
    expanded: false,          // document fills the pane instead of its reading measure
    viewSource: false,        // showing the file's own text instead of the rendered document
    highlightSource: false,   // ...and colouring it
    lastMessage: null,        // the render that produced what is on screen
    bodyLine: 0,              // source line the rendered body starts on (after front matter)
    trusted: false,           // this document may load resources from the internet
    trustable: false          // ...and it has a path to record that grant against
  };

  var assetLoads = Object.create(null);   // href -> Promise
  var content = document.getElementById('content');
  var notice = document.getElementById('notice');
  var toc = document.getElementById('toc');
  var tocList = document.getElementById('toc-list');

  function defaultSettings() {
    return {
      allowRawHtml: true,
      allowRemoteImages: false,
      linkify: true,
      typographer: false,
      highlight: true,
      mermaid: true,
      math: true,
      singleDollarMath: false,
      taskLists: true,
      showFrontMatter: true,
      fontScalePercent: 100,
      maxAutoDetectBytes: 10240
    };
  }

  // -------------------------------------------------------------- plumbing ---

  function host() {
    return (window.chrome && window.chrome.webview) || null;
  }

  function post(message) {
    var h = host();
    if (h) { try { h.postMessage(message); } catch (_) { /* host gone */ } }
  }

  /*
   * The host's token is the single source of truth for render identity.
   *
   * An earlier version kept a page-local counter and echoed that back, which
   * looked fine in isolation but meant the host was comparing its own sequence
   * against ours. They diverge immediately, the host discards every reply as
   * stale, and every preview dies on the 30-second timeout. Do not reintroduce a
   * separate counter here.
   */
  function isCurrent(token) {
    return token === state.token;
  }

  function showNotice(text, severity) {
    notice.textContent = text;
    notice.setAttribute('data-severity', severity || 'warning');
    notice.hidden = false;
  }

  function clearNotice() {
    notice.hidden = true;
    notice.textContent = '';
  }

  // ------------------------------------------------------------- utilities ---

  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, '&amp;')
      .replace(/</g, '&lt;')
      .replace(/>/g, '&gt;')
      .replace(/"/g, '&quot;')
      .replace(/'/g, '&#39;');
  }

  /*
   * Whether this document may fetch from the internet. Mirrors the host's own
   * gate exactly — the standing preference, or a trust grant for this one file
   * — so the page and the host never disagree about what should load.
   */
  function remoteImagesAllowed() {
    return state.settings.allowRemoteImages === true || state.trusted === true;
  }

  /*
   * Resolve a URL found in the Markdown source.
   *
   * Returns '' for anything we refuse to emit. Returning empty rather than
   * throwing keeps a single bad link from killing the whole render.
   */
  function resolveDocumentUrl(raw, kind) {
    if (!raw) { return ''; }
    var value = String(raw).trim();
    if (!value) { return ''; }

    if (value.charAt(0) === '#') { return value; }            // in-page anchor

    if (/^[a-z][a-z0-9+.\-]*:/i.test(value)) {                // has a scheme
      if (/^https?:/i.test(value)) {
        // Links stay clickable however the document is trusted: following one
        // is a deliberate act, and the host re-validates before handing it to
        // the shell. An image is different — it fetches itself — so the URL is
        // withheld unless this document is allowed to reach the internet.
        //
        // The host refuses these requests too, and that remains the security
        // boundary. Withholding the URL here is about behaviour, not safety:
        // an <img> that is never given a src issues no request, so it cannot
        // be answered from the browser's cache. That is what makes withdrawing
        // trust take effect on the very next render instead of leaving already
        // fetched images on screen until the cached copies are evicted.
        // Counted whether or not the URL is handed back, because the
        // question this answers is "does this document have anything to
        // trust?" — and a withheld image is exactly the case where it does.
        if (kind === 'image') { state.remoteImages++; }

        return (kind === 'image' && !remoteImagesAllowed()) ? '' : value;
      }
      if (kind === 'link' && /^mailto:/i.test(value)) { return value; }
      if (kind === 'image' && /^data:image\/(png|jpeg|gif|webp|svg\+xml|avif);/i.test(value)) {
        return value;
      }
      // Everything else — javascript:, vbscript:, file:, ms-appx:, about: — is
      // dropped. file: in particular would hand a previewed document read
      // access to the whole disk.
      return '';
    }

    // Relative: resolve against the document's own directory on the doc host.
    try {
      var url = new URL(value.replace(/\\/g, '/'), state.docBase);
      return url.origin === DOC_ORIGIN ? url.href : '';
    } catch (_) {
      return '';
    }
  }

  /* GitHub-compatible heading slugs, so a hand-written [](#some-heading) works. */
  function githubSlug(text) {
    return String(text)
      .trim()
      .toLowerCase()
      .replace(/[ -⁯⸀-⹿\\'!"#$%&()*+,.\/:;<=>?@\[\]^`{|}~]/g, '')
      .replace(/\s+/g, '-');
  }

  /* Inject a script from the asset origin exactly once; resolve when ready. */
  function loadAsset(relativeHref) {
    if (assetLoads[relativeHref]) { return assetLoads[relativeHref]; }

    assetLoads[relativeHref] = new Promise(function (resolve, reject) {
      var settled = false;

      function settle(fn, value) {
        if (settled) { return; }
        settled = true;
        window.clearTimeout(timer);
        fn(value);
      }

      // A script element that fires neither load nor error would hang the render
      // until the host's own timeout expires, with nothing in the log to explain
      // it. Fail fast and name the asset instead.
      var timer = window.setTimeout(function () {
        delete assetLoads[relativeHref];
        settle(reject, new Error('Timed out loading ' + relativeHref));
      }, ASSET_LOAD_TIMEOUT_MS);

      var el = document.createElement('script');
      el.src = ASSET_ORIGIN + '/' + relativeHref;
      el.async = false;                       // preserve injection order
      el.onload = function () { settle(resolve); };
      el.onerror = function () {
        delete assetLoads[relativeHref];      // allow a later retry
        settle(reject, new Error('Failed to load ' + relativeHref));
      };

      document.head.appendChild(el);
    });

    return assetLoads[relativeHref];
  }

  // ------------------------------------------------------ markdown-it setup ---

  /*
   * The parser is rebuilt only when a setting that affects parsing changes.
   * Reconstructing markdown-it per keystroke-fast selection change is wasteful.
   */
  function parserSignature(s) {
    return [s.allowRawHtml, s.linkify, s.typographer, s.highlight,
            s.mermaid, s.maxAutoDetectBytes].join('|');
  }

  function buildParser(settings) {
    var md = window.markdownit({
      html: settings.allowRawHtml === true,
      xhtmlOut: false,
      breaks: false,
      langPrefix: 'language-',
      linkify: settings.linkify !== false,
      typographer: settings.typographer === true,
      highlight: function (code, lang) {
        if (!settings.highlight || typeof window.hljs === 'undefined') { return ''; }
        try {
          var result;
          if (lang && window.hljs.getLanguage(lang)) {
            result = window.hljs.highlight(code, { language: lang, ignoreIllegals: true });
          } else if (code.length <= settings.maxAutoDetectBytes) {
            // Auto-detection is O(languages x length); only worth it on short
            // unlabelled fences.
            result = window.hljs.highlightAuto(code);
          } else {
            return '';
          }
          return '<pre class="hljs"><code class="hljs language-' +
                 escapeHtml(result.language || 'plaintext') + '">' +
                 result.value + '</code></pre>';
        } catch (_) {
          return '';   // '' => markdown-it falls back to plain escaped output
        }
      }
    });

    // --- heading anchors ---------------------------------------------------
    var anchorNs = window.markdownItAnchor;
    var anchorPlugin = (anchorNs && anchorNs.default) || anchorNs;
    if (typeof anchorPlugin === 'function') {
      md.use(anchorPlugin, { slugify: githubSlug, tabIndex: false, level: [1, 2, 3, 4, 5, 6] });
    }

    // --- mermaid fences ----------------------------------------------------
    var defaultFence = md.renderer.rules.fence;
    md.renderer.rules.fence = function (tokens, idx, options, env, self) {
      var token = tokens[idx];
      var lang = String(token.info || '').trim().split(/\s+/)[0].toLowerCase();
      if (settings.mermaid && lang === 'mermaid') {
        env.usedMermaid = true;
        return '<div class="mermaid-block"><pre class="mermaid">' +
               escapeHtml(token.content) + '</pre></div>\n';
      }
      return defaultFence(tokens, idx, options, env, self);
    };

    // --- list items ----------------------------------------------------------
    // Every list item carries the source line it starts on (relative to the
    // rendered body), which is how a task checkbox finds its way back to the
    // file when the reader edits it.
    var defaultListItem = md.renderer.rules.list_item_open ||
      function (tokens, idx, options, env, self) { return self.renderToken(tokens, idx, options); };
    md.renderer.rules.list_item_open = function (tokens, idx, options, env, self) {
      var token = tokens[idx];
      if (token.map) { token.attrSet('data-mdp-line', String(token.map[0])); }
      return defaultListItem(tokens, idx, options, env, self);
    };

    // --- images ------------------------------------------------------------
    var defaultImage = md.renderer.rules.image;
    md.renderer.rules.image = function (tokens, idx, options, env, self) {
      var token = tokens[idx];
      var i = token.attrIndex('src');
      if (i >= 0) {
        var raw = token.attrs[i][1];
        var resolved = resolveDocumentUrl(raw, 'image');
        if (!resolved) {
          // Keep the alt text visible instead of emitting a dead <img>, and
          // separate the two reasons a URL can vanish: an image held back for
          // want of trust is a decision the reader can reverse from the
          // toolbar, where a path that cannot be resolved is not.
          var blocked = /^\s*https?:/i.test(String(raw || ''));
          return '<span class="mdp-broken' + (blocked ? ' mdp-blocked' : '') + '"' +
                 (blocked ? ' title="Blocked. Trust this document from the toolbar to load images from the internet."' : '') +
                 '>' + escapeHtml(token.content || raw) + '</span>';
        }
        token.attrs[i][1] = resolved;
      }
      token.attrSet('loading', 'lazy');
      token.attrSet('decoding', 'async');
      return defaultImage(tokens, idx, options, env, self);
    };

    // --- links -------------------------------------------------------------
    var passthroughToken = function (tokens, idx, options, env, self) {
      return self.renderToken(tokens, idx, options);
    };
    var defaultLinkOpen = md.renderer.rules.link_open || passthroughToken;
    md.renderer.rules.link_open = function (tokens, idx, options, env, self) {
      var token = tokens[idx];
      var i = token.attrIndex('href');
      if (i >= 0) {
        var resolved = resolveDocumentUrl(token.attrs[i][1], 'link');
        token.attrs[i][1] = resolved || '#';
        if (resolved.indexOf(DOC_ORIGIN + '/') === 0) {
          // A sibling of the previewed document; the host opens the real file.
          token.attrSet('data-mdp-doclink', '1');
        } else if (/^https?:/i.test(resolved)) {
          token.attrSet('rel', 'noopener noreferrer nofollow');
          token.attrSet('data-mdp-external', '1');
        }
      }
      return defaultLinkOpen(tokens, idx, options, env, self);
    };

    return md;
  }

  function parserFor(settings) {
    var signature = parserSignature(settings);
    if (!state.md || state.mdSignature !== signature) {
      state.md = buildParser(settings);
      state.mdSignature = signature;
    }
    return state.md;
  }

  // ------------------------------------------------------------ front matter ---

  /*
   * Strip a leading YAML/TOML front-matter block. Rendering it as Markdown
   * turns `---` into an <hr> and the first key into an <h2>, which looks broken.
   */
  function splitFrontMatter(source) {
    var match = /^(﻿)?(---|\+\+\+)[ \t]*\r?\n([\s\S]*?)\r?\n\2[ \t]*(\r?\n|$)/.exec(source);
    if (!match) { return { frontMatter: null, body: source, bodyLine: 0 }; }

    // How many source lines the stripped block spans: everything the parser
    // never sees still counts when a task edit is addressed back to the file.
    var consumed = (match[0].match(/\n/g) || []).length;
    return { frontMatter: match[3], body: source.slice(match[0].length), bodyLine: consumed };
  }

  function frontMatterHtml(text) {
    return '<details class="mdp-frontmatter"><summary>Front matter</summary><pre>' +
           escapeHtml(text) + '</pre></details>';
  }

  // -------------------------------------------------------------- task lists ---

  /*
   * GFM task lists, applied post-render over the DOM.
   *
   * Doing this as a DOM pass rather than a markdown-it core rule is a conscious
   * trade: it is ~20 lines instead of ~80, it cannot corrupt the token stream,
   * and it degrades to "renders as literal [ ]" if it ever misses.
   */
  function applyTaskLists(root) {
    var items = root.querySelectorAll('li');
    for (var i = 0; i < items.length; i++) {
      var li = items[i];
      // The marker lives in the first text node, possibly nested in the
      // implicit <p> that markdown-it emits for loose lists.
      var container = li.firstElementChild &&
                      li.firstElementChild.tagName === 'P' ? li.firstElementChild : li;
      var node = container.firstChild;
      if (!node || node.nodeType !== Node.TEXT_NODE) { continue; }

      var m = /^\s*\[([ xX])\]\s+/.exec(node.nodeValue);
      if (!m) { continue; }

      node.nodeValue = node.nodeValue.slice(m[0].length);

      var box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = m[1] !== ' ';
      // Read-only unless the reader turned editing on for a document that can
      // take the edit; syncTaskBoxes() re-applies this when the toggle flips.
      box.disabled = !taskEditingActive();
      box.addEventListener('change', onTaskBoxChanged);
      container.insertBefore(box, node);
      li.classList.add('mdp-task');
    }
  }

  /*
   * A checked box is a WRITE: the host re-reads the file, verifies the marker
   * on the named line is a task in the state the page believes, flips that one
   * character and saves — silently, by design; the toggle's tooltip is where
   * the save behaviour is disclosed. If the host refuses (the file changed
   * underneath the preview, went read-only, disappeared), it re-renders from
   * disk, which snaps the box back to the truth.
   */
  function onTaskBoxChanged() {
    var li = this.closest && this.closest('li');
    var line = li ? parseInt(li.getAttribute('data-mdp-line'), 10) : NaN;

    if (!taskEditingActive() || isNaN(line)) {
      this.checked = !this.checked;   // not editable; undo the flip
      return;
    }

    post({ kind: 'toggleTask', line: line + state.bodyLine, checked: this.checked });
  }

  // -------------------------------------------------- raw HTML sanitisation ---

  var FORBIDDEN_ELEMENTS =
    'script,iframe,frame,frameset,object,embed,applet,base,meta,link,form,' +
    'input,button,textarea,select,noscript,portal';

  var URL_ATTRIBUTES = [
    ['href', 'link'], ['src', 'image'], ['xlink:href', 'link'],
    ['action', 'link'], ['formaction', 'link'], ['poster', 'image'],
    ['background', 'image'], ['data', 'link'], ['srcset', 'image'],
    ['ping', 'link'], ['longdesc', 'link'],
  ];

  /*
   * Scrub raw HTML that the user has explicitly opted into.
   *
   * markdown-it's validateLink already rejects dangerous schemes in Markdown
   * syntax, and our renderer rules re-check them. Neither touches raw HTML — it
   * is passed through verbatim by design. So when allowRawHtml is on, this is the
   * only thing standing between a previewed file and an onerror= handler.
   *
   * An allowlist would be stricter, but a denylist of elements and event
   * attributes is what keeps the legitimate use case (hand-written <details>,
   * <sub>, <kbd>, styled tables) working. Combined with CSP forbidding inline
   * script and connect-src, the residual risk is presentational.
   */
  function sanitiseRawHtml(root) {
    var forbidden = root.querySelectorAll(FORBIDDEN_ELEMENTS);
    for (var i = forbidden.length - 1; i >= 0; i--) {
      forbidden[i].parentNode.removeChild(forbidden[i]);
    }

    var all = root.querySelectorAll('*');
    for (var j = 0; j < all.length; j++) {
      var element = all[j];
      var attributes = element.attributes;

      // Backwards: removing an attribute mutates the live collection.
      for (var k = attributes.length - 1; k >= 0; k--) {
        var name = attributes[k].name.toLowerCase();
        if (name.indexOf('on') === 0 || name === 'srcdoc' || name === 'sandbox') {
          element.removeAttribute(attributes[k].name);
        }
      }

      for (var u = 0; u < URL_ATTRIBUTES.length; u++) {
        var attribute = URL_ATTRIBUTES[u][0];
        if (!element.hasAttribute(attribute)) { continue; }

        var resolved = resolveDocumentUrl(element.getAttribute(attribute), URL_ATTRIBUTES[u][1]);
        if (resolved) {
          element.setAttribute(attribute, resolved);
        } else {
          element.removeAttribute(attribute);
        }
      }
    }
  }

  // ----------------------------------------------------------- broken images ---

  function markBrokenImages(root) {
    var images = root.querySelectorAll('img');
    for (var i = 0; i < images.length; i++) {
      images[i].addEventListener('error', function () {
        this.classList.add('mdp-broken');
        if (!this.alt) {
          try {
            this.alt = decodeURIComponent(this.src.replace(DOC_ORIGIN + '/', ''));
          } catch (_) { this.alt = 'image not found'; }
        }
      }, { once: true });
    }
  }

  // --------------------------------------------------------- contents rail ---

  function readTocPreference() {
    try { return window.localStorage.getItem('mdp.toc') !== '0'; }
    catch (_) { return true; }
  }

  function storeTocPreference(visible) {
    try { window.localStorage.setItem('mdp.toc', visible ? '1' : '0'); }
    catch (_) { /* storage unavailable; the choice just will not persist */ }
  }

  /*
   * Rebuilt per render from the headings markdown-it-anchor gave ids to.
   * Documents with fewer than two headings get no contents rail regardless of
   * the visibility preference - a one-heading outline is noise - and the
   * toolbar's Contents button disables itself to say why.
   */
  function buildToc() {
    tocList.textContent = '';

    var headings = content.querySelectorAll('h1[id], h2[id], h3[id], h4[id]');
    for (var i = 0; i < headings.length; i++) {
      var heading = headings[i];
      var link = document.createElement('a');
      link.href = '#' + heading.id;
      link.textContent = heading.textContent;
      link.className = 'mdp-toc-' + heading.tagName.toLowerCase();
      tocList.appendChild(link);
    }

    syncRail();
  }

  function setTocVisible(visible) {
    state.tocVisible = visible === true;
    storeTocPreference(state.tocVisible);
    syncRail();
  }

  /*
   * The single place the rail's visibility is decided: the user preference,
   * whether this document has enough headings, and whether the toolbar - the
   * rail's only control surface - is on screen at all.
   */
  function syncRail() {
    var toggle = document.getElementById('toc-toggle');
    var eligible = tocList.childElementCount >= 2;
    var open = state.tocVisible && eligible && !find.bar.hidden;

    toc.hidden = !open;

    if (toggle) {
      toggle.disabled = !eligible;
      setTip(toggle, state.viewSource
        ? 'Contents\nNot available while viewing source.'
        : eligible
          ? 'Contents\nJump to a heading.'
          : 'Contents\nThis document has fewer than two headings.');
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      toggle.classList.toggle('mdp-active', open);
    }
  }

  // ------------------------------------------------------------------ trust ---

  /*
   * Trusting a document lifts exactly one restriction: the host's refusal to
   * serve http(s) requests, which is what blocks remote images. Scripts,
   * frames, forms and outbound connections stay blocked by CSP either way, so
   * the worst a trusted document can do is tell a remote server it was opened.
   * That is still worth a dialog — it is how tracking pixels work — but it is
   * not worth pretending the grant is broader than it is.
   *
   * The host owns the decision and the memory of it. This page never assumes
   * its own click succeeded: the button is repainted from the `trusted` flag on
   * the next render, which the host sends after recording the grant.
   */
  var trust = {
    toggle: document.getElementById('trust-toggle'),
    dialog: document.getElementById('trust-dialog'),
    name: document.getElementById('trust-dialog-name'),
    confirm: document.getElementById('trust-confirm'),
    cancel: document.getElementById('trust-cancel')
  };

  function syncTrustToggle() {
    if (!trust.toggle) { return; }

    trust.toggle.setAttribute('aria-pressed', state.trusted ? 'true' : 'false');

    // Nothing to grant is its own reason, and a different one to say: a
    // document with no remote images gains nothing from being trusted.
    var nothingToTrust = state.remoteImages === 0;
    trust.toggle.disabled = !state.trustable || nothingToTrust || state.viewSource;

    setTip(trust.toggle, state.viewSource
      ? 'Trust external image links\nNot available while viewing source.'
      : !state.trustable
      ? 'Trust external image links\nThis item has no file on disk to trust.'
      : nothingToTrust
        ? 'Trust external image links\nThis document has no external image links.'
        : state.trusted
          ? 'External image links are trusted\nImages from the internet load for this file.'
          : 'Trust external image links\nAllow this file to load images from the internet.');
  }

  function openTrustDialog() {
    if (!trust.dialog) { return; }

    if (trust.name) {
      trust.name.textContent = state.documentName || 'this document';
    }

    trust.dialog.hidden = false;

    // Cancel takes focus, so Enter and Space — the keys someone mashes through
    // a dialog they did not expect — decline rather than agree.
    if (trust.cancel) { trust.cancel.focus(); }
  }

  function closeTrustDialog() {
    if (!trust.dialog || trust.dialog.hidden) { return; }

    trust.dialog.hidden = true;
    if (trust.toggle && !trust.toggle.disabled) { trust.toggle.focus(); }
  }

  function setTrusted(trusted) {
    closeTrustDialog();

    // Optimistic only for the paint; the host's next render is the truth.
    state.trusted = trusted === true;
    syncTrustToggle();
    post({ kind: 'trustDocument', trusted: state.trusted });
  }

  // ---------------------------------------------------------------- tooltips ---

  /*
   * One tooltip element, filled and positioned from whichever control the
   * pointer or the keyboard is on. The native `title` was doing this job badly:
   * it renders in the OS's colours rather than the document's theme, it wraps
   * where it likes, and it waits on a delay we do not control.
   *
   * The text convention is a label on the first line and the detail on the
   * rest, split on the newline in data-mdp-tip. Keyboard focus shows it too, so
   * the explanation is not mouse-only.
   */
  var TIP_DELAY_MS = 350;

  var tip = {
    el: document.getElementById('mdp-tip'),
    timer: 0,
    target: null
  };

  /* Sets the tooltip text of a control, refreshing it if it is on screen. */
  function setTip(element, text) {
    if (!element) { return; }

    element.setAttribute('data-mdp-tip', text || '');
    element.setAttribute('aria-label', String(text || '').replace(/\n+/g, ' — '));

    if (tip.target === element) { fillTip(text); positionTip(element); }
  }

  function fillTip(text) {
    if (!tip.el) { return; }

    var lines = String(text || '').split('\n');
    tip.el.textContent = '';

    var label = document.createElement('b');
    label.textContent = lines[0];
    tip.el.appendChild(label);

    if (lines.length > 1) {
      var detail = document.createElement('span');
      detail.textContent = lines.slice(1).join('\n');
      tip.el.appendChild(detail);
    }
  }

  /*
   * Below the control, nudged left when it would overhang the pane. Fixed
   * positioning keeps it out of the document's scroll, so it never drags a
   * scrollbar into existence.
   */
  function positionTip(element) {
    if (!tip.el) { return; }

    var anchor = element.getBoundingClientRect();

    // Measure from a neutral origin. Left where the last anchor put it, a
    // tooltip pressed against an edge wraps differently, and positioning from
    // that distorted box throws it off screen entirely.
    tip.el.style.left = '0px';
    tip.el.style.top = '0px';

    var box = tip.el.getBoundingClientRect();
    var margin = 6;

    // Both fall back, and both are checked for zero: a viewport of no size is
    // not a viewport with no room in it. Clamping against zero would pin every
    // tooltip to the left margin and flip every one of them above its control.
    var viewWidth = window.innerWidth || document.documentElement.clientWidth || 0;
    var viewHeight = window.innerHeight || document.documentElement.clientHeight || 0;

    var left = anchor.left + (anchor.width / 2) - (box.width / 2);
    if (viewWidth > 0) {
      left = Math.min(left, viewWidth - box.width - margin);
    }
    left = Math.max(margin, left);

    var top = anchor.bottom + 6;
    if (viewHeight > 0 && top + box.height > viewHeight - margin) {
      top = anchor.top - box.height - 6;   // flip above rather than run off
    }

    tip.el.style.left = Math.round(left) + 'px';
    tip.el.style.top = Math.round(top) + 'px';
  }

  function showTip(element) {
    var text = element.getAttribute('data-mdp-tip');
    if (!tip.el || !text) { return; }

    tip.target = element;
    fillTip(text);
    tip.el.hidden = false;
    positionTip(element);           // measured only once it is laid out
    tip.el.classList.add('mdp-tip-shown');
  }

  function hideTip() {
    window.clearTimeout(tip.timer);
    tip.target = null;

    if (tip.el) {
      tip.el.classList.remove('mdp-tip-shown');
      tip.el.hidden = true;
    }
  }

  function scheduleTip(element) {
    window.clearTimeout(tip.timer);
    tip.timer = window.setTimeout(function () { showTip(element); }, TIP_DELAY_MS);
  }

  /* Delegated, so controls created after startup are covered too. */
  function tipTargetFor(node) {
    return node && node.closest ? node.closest('[data-mdp-tip]') : null;
  }

  document.addEventListener('mouseover', function (event) {
    var target = tipTargetFor(event.target);
    if (target === tip.target) { return; }

    hideTip();
    if (target) { scheduleTip(target); }
  });

  document.addEventListener('mouseout', function (event) {
    if (tipTargetFor(event.target)) { hideTip(); }
  });

  // A tooltip that survives its own control being clicked would sit over the
  // thing the reader just acted on.
  document.addEventListener('mousedown', hideTip, true);

  document.addEventListener('focusin', function (event) {
    var target = tipTargetFor(event.target);
    hideTip();
    if (target) { showTip(target); }
  });
  document.addEventListener('focusout', hideTip);

  // ----------------------------------------------------------- task editing ---

  /*
   * Checkbox editing is opt-in and per-user, not per-document: the reader who
   * wants clickable task lists wants them everywhere. Whether a particular
   * document can take the edit is the host's call (taskEditable — a file on
   * disk, not truncated), and the button disables itself when it cannot.
   */
  var taskEdit = { toggle: document.getElementById('task-edit') };

  function taskEditingActive() {
    return state.editTasks === true && state.taskEditable === true && state.hasTasks === true;
  }

  function syncTaskEditToggle() {
    if (taskEdit.toggle) {
      // Three different reasons the control can be unavailable, and the
      // tooltip says which one applies rather than leaving a dead button.
      var usable = state.taskEditable && state.hasTasks && !state.viewSource;

      taskEdit.toggle.setAttribute('aria-pressed', taskEditingActive() ? 'true' : 'false');
      taskEdit.toggle.disabled = !usable;
      setTip(taskEdit.toggle,
        state.viewSource ? 'Edit checkboxes\nNot available while viewing source.'
          : !state.hasTasks ? 'Edit checkboxes\nThis document has no task list.'
            : !state.taskEditable ? 'Edit checkboxes\nThis document has no file to save to.'
              : state.editTasks ? 'Checkboxes are editable\nChanges save to the file straight away.'
                : 'Edit checkboxes\nChanges save to the file straight away.');
    }

    syncTaskBoxes();
  }

  /* Re-applies editability to the boxes already on screen. */
  function syncTaskBoxes() {
    var active = taskEditingActive();
    var boxes = content.querySelectorAll('li.mdp-task input[type="checkbox"]');
    for (var i = 0; i < boxes.length; i++) {
      boxes[i].disabled = !active;
    }
  }

  // ------------------------------------------------------------- view source ---

  /*
   * Shows the file as it is on disk instead of the document it renders to.
   *
   * A view preference rather than a document one - someone checking raw
   * Markdown is usually checking several files - so it lives in local storage
   * beside the other view choices rather than in the host's per-document
   * stores.
   *
   * Redrawing is a re-render of the message already in hand: nothing about the
   * document has changed, only how it is being shown, so there is no reason to
   * ask the host for it again. Going back to the rendered view has to take the
   * same path anyway, because diagrams and mathematics need re-running.
   */
  var viewSource = {
    toggle: document.getElementById('view-source'),
    highlight: document.getElementById('source-highlight'),
    highlightLabel: document.getElementById('source-highlight-label')
  };

  function readViewSourcePreferences() {
    try {
      state.viewSource = window.localStorage.getItem('mdp.viewSource') === '1';
      state.highlightSource = window.localStorage.getItem('mdp.highlightSource') === '1';
    } catch (_) { /* defaults stand */ }
  }

  function storeViewSourcePreferences() {
    try {
      window.localStorage.setItem('mdp.viewSource', state.viewSource ? '1' : '0');
      window.localStorage.setItem('mdp.highlightSource', state.highlightSource ? '1' : '0');
    } catch (_) { /* storage unavailable; the choice just will not persist */ }
  }

  function syncViewSourceControls() {
    if (viewSource.toggle) {
      viewSource.toggle.setAttribute('aria-pressed', state.viewSource ? 'true' : 'false');
      setTip(viewSource.toggle, state.viewSource
        ? 'Viewing source\nShowing the file as it is on disk.'
        : 'View source\nShow the file as it is on disk.');
    }

    if (viewSource.highlight) {
      viewSource.highlight.checked = state.highlightSource;
    }

    // The option does nothing outside source view, and saying so beats leaving
    // a live-looking checkbox that changes nothing.
    if (viewSource.highlightLabel) {
      viewSource.highlightLabel.classList.toggle('mdp-option-idle', !state.viewSource);
      setTip(viewSource.highlightLabel, state.viewSource
        ? 'Syntax highlighting\nColours the raw Markdown.'
        : 'Syntax highlighting\nApplies while viewing source.');
    }
  }

  /*
   * Markdown source highlighting.
   *
   * highlight.js has a markdown grammar, but not one that answers the
   * questions a reader of raw Markdown actually has: it paints front matter as
   * several unrelated things, cannot bold a key or a table heading, separates
   * a fence from the language it names, leaves mathematics as prose, and shows
   * the inside of a ```csharp block as Markdown rather than as C#. So the
   * source view tokenises the file itself, line by line, and hands the body of
   * each fenced block to highlight.js under the language the fence declares.
   *
   * Every colour is one of the palette tokens the rest of the chrome already
   * uses. That is deliberate: those tokens are defined per theme against that
   * theme's own surfaces, so source highlighting is legible in all seven
   * without a single new colour, and a theme added later inherits it.
   */

  /* Beyond this, tokenising costs more than the colour is worth. */
  var MAX_HIGHLIGHTED_SOURCE = 300000;

  var SRC = {
    frontFence: /^(---|\+\+\+)[ \t]*$/,
    frontPair: /^([ \t]*)([A-Za-z0-9_.$-][A-Za-z0-9_.\[\]$ -]*?)([ \t]*:)(.*)$/,
    fence: /^([ \t]*)(`{3,}|~{3,})[ \t]*([A-Za-z0-9_+#.-]*)[ \t]*(.*)$/,
    mathFence: /^[ \t]*(\$\$|\\\[|\\\])[ \t]*$/,
    tableRow: /^[ \t]*\|/,
    tableRule: /^[ \t]*\|[\s:|-]*\|[ \t]*$/,
    heading: /^[ \t]{0,3}#{1,6}([ \t]|$)/,
    rule: /^[ \t]{0,3}((\*[ \t]*){3,}|(-[ \t]*){3,}|(_[ \t]*){3,})$/,
    quote: /^([ \t]*>+[ \t]?)(.*)$/,
    list: /^([ \t]*)([-*+]|\d{1,9}[.)])([ \t]+)/
  };

  /*
   * Inline spans, tried at every position with the earliest match winning.
   * Order settles ties: code first, because a backtick span may legitimately
   * contain asterisks and underscores that are not emphasis.
   */
  var SRC_INLINE = [
    ['mdp-s-code', /`[^`\n]+`/],
    ['mdp-s-math', /\$\$[^\n]+?\$\$|\\\([^\n]*?\\\)|\$[^\s$][^\n$]*?\$/],
    ['mdp-s-link', /!?\[[^\]\n]*\]\([^)\n]*\)/],
    ['mdp-s-autolink', /<https?:\/\/[^>\s]+>/],
    ['mdp-s-strong', /\*\*[^\n]+?\*\*|__[^\n]+?__/],
    ['mdp-s-em', /\*[^\s*][^\n*]*?\*|_[^\s_][^\n_]*?_/],
    ['mdp-s-html', /<\/?[A-Za-z][^>\n]*>/]
  ];

  function srcSpan(className, text) {
    var el = document.createElement('span');
    el.className = className;
    el.textContent = text;
    return el;
  }

  function appendSourceInline(parent, text) {
    var pos = 0;

    while (pos < text.length) {
      var rest = text.slice(pos);
      var bestIndex = -1;
      var bestText = '';
      var bestClass = '';

      for (var i = 0; i < SRC_INLINE.length; i++) {
        var found = rest.match(SRC_INLINE[i][1]);
        if (found && (bestIndex < 0 || found.index < bestIndex)) {
          bestIndex = found.index;
          bestText = found[0];
          bestClass = SRC_INLINE[i][0];
        }
      }

      if (bestIndex < 0) { break; }

      if (bestIndex > 0) {
        parent.appendChild(document.createTextNode(rest.slice(0, bestIndex)));
      }

      parent.appendChild(srcSpan(bestClass, bestText));
      pos += bestIndex + bestText.length;
    }

    if (pos < text.length) {
      parent.appendChild(document.createTextNode(text.slice(pos)));
    }
  }

  /*
   * The body of a fenced block, coloured as whatever the fence named. An
   * unknown or absent language leaves plain text rather than guessing: an
   * auto-detected wrong language is more confusing than none at all.
   */
  function appendFencedCode(parent, text, language) {
    var el = document.createElement('code');
    el.className = 'mdp-s-codeblock';
    el.textContent = text;

    if (language && typeof window.hljs !== 'undefined' &&
        window.hljs.getLanguage(language)) {
      el.className += ' language-' + language;
      try { window.hljs.highlightElement(el); }
      catch (_) { /* the plain text is already in place */ }
    }

    parent.appendChild(el);
  }

  /* One colour for the whole block; only the keys are picked out, in bold. */
  function appendFrontMatterLine(parent, raw) {
    var pair = raw.match(SRC.frontPair);
    if (!pair) {
      parent.appendChild(srcSpan('mdp-s-front', raw));
      return;
    }

    if (pair[1]) { parent.appendChild(srcSpan('mdp-s-front', pair[1])); }
    parent.appendChild(srcSpan('mdp-s-front mdp-s-key', pair[2] + pair[3]));
    if (pair[4]) { parent.appendChild(srcSpan('mdp-s-front', pair[4])); }
  }

  /* Pipes and the alignment row are one colour; the heading row is bold. */
  function appendTableRow(parent, raw, isHeading) {
    var pos = 0;

    for (;;) {
      var pipe = raw.indexOf('|', pos);
      if (pipe < 0) { break; }

      if (pipe > pos) { appendTableCell(parent, raw.slice(pos, pipe), isHeading); }
      parent.appendChild(srcSpan('mdp-s-table-rule', '|'));
      pos = pipe + 1;
    }

    if (pos < raw.length) { appendTableCell(parent, raw.slice(pos), isHeading); }
  }

  function appendTableCell(parent, text, isHeading) {
    if (isHeading) { parent.appendChild(srcSpan('mdp-s-table-head', text)); }
    else { appendSourceInline(parent, text); }
  }

  function highlightSourceInto(pre, text) {
    var lines = text.split('\n');
    var index = 0;
    var atStart = true;

    function newline() {
      if (!atStart) { pre.appendChild(document.createTextNode('\n')); }
      atStart = false;
    }

    // Front matter, if the file opens with it: delimiters and body alike.
    if (lines.length > 0 && SRC.frontFence.test(lines[0])) {
      var closer = lines[0].trim();
      var end = 1;
      while (end < lines.length && lines[end].trim() !== closer) { end++; }

      var last = Math.min(end, lines.length - 1);
      for (; index <= last; index++) {
        newline();
        appendFrontMatterLine(pre, lines[index]);
      }
    }

    while (index < lines.length) {
      var raw = lines[index];

      var fence = raw.match(SRC.fence);
      if (fence) {
        newline();
        pre.appendChild(srcSpan('mdp-s-fence', raw));   // marker and language as one
        index++;

        var body = [];
        var closing = fence[2].charAt(0) === '`'
          ? /^[ \t]*`{3,}[ \t]*$/
          : /^[ \t]*~{3,}[ \t]*$/;

        while (index < lines.length && !closing.test(lines[index])) {
          body.push(lines[index]);
          index++;
        }

        if (body.length > 0) {
          newline();
          appendFencedCode(pre, body.join('\n'), fence[3]);
        }

        if (index < lines.length) {
          newline();
          pre.appendChild(srcSpan('mdp-s-fence', lines[index]));
          index++;
        }

        continue;
      }

      // Display mathematics: the delimiters and everything between them.
      if (SRC.mathFence.test(raw)) {
        newline();
        pre.appendChild(srcSpan('mdp-s-math', raw));
        index++;

        while (index < lines.length && !SRC.mathFence.test(lines[index])) {
          newline();
          pre.appendChild(srcSpan('mdp-s-math', lines[index]));
          index++;
        }

        if (index < lines.length) {
          newline();
          pre.appendChild(srcSpan('mdp-s-math', lines[index]));
          index++;
        }

        continue;
      }

      // A table is a run of pipe rows. Which row is the heading is only
      // knowable from the alignment row underneath it, so the whole run is
      // measured before any of it is drawn.
      if (SRC.tableRow.test(raw)) {
        var start = index;
        var stop = index;
        while (stop < lines.length && SRC.tableRow.test(lines[stop])) { stop++; }

        var ruleAt = -1;
        for (var scan = start; scan < stop; scan++) {
          if (SRC.tableRule.test(lines[scan])) { ruleAt = scan; break; }
        }

        for (var row = start; row < stop; row++) {
          newline();
          if (row === ruleAt) {
            pre.appendChild(srcSpan('mdp-s-table-rule', lines[row]));
          } else {
            appendTableRow(pre, lines[row], ruleAt > start && row === ruleAt - 1);
          }
        }

        index = stop;
        continue;
      }

      newline();

      if (SRC.heading.test(raw)) {
        pre.appendChild(srcSpan('mdp-s-heading', raw));
        index++;
        continue;
      }

      if (SRC.rule.test(raw)) {
        pre.appendChild(srcSpan('mdp-s-rule', raw));
        index++;
        continue;
      }

      var quote = raw.match(SRC.quote);
      if (quote) {
        pre.appendChild(srcSpan('mdp-s-quote', quote[1]));
        appendSourceInline(pre, quote[2]);
        index++;
        continue;
      }

      var list = raw.match(SRC.list);
      if (list) {
        if (list[1]) { pre.appendChild(document.createTextNode(list[1])); }
        pre.appendChild(srcSpan('mdp-s-list', list[2]));
        pre.appendChild(document.createTextNode(list[3]));
        appendSourceInline(pre, raw.slice(list[0].length));
        index++;
        continue;
      }

      appendSourceInline(pre, raw);
      index++;
    }
  }

  /*
   * Paints the raw text. Text always goes in through textContent, so a file's
   * own angle brackets are never parsed as markup on the way in; highlighting
   * is layered on afterwards and is a nicety, never a failure.
   */
  function paintSource(source) {
    var pre = document.createElement('pre');
    pre.className = 'mdp-source';

    var highlight = state.highlightSource && source.length <= MAX_HIGHLIGHTED_SOURCE;

    if (highlight) {
      try { highlightSourceInto(pre, source); }
      catch (_) {
        pre.textContent = source;   // a tokeniser bug must not blank the view
      }
    } else {
      pre.textContent = source;
    }

    content.textContent = '';
    content.appendChild(pre);
  }

  /* Re-shows the document already on screen under the current view settings. */
  function redrawCurrentDocument() {
    if (state.lastMessage) { render(state.lastMessage); }
  }

  // ------------------------------------------------------------ expanded view ---

  /*
   * The document normally holds a centred reading measure (the 980px cap in
   * preview.css) because long lines are hard to read. In a wide pane that
   * leaves margins, and for a wide table or diagram they are wasted. Expanding
   * drops the cap; collapsing restores it by removing the class, so the width
   * has exactly one definition and cannot drift out of step with itself.
   */
  var expand = { toggle: document.getElementById('expand-view') };

  function readExpandPreference() {
    try { return window.localStorage.getItem('mdp.expanded') === '1'; }
    catch (_) { return false; }
  }

  function storeExpandPreference() {
    try { window.localStorage.setItem('mdp.expanded', state.expanded ? '1' : '0'); }
    catch (_) { /* storage unavailable; the choice just will not persist */ }
  }

  /*
   * Whether expanding would visibly do anything. The document is only narrower
   * than the pane once the pane is wider than the reading measure — below that
   * it already fills the width and the toggle is a no-op, which is worse than
   * a disabled control because it looks broken.
   *
   * Measured rather than compared against 980: the cap lives in the stylesheet
   * and this must not carry a second copy of it. Already-expanded always
   * counts as available, or there would be no way back.
   */
  var EXPAND_MIN_MARGIN = 60;   // total slack, so ~30px a side

  function expandAvailable() {
    if (state.expanded) { return true; }

    var pane = document.documentElement.clientWidth || window.innerWidth || 0;
    var used = content.getBoundingClientRect().width;

    // Unknown geometry (pre-layout, jsdom): do not disable on a guess.
    if (pane === 0 || used === 0) { return true; }

    return (pane - used) >= EXPAND_MIN_MARGIN;
  }

  function syncExpandToggle() {
    content.classList.toggle('mdp-expanded', state.expanded === true);

    if (expand.toggle) {
      var available = expandAvailable();

      expand.toggle.setAttribute('aria-pressed', state.expanded ? 'true' : 'false');
      expand.toggle.disabled = !available;
      setTip(expand.toggle, !available
        ? 'Expand preview width\nThe document already fills the pane. Widen the preview pane to use this.'
        : state.expanded
          ? 'Restore preview width\nBack to the centred reading width.'
          : 'Expand preview width\nUse the empty margins either side.');
    }

    // A wider document moves every diagram and image, so any highlight boxes
    // drawn over them are now in the wrong place.
    rerunActiveFindSoon();
  }

  // -------------------------------------------------------- link destination ---

  /*
   * Where a click on a local link goes. Two modes, both legitimate, so this is
   * a mode switch rather than an on/off toggle:
   *
   *   navigate  the host steers File Explorer to the file and selects it, so
   *             the preview follows the link. Works for any file that exists -
   *             selecting a file executes nothing.
   *   app       the file opens in its default application (the host applies
   *             its inert-type allowlist, as it always has).
   *
   * The page only declares the mode per click; the host stays the authority on
   * what actually happens to the path.
   */
  var linkMode = { toggle: document.getElementById('link-mode') };

  function readLinkModePreference() {
    try {
      return window.localStorage.getItem('mdp.linkMode') === 'app' ? 'app' : 'navigate';
    } catch (_) { return 'navigate'; }
  }

  function storeLinkModePreference() {
    try { window.localStorage.setItem('mdp.linkMode', state.linkMode); }
    catch (_) { /* storage unavailable; the choice just will not persist */ }
  }

  function syncLinkModeToggle() {
    if (!linkMode.toggle) { return; }

    var app = state.linkMode === 'app';
    linkMode.toggle.setAttribute('aria-pressed', app ? 'true' : 'false');
    setTip(linkMode.toggle, app
      ? 'Links open in the default app\nClick to reveal them in File Explorer instead.'
      : 'Links reveal the file in File Explorer\nClick to open them in the default app instead.');
  }

  // ------------------------------------------------------------ find in page ---

  /*
   * In-page find: the browser's own find UI is disabled along with its other
   * accelerators, and doing it here gives styled matches and a match counter.
   * Matches are wrapped in <mark> elements per text node (a match spanning two
   * inline elements is not found — a limitation shared with plenty of in-app
   * finders and irrelevant for prose search in practice).
   */
  var MAX_FIND_MATCHES = 2000;

  var find = {
    bar: document.getElementById('toolbar'),
    input: document.getElementById('find-input'),
    count: document.getElementById('find-count'),
    marks: [],
    overlays: [],            // highlight boxes drawn over SVG (diagram) text
    index: -1,
    timer: 0,
    opts: { matchCase: false, matchWord: false, regex: false }
  };

  // ----------------------------------------------------- search options ---

  /*
   * The three options are independent and combine freely.
   *
   * Whole-word plus regular expression is the only pairing with a wrinkle: the
   * pattern is wrapped as \\b(?:...)\\b, so a pattern that begins or ends
   * with a non-word character can never match. That is how every editor with
   * both switches behaves; the tooltip says so rather than disabling the pair.
   */
  function readFindOptions() {
    try {
      var raw = JSON.parse(window.localStorage.getItem('mdp.findOpts') || 'null');
      if (!raw) { return; }
      find.opts.matchCase = raw.matchCase === true;
      find.opts.matchWord = raw.matchWord === true;
      find.opts.regex = raw.regex === true;
    } catch (_) { /* defaults stand */ }
  }

  function storeFindOptions() {
    try {
      window.localStorage.setItem('mdp.findOpts', JSON.stringify(find.opts));
    } catch (_) { /* storage unavailable; the choice just will not persist */ }
  }

  function escapeRegExp(text) {
    return text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  }

  /* Returns a global RegExp, or null when the user's own pattern is invalid. */
  function buildFindPattern(query) {
    var source = find.opts.regex ? query : escapeRegExp(query);
    if (find.opts.matchWord) { source = '\\b(?:' + source + ')\\b'; }

    try {
      return new RegExp(source, find.opts.matchCase ? 'g' : 'gi');
    } catch (_) {
      return null;
    }
  }

  /*
   * The toolbar is the only chrome: search, its options row, and the contents
   * toggle all live in it, and its height is reserved below it so the document
   * is never covered (the contents rail, by design, does hang over content).
   */
  function layoutChrome() {
    var offset = 0;

    if (!find.bar.hidden) {
      offset = find.bar.offsetHeight;

      // Before first layout (and under jsdom, which has none) the measure is
      // 0; fall back to the nominal heights so the document is never covered
      // during the frame the bar appears in.
      if (offset === 0) {
        var optionsRow = document.getElementById('find-opts');
        offset = 41 + (optionsRow && !optionsRow.hidden ? 34 : 0);
      }
    }

    document.documentElement.style.setProperty('--mdp-bar-offset', offset + 'px');
  }

  /*
   * The search field's border answers one question: did that find anything?
   *
   *   ''      no query — neutral
   *   hit     matches exist — green
   *   miss    a query with nothing to show for it — red
   *   invalid a regular expression that will not compile — red, and the
   *           counter says why
   *
   * Every exit from runFind() goes through here, so the field can never be
   * left wearing a stale colour from the previous query.
   */
  /*
   * The cycle buttons are only meaningful with somewhere to cycle to: one match
   * has no next, and none would leave two dead controls sitting in the field.
   */
  function syncFindCycle() {
    var cycle = document.getElementById('find-cycle');
    if (cycle) { cycle.hidden = find.marks.length < 2; }
    measureFindActions();
  }

  /*
   * The input reserves exactly as much room on its right as the count and
   * buttons currently occupy, so a growing match count ("1/1247") can never end
   * up underneath them.
   */
  function measureFindActions() {
    var actions = document.querySelector('.mdp-find-actions');

    // A zero here is ambiguous — nothing to reserve room for, or nothing laid
    // out yet — so the toolbar's own height is what says whether measuring is
    // meaningful. Reading the actions' width alone left the padding stuck at
    // whatever the last non-empty query needed.
    if (!actions || find.bar.offsetHeight === 0) { return; }

    var width = actions.offsetWidth;
    document.documentElement.style.setProperty(
      '--mdp-find-actions-width', (width > 0 ? width + 8 : 8) + 'px');
  }

  function setFindState(name) {
    find.bar.classList.toggle('mdp-find-hit', name === 'hit');
    find.bar.classList.toggle('mdp-find-miss', name === 'miss');
    find.bar.classList.toggle('mdp-find-invalid', name === 'invalid');
  }

  function showToolbar() {
    find.bar.hidden = false;
    layoutChrome();
    syncRail();
  }

  function openFind() {
    showToolbar();
    find.input.focus();
    find.input.select();
    if (find.input.value) { runFind(find.input.value); }
  }

  /*
   * Dismisses the toolbar for the document on screen. Deliberately not
   * remembered: the next selection gets the toolbar back, because it is the
   * only way to reach search, the contents rail, the theme and the trust
   * control, and a preference silently hiding all of that is a trap. Closing
   * it must still work while it is closed, which is the substance of issue #1
   * — see the note on .mdp-toolbar[hidden] in preview.css.
   */
  function closeToolbar() {
    find.bar.hidden = true;
    closeTrustDialog();
    layoutChrome();
    syncRail();               // the rail's control surface is gone; so is it
    clearFindMarks();
    setFindState('');
    find.count.textContent = '';
  }

  function clearFindMarks() {
    for (var i = 0; i < find.marks.length; i++) {
      var mark = find.marks[i];
      var parent = mark.parentNode;
      if (!parent) { continue; }

      if (mark.nodeName === 'MARK') {
        parent.replaceChild(document.createTextNode(mark.textContent), mark);
        parent.normalize();   // merge the split text nodes back together
      } else {
        parent.removeChild(mark);   // a diagram overlay; nothing to splice back
      }
    }

    find.marks = [];
    find.overlays = [];
    find.index = -1;
  }

  /*
   * Text drawn inside a diagram cannot be wrapped in <mark>.
   *
   * An <svg> subtree renders only SVG elements, so an HTML <mark> spliced into
   * a <text> would make the label disappear. Instead the match is measured
   * with a Range and a box is drawn over it, positioned in document
   * coordinates so it scrolls with the diagram. The box is what goes into
   * find.marks, which keeps counting, cycling and scroll-into-view identical
   * for diagram and prose matches.
   *
   * Diagrams scale with the pane (useMaxWidth), so a resize re-runs the search
   * rather than leaving the boxes behind.
   */
  function addDiagramHighlight(node, start, end) {
    var rect = null;

    try {
      var range = document.createRange();
      range.setStart(node, start);
      range.setEnd(node, end);
      rect = range.getBoundingClientRect();
    } catch (_) { /* measured below instead */ }

    // Ranges over SVG text are not measurable everywhere; fall back to the
    // whole label, which still boxes the right diagram node.
    if (!rect || (!rect.width && !rect.height)) {
      var owner = node.parentNode;
      if (owner && owner.getBoundingClientRect) {
        try { rect = owner.getBoundingClientRect(); } catch (_) { rect = null; }
      }
    }

    var box = document.createElement('div');
    box.className = 'mdp-find mdp-find-overlay';

    if (rect) {
      box.style.left = (rect.left + window.scrollX) + 'px';
      box.style.top = (rect.top + window.scrollY) + 'px';
      box.style.width = rect.width + 'px';
      box.style.height = rect.height + 'px';
    }

    document.body.appendChild(box);
    find.marks.push(box);
    find.overlays.push(box);
  }

  function runFind(query) {
    clearFindMarks();

    query = String(query || '');
    if (query.length === 0) {
      setFindState('');
      find.count.textContent = '';
      syncFindCycle();
      return;
    }

    var pattern = buildFindPattern(query);
    if (!pattern) {
      // Only reachable in regex mode: the user is mid-pattern, or wrong.
      setFindState('invalid');
      find.count.textContent = 'bad pattern';
      syncFindCycle();
      return;
    }
    setFindState('');

    // Snapshot first: wrapping matches mutates the tree under the walker.
    //
    // The filter is not optional, but it is narrower than it looks. A rendered
    // mermaid diagram injects a <style> block full of "#mermaid-..." selectors
    // and MathJax emits similar machinery; counting those invisible nodes made
    // a search for "mermaid" report 146 matches on a document that visibly
    // contains four. Only those wrappers are skipped -- <text> and <tspan>
    // inside a diagram hold real, visible labels and are searched.
    var walker = document.createTreeWalker(content, NodeFilter.SHOW_TEXT, {
      acceptNode: function (node) {
        if (!node.nodeValue || node.nodeValue.length === 0) {
          return NodeFilter.FILTER_REJECT;
        }

        for (var el = node.parentNode; el && el !== content; el = el.parentNode) {
          var name = el.nodeName.toUpperCase();
          if (name === 'STYLE' || name === 'SCRIPT' || name === 'MJX-CONTAINER' ||
              name === 'TITLE' || name === 'DESC' || name === 'DEFS' ||
              name === 'METADATA') {
            return NodeFilter.FILTER_REJECT;
          }
        }

        return NodeFilter.FILTER_ACCEPT;
      }
    });

    var textNodes = [];
    while (walker.nextNode()) { textNodes.push(walker.currentNode); }

    for (var n = 0; n < textNodes.length && find.marks.length < MAX_FIND_MATCHES; n++) {
      var node = textNodes[n];
      var parent = node.parentNode;
      var inDiagram = parent && parent.closest && parent.closest('svg') !== null;

      if (inDiagram) {
        // No splitting: measure every match in place, then draw the boxes.
        pattern.lastIndex = 0;
        var m;
        while ((m = pattern.exec(node.nodeValue)) !== null &&
               find.marks.length < MAX_FIND_MATCHES) {
          if (m[0].length === 0) { pattern.lastIndex++; continue; }
          addDiagramHighlight(node, m.index, m.index + m[0].length);
        }

        continue;
      }

      var rest = node;
      while (find.marks.length < MAX_FIND_MATCHES) {
        pattern.lastIndex = 0;
        var hit = pattern.exec(rest.nodeValue);
        if (!hit || hit[0].length === 0) { break; }

        var matchNode = rest.splitText(hit.index);
        rest = matchNode.splitText(hit[0].length);

        var mark = document.createElement('mark');
        mark.className = 'mdp-find';
        matchNode.parentNode.replaceChild(mark, matchNode);
        mark.appendChild(matchNode);

        find.marks.push(mark);
      }
    }

    // Images: alt, title, and any text the host extracted from an SVG. One
    // box per image however many times the pattern occurs inside it - there
    // is no meaningful "second position" on a replaced element.
    var images = content.querySelectorAll('img');
    for (var im = 0; im < images.length && find.marks.length < MAX_FIND_MATCHES; im++) {
      var image = images[im];
      var haystack2 = (image.getAttribute('alt') || '') + '\n' +
                      (image.getAttribute('title') || '') + '\n' +
                      (state.imageText[image.src] || '');

      pattern.lastIndex = 0;
      if (haystack2.trim().length > 0 && pattern.test(haystack2)) {
        addImageHighlight(image);
      }
    }

    if (find.marks.length > 0) {
      setFindState('hit');
      setCurrentMatch(0);
    } else {
      setFindState('miss');
      find.count.textContent = '0/0';
    }

    syncFindCycle();
  }

  function setCurrentMatch(index) {
    if (find.marks.length === 0) { return; }

    if (find.index >= 0 && find.marks[find.index]) {
      find.marks[find.index].classList.remove('mdp-find-current');
    }

    find.index = ((index % find.marks.length) + find.marks.length) % find.marks.length;
    var current = find.marks[find.index];
    current.classList.add('mdp-find-current');

    // A match inside collapsed front matter is invisible; open it first.
    var details = current.closest && current.closest('details');
    if (details && !details.open) { details.open = true; }

    try { current.scrollIntoView({ block: 'center' }); } catch (_) { /* jsdom */ }

    var suffix = find.marks.length >= MAX_FIND_MATCHES ? '+' : '';
    find.count.textContent = (find.index + 1) + '/' + find.marks.length + suffix;
  }

  // ------------------------------------------------------------- image text ---

  /*
   * Text inside an <img> is unreachable from this document: an SVG loaded
   * through <img> is a separate, non-scriptable document, and a raster image
   * has no DOM at all. The HOST already serves every document-relative image
   * from disk, so for SVGs it can also read the <text> the diagram draws.
   * After each render the page asks for the text of the document's SVG
   * images; the reply fills state.imageText, and the search treats it - along
   * with every image's alt and title - as that image's haystack. Matches box
   * the whole image: the exact word position inside a replaced element is not
   * knowable from out here.
   */
  var MAX_IMAGE_TEXT_REQUESTS = 40;

  function requestImageText() {
    var images = content.querySelectorAll('img');
    var urls = [];

    for (var i = 0; i < images.length && urls.length < MAX_IMAGE_TEXT_REQUESTS; i++) {
      var src = images[i].src || '';
      if (src.indexOf(DOC_ORIGIN + '/') === 0 &&
          /\.svg$/i.test(src) &&
          urls.indexOf(src) < 0) {
        urls.push(src);
      }
    }

    if (urls.length > 0) {
      post({ kind: 'imageTextRequest', urls: urls });
    }

    // Once an image gets its real size, any overlay drawn over it while it
    // measured 0x0 is wrong; re-run an active search to reposition.
    for (var j = 0; j < images.length; j++) {
      images[j].addEventListener('load', rerunActiveFindSoon, { once: true });
    }
  }

  function rerunActiveFindSoon() {
    if (find.bar.hidden || !find.input.value) { return; }
    window.clearTimeout(find.timer);
    find.timer = window.setTimeout(function () { runFind(find.input.value); }, 100);
  }

  function addImageHighlight(image) {
    // A still-loading image measures 0x0; box it anyway so the match counts,
    // and the load listener below re-runs the search once real geometry
    // exists. Skipping it would silently drop the match.
    var rect = image.getBoundingClientRect();

    var box = document.createElement('div');
    box.className = 'mdp-find mdp-find-overlay mdp-find-image';
    box.style.left = (rect.left + window.scrollX) + 'px';
    box.style.top = (rect.top + window.scrollY) + 'px';
    box.style.width = rect.width + 'px';
    box.style.height = rect.height + 'px';

    document.body.appendChild(box);
    find.marks.push(box);
    find.overlays.push(box);
  }

  // ----------------------------------------------------------------- mermaid ---

  function mermaidTheme() {
    return state.appearance === 'dark' ? 'dark' : 'default';
  }

  function renderMermaid(root, token, warnings) {
    var blocks = root.querySelectorAll('pre.mermaid');
    if (blocks.length === 0) { return Promise.resolve(false); }

    return loadAsset('js/mermaid.min.js')
      .then(function () {
        if (!isCurrent(token) || !window.mermaid) { return false; }

        window.mermaid.initialize({
          startOnLoad: false,
          theme: mermaidTheme(),
          // securityLevel 'strict' disables click handlers and inline HTML in
          // labels. Non-negotiable for untrusted input.
          securityLevel: 'strict',
          fontFamily: '"Segoe UI", system-ui, sans-serif',
          logLevel: 'error',
          maxTextSize: 200000,
          flowchart: { htmlLabels: false, useMaxWidth: true },
          sequence: { useMaxWidth: true },
          gantt: { useMaxWidth: true }
        });

        return window.mermaid.run({ nodes: blocks, suppressErrors: true })
          .then(function () {
            if (!isCurrent(token)) { return false; }
            // suppressErrors keeps one bad diagram from aborting the batch; find
            // the ones that never got processed and label them.
            for (var i = 0; i < blocks.length; i++) {
              if (!blocks[i].hasAttribute('data-processed')) {
                var wrapper = blocks[i].closest('.mermaid-block');
                if (wrapper) { wrapper.setAttribute('data-failed', 'true'); }
              }
            }
            return true;
          });
      })
      .catch(function (error) {
        warnings.push('Mermaid: ' + error.message);
        return false;
      });
  }

  // -------------------------------------------------------------------- math ---

  var MATH_DELIMITERS = /\$\$[\s\S]{1,20000}?\$\$|\\\[[\s\S]{1,20000}?\\\]|\\\((?:[\s\S]{1,4000}?)\\\)|\\begin\{(?:equation|align|aligned|gather|multline|cases|matrix|pmatrix|bmatrix|vmatrix)\*?\}/;
  var SINGLE_DOLLAR = /(?:^|[\s(\[{])\$(?!\s)(?:[^\n$\\]|\\.){1,300}?(?<!\s)\$(?![\w$])/;

  function documentHasMath(source) {
    if (MATH_DELIMITERS.test(source)) { return true; }
    if (state.settings.singleDollarMath === true) {
      try { return SINGLE_DOLLAR.test(source); } catch (_) { return false; }
    }
    return false;
  }

  function renderMath(root, token, warnings) {
    window.__mdpMathSingleDollar = state.settings.singleDollarMath === true;
    window.__mdpOnMathLoaderFailure = function (message) {
      warnings.push('MathJax: ' + message);
    };

    return loadAsset('js/mathjax-config.js')
      .then(function () { return loadAsset('js/tex-mml-svg.js'); })
      .then(function () {
        if (!isCurrent(token)) { return false; }
        var mj = window.MathJax;
        if (!mj || !mj.startup || !mj.startup.promise) { return false; }
        return mj.startup.promise
          .then(function () {
            if (!isCurrent(token)) { return false; }
            return mj.typesetPromise([root]);
          })
          .then(function () { return isCurrent(token); });
      })
      .catch(function (error) {
        warnings.push('MathJax: ' + error.message);
        return false;
      });
  }

  // ------------------------------------------------------------------- theme ---

  /*
   * Seven appearances: System (the host resolves light/dark from Windows and
   * its registry settings, rendered with the stock GitHub palettes) and six
   * named palettes with a fixed lightness. The name is a per-user, page-side
   * preference; the host keeps sending its light/dark signal, which only
   * matters while System is selected.
   */
  var THEME_LIGHTNESS = {
    paper: 'light', arctic: 'light', ledger: 'light',
    harbor: 'dark', midnight: 'dark', carbon: 'dark'
  };

  function readThemePreference() {
    try {
      var raw = window.localStorage.getItem('mdp.theme');
      return (raw && THEME_LIGHTNESS[raw]) ? raw : 'system';
    } catch (_) {
      return 'system';
    }
  }

  function storeThemePreference(name) {
    try { window.localStorage.setItem('mdp.theme', name); }
    catch (_) { /* storage unavailable; the choice just will not persist */ }
  }

  /* Host light/dark signal; only decides anything while the theme is System. */
  function applyTheme(theme) {
    state.theme = theme === 'dark' ? 'dark' : 'light';
    applyAppearance();
  }

  function applyAppearance() {
    var palette;
    var dark;

    if (THEME_LIGHTNESS[state.themeName]) {
      palette = state.themeName;
      dark = THEME_LIGHTNESS[palette] === 'dark';
    } else {
      dark = state.theme === 'dark';
      palette = dark ? 'github-dark' : 'github-light';
    }

    state.appearance = dark ? 'dark' : 'light';
    document.documentElement.setAttribute('data-mdp-theme', palette);

    // Named palettes need themes.css to override the GitHub sheets' colours
    // directly: the vendored sheets are flattened builds with the colours
    // hard-coded, so variable overrides alone cannot re-tint them.
    if (THEME_LIGHTNESS[palette]) {
      document.documentElement.setAttribute('data-mdp-named', '');
    } else {
      document.documentElement.removeAttribute('data-mdp-named');
    }

    // The GitHub body/code sheets still carry the document's base styling and
    // the syntax colours; a named palette picks the pair matching its own
    // lightness and re-tints via the tokens in themes.css.
    setMedia('css-body-light', !dark);
    setMedia('css-body-dark', dark);
    setMedia('css-code-light', !dark);
    setMedia('css-code-dark', dark);

    document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
  }

  function setThemeName(name) {
    var before = state.appearance;
    state.themeName = THEME_LIGHTNESS[name] ? name : 'system';
    storeThemePreference(state.themeName);
    applyAppearance();

    // Mermaid bakes colours into its SVG at render time; a lightness flip
    // needs the diagrams redrawn. A same-lightness palette change does not -
    // diagram colours come from mermaid's own light/dark themes, not ours.
    if (state.appearance !== before &&
        content.querySelector('pre.mermaid[data-processed]')) {
      post({ kind: 'rerenderRequested', reason: 'theme' });
    }
  }

  function setMedia(id, enabled) {
    var el = document.getElementById(id);
    if (el) { el.media = enabled ? 'all' : 'not all'; }
  }

  function applyFontScale(percent) {
    var scale = Math.min(300, Math.max(50, Number(percent) || 100)) / 100;
    document.documentElement.style.setProperty('--mdp-font-scale', String(scale));
  }

  // ------------------------------------------------------------------ render ---

  function render(message) {
    // Kept so the view toggles can redraw the same document without asking the
    // host for it again.
    state.lastMessage = message;

    // Adopt the host's token rather than minting our own — see isCurrent().
    var token = typeof message.token === 'number' ? message.token : (state.token + 1);
    state.token = token;

    var started = (window.performance && performance.now()) || Date.now();
    var warnings = [];

    if (message.settings) {
      state.settings = Object.assign(defaultSettings(), message.settings);
    }
    if (message.docBase) { state.docBase = message.docBase; }

    // A search belongs to the document it was typed against. Explorer reuses
    // this page for every selection, so without this the query — and its match
    // count and green border — follow the reader from one file to the next and
    // describe a document they are no longer looking at.
    //
    // Only a change of document clears it. A redraw of the SAME document (a
    // theme flip, a trust change) must keep the query, or those controls would
    // wipe the search out from under whoever just used them. The host is the
    // only party that can tell those apart; a host that sends no generation at
    // all keeps the old carry-across behaviour rather than clearing blindly.
    var generation = typeof message.documentGeneration === 'number'
      ? message.documentGeneration
      : state.documentGeneration;

    if (generation !== state.documentGeneration) {
      state.documentGeneration = generation;

      // Before anything else reads find.input.value below.
      window.clearTimeout(find.timer);   // a debounce still holding the old query
      find.input.value = '';
      find.count.textContent = '';
      setFindState('');
    }

    // Trust is the host's to know: it holds the path and the stored grant. A
    // render is the only moment the page hears the current answer.
    state.documentName = String(message.documentName || '');
    state.trusted = message.trusted === true;
    state.trustable = message.trustable === true;
    state.taskEditable = message.taskEditable === true;
    // Per document, so the host is the authority; the page never carries one
    // document's answer over to the next.
    state.editTasks = message.taskEditOn === true;
    syncTrustToggle();
    closeTrustDialog();

    // Any render gets the toolbar back — closing it is scoped to the document
    // that was on screen, not made into a standing preference. Usually that
    // means the next selection; a host-driven redraw (a theme flip on a
    // document with diagrams in it) also counts, which is the same rule
    // applied consistently rather than a special case worth carving out.
    showToolbar();

    applyTheme(message.theme || state.theme);
    applyFontScale(state.settings.fontScalePercent);
    clearNotice();

    var source = String(message.markdown == null ? '' : message.markdown);
    var split = splitFrontMatter(source);
    state.bodyLine = split.bodyLine || 0;

    // Recounted by resolveDocumentUrl during the parse below; without the
    // reset a document with no remote images would inherit the last one's
    // count and leave the trust toggle enabled with nothing to trust.
    state.remoteImages = 0;
    var env = {};
    var html = '';

    if (state.viewSource) {
      // The whole file, front matter included: the point is to show what is
      // actually on disk, not a tidied version of it.
      paintSource(source);
    } else {
      try {
        html = parserFor(state.settings).render(split.body, env);
      } catch (error) {
        fail(token, 'Markdown parse failed: ' + error.message);
        return;
      }

      if (!isCurrent(token)) { return; }

      if (split.frontMatter !== null && state.settings.showFrontMatter) {
        html = frontMatterHtml(split.frontMatter) + html;
      }

      content.innerHTML = html;

      if (state.settings.allowRawHtml) {
        try { sanitiseRawHtml(content); }
        catch (error) { warnings.push('Sanitiser: ' + error.message); }
      }

      if (state.settings.taskLists) {
        try { applyTaskLists(content); }
        catch (error) { warnings.push('Task lists: ' + error.message); }
      }
    }

    if (!isCurrent(token)) { return; }
    syncViewSourceControls();

    // Only knowable once the body is on screen: whether there are checkboxes
    // to edit, how many remote images there are to trust, and whether the
    // document leaves any margin worth expanding into. Each decides whether
    // its toggle is a live control or a disabled one that says why.
    state.hasTasks = content.querySelector('li.mdp-task') !== null;
    syncTaskEditToggle();
    syncTrustToggle();
    syncExpandToggle();

    markBrokenImages(content);
    requestImageText();

    try { buildToc(); }
    catch (error) { warnings.push('Contents: ' + error.message); }

    // The old document's marks died with its innerHTML; re-run against the new
    // one so an open find bar keeps working across selections. Diagram overlays
    // live on <body>, outside the replaced subtree, so they must be removed by
    // hand rather than left to leak one set per selection.
    for (var ov = 0; ov < find.overlays.length; ov++) {
      var box = find.overlays[ov];
      if (box.parentNode) { box.parentNode.removeChild(box); }
    }

    find.marks = [];
    find.overlays = [];
    find.index = -1;
    if (!find.bar.hidden && find.input.value) { runFind(find.input.value); }

    // Explorer reuses the same page for the next selection; without this the new
    // document opens scrolled to wherever the previous one was.
    window.scrollTo(0, 0);

    var mermaidStep = !state.viewSource && state.settings.mermaid && env.usedMermaid
      ? renderMermaid(content, token, warnings)
      : Promise.resolve(false);

    var mathStep = !state.viewSource && state.settings.math && documentHasMath(split.body)
      ? renderMath(content, token, warnings)
      : Promise.resolve(false);

    Promise.all([mermaidStep, mathStep]).then(function (results) {
      if (!isCurrent(token)) { return; }
      if (warnings.length > 0) {
        showNotice(warnings.join('  •  '), 'warning');
      }
      post({
        kind: 'rendered',
        token: token,
        elapsedMs: Math.round(((window.performance && performance.now()) || Date.now()) - started),
        usedMermaid: results[0] === true,
        usedMath: results[1] === true,
        warnings: warnings
      });
    });
  }

  function fail(token, message) {
    content.textContent = '';
    showNotice(message, 'error');
    post({ kind: 'failed', token: token, message: message });
  }

  // ---------------------------------------------------------------- messages ---

  function onHostMessage(event) {
    var message = event.data;
    if (typeof message === 'string') {
      try { message = JSON.parse(message); } catch (_) { return; }
    }
    if (!message || typeof message.kind !== 'string') { return; }

    switch (message.kind) {
      case 'render':
        render(message);
        break;

      case 'theme':
        applyTheme(message.theme);
        // Mermaid bakes colours into the SVG at render time, so a theme flip
        // needs the diagrams redrawn. Nothing else does.
        if (content.querySelector('pre.mermaid[data-processed]')) {
          post({ kind: 'rerenderRequested', reason: 'theme' });
        }
        break;

      case 'settings':
        state.settings = Object.assign(defaultSettings(), message.settings || {});
        applyFontScale(state.settings.fontScalePercent);
        break;

      case 'toc': {
        // The host's context-menu entry. When the toolbar is closed the ask
        // is unambiguous - bring the contents back - so opening must not
        // depend on (or blindly flip) the stored preference. With the toolbar
        // already up, it is a plain toggle.
        var toolbarWasHidden = find.bar.hidden;
        showToolbar();
        setTocVisible(toolbarWasHidden ? true : !state.tocVisible);
        break;
      }

      case 'imageText':
        // The host's answer to imageTextRequest. Refresh an in-flight search:
        // the text may create matches the walk could not see.
        (message.images || []).forEach(function (entry) {
          if (entry && entry.url) { state.imageText[entry.url] = entry.text || ''; }
        });
        if (!find.bar.hidden && find.input.value) { runFind(find.input.value); }
        break;

      case 'find':
        // From the host's context-menu entry. Deliberately open-not-toggle: the
        // page's own Ctrl+F handler may also fire for the same gesture, and two
        // "open" calls are harmless where two toggles would cancel out. Esc and
        // the × close it.
        openFind();
        break;

      default:
        break;
    }
  }

  // -------------------------------------------------------------- navigation ---

  /*
   * A preview pane must never navigate. Anchors stay in-page; http(s) and
   * mailto are handed to the host, which decides whether to shell-execute them.
   */
  document.addEventListener('click', function (event) {
    var anchor = event.target && event.target.closest && event.target.closest('a[href]');
    if (!anchor) { return; }

    var href = anchor.getAttribute('href') || '';
    if (href.charAt(0) === '#') { return; }        // let the browser scroll

    event.preventDefault();

    // A link to a sibling of the previewed document: the host resolves it to
    // the real file and, depending on the toolbar's link-mode switch, either
    // reveals it in File Explorer or opens it in its default application.
    if (href.indexOf(DOC_ORIGIN + '/') === 0) {
      post({ kind: 'openDocument', url: href, mode: state.linkMode });
      return;
    }

    if (/^(https?|mailto):/i.test(href)) {
      post({ kind: 'openExternal', url: href });
    }
  }, true);

  window.addEventListener('beforeunload', function (event) {
    event.preventDefault();
    event.returnValue = '';
  });

  // Surface script errors instead of rendering a blank pane.
  window.addEventListener('error', function (event) {
    post({ kind: 'scriptError', message: String(event.message || 'unknown') });
  });
  window.addEventListener('unhandledrejection', function (event) {
    post({
      kind: 'scriptError',
      message: String((event.reason && event.reason.message) || event.reason || 'unknown')
    });
  });

  // ------------------------------------------------------------------ startup ---

  var h = host();
  if (h) { h.addEventListener('message', onHostMessage); }

  // --- keyboard ---------------------------------------------------------------

  document.addEventListener('keydown', function (event) {
    if ((event.ctrlKey || event.metaKey) && (event.key === 'f' || event.key === 'F')) {
      event.preventDefault();
      openFind();
      return;
    }

    if (event.key === 'Escape' && tip.target) {
      hideTip();
      return;
    }

    // Esc unwinds one layer at a time: the dialog if it is up, then the bar.
    if (event.key === 'Escape' && trust.dialog && !trust.dialog.hidden) {
      event.preventDefault();
      closeTrustDialog();
      return;
    }

    if (event.key === 'Escape' && !find.bar.hidden) {
      event.preventDefault();
      closeToolbar();
      return;
    }

    // F3 / Shift+F3 cycle matches from anywhere, as they do in a browser.
    if (event.key === 'F3' && find.marks.length > 0) {
      event.preventDefault();
      setCurrentMatch(find.index + (event.shiftKey ? -1 : 1));
    }
  });

  // --- search -------------------------------------------------------------------

  find.input.addEventListener('input', function () {
    // Debounced: retyping a query on a long document would otherwise re-walk
    // the whole tree per keystroke.
    window.clearTimeout(find.timer);
    var value = find.input.value;
    find.timer = window.setTimeout(function () { runFind(value); }, 120);
  });

  find.input.addEventListener('keydown', function (event) {
    if (event.key !== 'Enter') { return; }
    event.preventDefault();

    // Enter before the debounce fires: search now rather than doing nothing.
    if (find.marks.length === 0) {
      window.clearTimeout(find.timer);
      runFind(find.input.value);
      return;
    }

    setCurrentMatch(find.index + (event.shiftKey ? -1 : 1));
  });

  document.getElementById('find-next').addEventListener('click', function () {
    setCurrentMatch(find.index + 1);
  });
  document.getElementById('find-prev').addEventListener('click', function () {
    setCurrentMatch(find.index - 1);
  });
  document.getElementById('toolbar-close').addEventListener('click', closeToolbar);

  // --- search options row ---------------------------------------------------------

  readFindOptions();

  var findOptsRow = document.getElementById('find-opts');
  var findOptsButton = document.getElementById('find-options');
  var optionBoxes = {
    matchCase: document.getElementById('find-case'),
    matchWord: document.getElementById('find-word'),
    regex: document.getElementById('find-regex')
  };

  Object.keys(optionBoxes).forEach(function (key) {
    var box = optionBoxes[key];
    box.checked = find.opts[key];
    box.addEventListener('change', function () {
      find.opts[key] = box.checked;
      storeFindOptions();
      if (find.input.value) { runFind(find.input.value); }
      find.input.focus();
    });
  });

  function setFindOptionsOpen(open) {
    findOptsRow.hidden = !open;
    findOptsButton.setAttribute('aria-expanded', open ? 'true' : 'false');
    findOptsButton.classList.toggle('mdp-active', open);
    try { window.localStorage.setItem('mdp.optsRow', open ? '1' : '0'); }
    catch (_) { /* not persisted, still works */ }
    layoutChrome();             // the toolbar just changed height
  }

  findOptsButton.addEventListener('click', function () {
    setFindOptionsOpen(findOptsRow.hidden);
  });

  // The row is part of the toolbar, not a popover: reopen it the way it was left.
  try {
    if (window.localStorage.getItem('mdp.optsRow') === '1') { setFindOptionsOpen(true); }
  } catch (_) { /* default closed */ }

  window.addEventListener('resize', function () {
    layoutChrome();
    hideTip();              // anchored to a control that has just moved
    measureFindActions();
    syncExpandToggle();     // margin worth reclaiming changes with the pane

    // Diagrams and images scale with the pane, so any overlay drawn over one
    // is now in the wrong place. Re-running is cheaper than tracking each box.
    if (!find.bar.hidden && find.overlays.length > 0 && find.input.value) {
      window.clearTimeout(find.timer);
      find.timer = window.setTimeout(function () { runFind(find.input.value); }, 150);
    }
  });

  // --- contents rail -------------------------------------------------------------

  state.tocVisible = readTocPreference();

  document.getElementById('toc-toggle').addEventListener('click', function () {
    setTocVisible(toc.hidden);   // hidden -> open it; open -> hide it
  });

  // --- trust ------------------------------------------------------------------------

  /*
   * Granting asks; withdrawing does not. Confirming the removal of a permission
   * teaches people to click through the dialog that matters.
   */
  if (trust.toggle) {
    trust.toggle.addEventListener('click', function () {
      if (state.trusted) {
        setTrusted(false);
      } else {
        openTrustDialog();
      }
    });
  }

  if (trust.confirm) {
    trust.confirm.addEventListener('click', function () { setTrusted(true); });
  }
  if (trust.cancel) {
    trust.cancel.addEventListener('click', closeTrustDialog);
  }

  // Clicking the backdrop is a decline, like Esc. Clicks inside the panel are
  // not: a stray click while reading must not dismiss the question.
  if (trust.dialog) {
    trust.dialog.addEventListener('click', function (event) {
      if (event.target === trust.dialog) { closeTrustDialog(); }
    });
  }

  // --- task editing -----------------------------------------------------------------

  syncTaskEditToggle();

  if (taskEdit.toggle) {
    taskEdit.toggle.addEventListener('click', function () {
      state.editTasks = !state.editTasks;
      syncTaskEditToggle();
      // The host owns the answer per document; it comes back on the next render.
      post({ kind: 'setTaskEdit', enabled: state.editTasks });
    });
  }

  // --- view source --------------------------------------------------------------

  readViewSourcePreferences();
  syncViewSourceControls();

  if (viewSource.toggle) {
    viewSource.toggle.addEventListener('click', function () {
      state.viewSource = !state.viewSource;
      storeViewSourcePreferences();
      syncViewSourceControls();
      redrawCurrentDocument();
    });
  }

  if (viewSource.highlight) {
    viewSource.highlight.addEventListener('change', function () {
      state.highlightSource = viewSource.highlight.checked;
      storeViewSourcePreferences();
      syncViewSourceControls();
      if (state.viewSource) { redrawCurrentDocument(); }
    });
  }

  // --- expanded view ------------------------------------------------------------

  state.expanded = readExpandPreference();
  syncExpandToggle();

  if (expand.toggle) {
    expand.toggle.addEventListener('click', function () {
      state.expanded = !state.expanded;
      storeExpandPreference();
      syncExpandToggle();
    });
  }

  // --- link destination ---------------------------------------------------------------

  state.linkMode = readLinkModePreference();
  syncLinkModeToggle();

  if (linkMode.toggle) {
    linkMode.toggle.addEventListener('click', function () {
      state.linkMode = state.linkMode === 'app' ? 'navigate' : 'app';
      storeLinkModePreference();
      syncLinkModeToggle();
    });
  }

  // --- theme ----------------------------------------------------------------------

  state.themeName = readThemePreference();

  var themeSelect = document.getElementById('theme-select');
  themeSelect.value = state.themeName;
  themeSelect.addEventListener('change', function () {
    setThemeName(themeSelect.value);
  });

  // The toolbar ships visible, so the offset it reserves has to exist before
  // the first document arrives or the opening render sits underneath it.
  syncTrustToggle();
  layoutChrome();
  syncRail();

  applyTheme('light');
  post({ kind: 'ready', version: VERSION });
})();
