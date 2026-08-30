/*
 * preview.js — render controller for the Markdown Explorer preview handler.
 *
 * Contract with the host (WebView2 <-> page, JSON over postMessage):
 *
 *   host -> page  { kind: "render", token, markdown, theme, docBase, settings }
 *   host -> page  { kind: "theme",  theme }
 *   host -> page  { kind: "settings", settings }
 *
 *   page -> host  { kind: "ready", version }
 *   page -> host  { kind: "rendered", token, elapsedMs, usedMermaid, usedMath, warnings[] }
 *   page -> host  { kind: "failed", token, message }
 *   page -> host  { kind: "openExternal", url }
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
  var VERSION = '1.3.0';

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
    imageText: Object.create(null)   // img src -> text the host extracted from it
  };

  var assetLoads = Object.create(null);   // href -> Promise
  var content = document.getElementById('content');
  var notice = document.getElementById('notice');
  var toc = document.getElementById('toc');
  var tocList = document.getElementById('toc-list');

  function defaultSettings() {
    return {
      allowRawHtml: true,
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
      if (/^https?:/i.test(value)) { return value; }
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

    // --- images ------------------------------------------------------------
    var defaultImage = md.renderer.rules.image;
    md.renderer.rules.image = function (tokens, idx, options, env, self) {
      var token = tokens[idx];
      var i = token.attrIndex('src');
      if (i >= 0) {
        var resolved = resolveDocumentUrl(token.attrs[i][1], 'image');
        if (!resolved) {
          // Keep the alt text visible instead of emitting a dead <img>.
          return '<span class="mdp-broken">' +
                 escapeHtml(token.content || token.attrs[i][1]) + '</span>';
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
    if (!match) { return { frontMatter: null, body: source }; }
    return { frontMatter: match[3], body: source.slice(match[0].length) };
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
      box.disabled = true;                     // a preview is read-only
      container.insertBefore(box, node);
      li.classList.add('mdp-task');
    }
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
    var chevron = document.getElementById('toc-chevron');
    var eligible = tocList.childElementCount >= 2;
    var open = state.tocVisible && eligible && !find.bar.hidden;

    toc.hidden = !open;

    if (toggle) {
      toggle.disabled = !eligible;
      toggle.title = eligible ? '' : 'This document has fewer than two headings';
      toggle.setAttribute('aria-expanded', open ? 'true' : 'false');
      toggle.classList.toggle('mdp-active', open);
    }

    if (chevron) { chevron.textContent = open ? '\u25B4' : '\u25BE'; }
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

  function closeToolbar() {
    find.bar.hidden = true;
    layoutChrome();
    syncRail();               // the rail's control surface is gone; so is it
    clearFindMarks();
    find.bar.classList.remove('mdp-find-invalid');
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
      find.bar.classList.remove('mdp-find-invalid');
      find.count.textContent = '';
      return;
    }

    var pattern = buildFindPattern(query);
    if (!pattern) {
      // Only reachable in regex mode: the user is mid-pattern, or wrong.
      find.bar.classList.add('mdp-find-invalid');
      find.count.textContent = 'bad pattern';
      return;
    }
    find.bar.classList.remove('mdp-find-invalid');

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
      setCurrentMatch(0);
    } else {
      find.count.textContent = '0/0';
    }
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
    // Adopt the host's token rather than minting our own — see isCurrent().
    var token = typeof message.token === 'number' ? message.token : (state.token + 1);
    state.token = token;

    var started = (window.performance && performance.now()) || Date.now();
    var warnings = [];

    if (message.settings) {
      state.settings = Object.assign(defaultSettings(), message.settings);
    }
    if (message.docBase) { state.docBase = message.docBase; }

    applyTheme(message.theme || state.theme);
    applyFontScale(state.settings.fontScalePercent);
    clearNotice();

    var source = String(message.markdown == null ? '' : message.markdown);
    var split = splitFrontMatter(source);
    var env = {};
    var html = '';

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

    var mermaidStep = state.settings.mermaid && env.usedMermaid
      ? renderMermaid(content, token, warnings)
      : Promise.resolve(false);

    var mathStep = state.settings.math && documentHasMath(split.body)
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
    // the real file and opens it with its default application.
    if (href.indexOf(DOC_ORIGIN + '/') === 0) {
      post({ kind: 'openDocument', url: href });
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

  // --- theme ----------------------------------------------------------------------

  state.themeName = readThemePreference();

  var themeSelect = document.getElementById('theme-select');
  themeSelect.value = state.themeName;
  themeSelect.addEventListener('change', function () {
    setThemeName(themeSelect.value);
  });

  applyTheme('light');
  post({ kind: 'ready', version: VERSION });
})();
