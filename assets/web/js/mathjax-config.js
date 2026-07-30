/*
 * MathJax 3 configuration. Must execute *before* tex-mml-svg.js, so preview.js
 * injects this script first and only when the document actually contains math.
 *
 * SVG output (not CHTML) is deliberate: the tex-mml-svg bundle embeds its own
 * glyph outlines, so no web font is fetched. That is what lets the page run
 * under `connect-src 'none'` and `font-src` restricted to local assets.
 *
 * window.__mdpMathSingleDollar is set by preview.js from user settings before
 * this file is injected.
 */
(function () {
  'use strict';

  var inlineMath = [['\\(', '\\)']];
  if (window.__mdpMathSingleDollar === true) {
    inlineMath.unshift(['$', '$']);
  }

  window.MathJax = {
    startup: {
      // preview.js drives typesetting explicitly, per render, against the
      // freshly injected subtree.
      typeset: false
    },
    tex: {
      inlineMath: inlineMath,
      displayMath: [['$$', '$$'], ['\\[', '\\]']],
      processEscapes: true,
      processEnvironments: true,
      // A previewed file is untrusted input; do not let it define macros that
      // persist, and cap expansion work.
      maxMacros: 1000,
      maxBuffer: 5 * 1024
    },
    svg: {
      fontCache: 'local',      // 'local' keeps each container self-contained
      scale: 1,
      displayAlign: 'left',
      displayIndent: '0'
    },
    options: {
      // skipHtmlTags already covers pre/code/script/style in MathJax 3
      // defaults; this adds our own opt-outs.
      ignoreHtmlClass: 'mdp-no-math|mermaid|hljs',
      processHtmlClass: 'mdp-math',
      enableAssistiveMml: false,
      menuOptions: { settings: { enrich: false } }
    },
    loader: {
      // Everything needed is inside the combined bundle. Fail loudly instead of
      // silently attempting a network fetch that CSP will block anyway.
      failed: function (error) {
        if (window.__mdpOnMathLoaderFailure) {
          window.__mdpOnMathLoaderFailure(String(error && error.message || error));
        }
      }
    }
  };
})();
