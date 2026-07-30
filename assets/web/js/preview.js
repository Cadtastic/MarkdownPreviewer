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

  var VERSION = '1.0.0';

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
    mdSignature: null
  };

  var assetLoads = Object.create(null);   // href -> Promise
  var content = document.getElementById('content');
  var notice = document.getElementById('notice');

  function defaultSettings() {
    return {
      allowRawHtml: false,
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
        if (/^https?:/i.test(resolved)) {
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

  // ----------------------------------------------------------------- mermaid ---

  function mermaidTheme() {
    return state.theme === 'dark' ? 'dark' : 'default';
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

  function applyTheme(theme) {
    var dark = theme === 'dark';
    state.theme = dark ? 'dark' : 'light';

    setMedia('css-body-light', !dark);
    setMedia('css-body-dark', dark);
    setMedia('css-code-light', !dark);
    setMedia('css-code-dark', dark);

    document.documentElement.style.colorScheme = dark ? 'dark' : 'light';
    // github-markdown-css paints .markdown-body; mirror it onto the canvas so
    // over-scroll and the area beside a narrow document match.
    var painted = window.getComputedStyle(content).backgroundColor;
    document.documentElement.style.setProperty('--mdp-canvas', painted);
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
  applyTheme('light');
  post({ kind: 'ready', version: VERSION });
})();
