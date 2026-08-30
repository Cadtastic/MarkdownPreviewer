import { JSDOM } from 'jsdom';
import { readFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';
import path from 'node:path';


// Resolved relative to this file so the suite runs from any checkout.
const WEB = path.resolve(path.dirname(new URL(import.meta.url).pathname.replace(/^\/([A-Za-z]:)/, '$1')), '..', '..', 'assets', 'web');

let pass = 0, fail = 0;
const ok  = (n, c, extra='') => { c ? (pass++, console.log(`  PASS  ${n}`)) : (fail++, console.log(`  FAIL  ${n}${extra?'\n        '+extra:''}`)); };

// Build a DOM from index.html, but load scripts manually so we control order and
// can skip the lazily-injected heavyweights (mermaid/MathJax need a real browser).
const html = readFileSync(path.join(WEB, 'index.html'), 'utf8');
const dom = new JSDOM(html, {
  url: 'https://assets.mdpreview.invalid/index.html',
  runScripts: 'outside-only',
  pretendToBeVisual: true,
});
const { window } = dom;
window.scrollTo = () => {};   // jsdom has no layout

// preview.css is a <link>, which jsdom does not fetch. Inline it so the cascade
// is real: the panels' visibility depends on `hidden` beating their own
// `display`, and asserting the .hidden property alone cannot see that.
const styleEl = window.document.createElement('style');
styleEl.textContent = readFileSync(path.join(WEB, 'css', 'preview.css'), 'utf8');
window.document.head.appendChild(styleEl);

/* Is the element actually hidden, as opposed to merely carrying the attribute? */
const isHidden = (el) => window.getComputedStyle(el).display === 'none';

// Intercept lazily injected <script> tags: jsdom will not fetch them, so without
// this the render promise never settles and we cannot observe the lazy-load path
// at all. Recording the src also lets us assert *whether* a bundle was requested,
// which is the real behaviour under test.
const requestedAssets = [];
// Assets whose load should be reported as SUCCEEDING. Anything else gets an
// error, letting us test both the happy path and the degradation path.
const succeedAssets = new Set();
const realAppend = window.document.head.appendChild.bind(window.document.head);
window.document.head.appendChild = (node) => {
  if (node.tagName === 'SCRIPT' && node.src) {
    requestedAssets.push(node.src);
    const succeed = [...succeedAssets].some(name => node.src.endsWith(name));
    setTimeout(() => {
      if (succeed) { node.onload && node.onload(new window.Event('load')); }
      else { node.onerror && node.onerror(new window.Event('error')); }
    }, 0);
    return node;
  }
  return realAppend(node);
};

// Host shim: capture what the page posts back to us.
const posted = [];
const listeners = [];
window.chrome = {
  webview: {
    postMessage: (m) => posted.push(m),
    addEventListener: (_evt, fn) => listeners.push(fn),
  },
};

function evalIn(file) {
  window.eval(readFileSync(path.join(WEB, file), 'utf8'));
}

evalIn('js/markdown-it.min.js');
evalIn('js/markdownItAnchor.umd.js');
evalIn('js/highlight.min.js');

console.log('\n== globals exposed by the vendored bundles ==');
ok('window.markdownit', typeof window.markdownit === 'function');
ok('window.markdownItAnchor', typeof window.markdownItAnchor !== 'undefined');
ok('window.hljs with languages', typeof window.hljs === 'object' && window.hljs.listLanguages().length > 60);

evalIn('js/preview.js');

console.log('\n== startup handshake ==');
ok('page posted {kind:"ready"}', posted.some(m => m.kind === 'ready'), JSON.stringify(posted));
ok('host listener registered', listeners.length === 1);

function render(markdown, settings = {}, theme = 'light') {
  posted.length = 0;
  listeners[0]({
    data: {
      kind: 'render',
      token: 1,
      markdown,
      theme,
      docBase: 'https://doc.mdpreview.invalid/notes/',
      settings: {
        allowRawHtml: false, linkify: true, typographer: false, highlight: true,
        mermaid: false, math: false, singleDollarMath: false, taskLists: true,
        showFrontMatter: true, fontScalePercent: 100, maxAutoDetectBytes: 10240,
        ...settings,
      },
    },
  });
  return window.document.getElementById('content');
}

console.log('\n== core markdown ==');
let c = render('# Title\n\nSome **bold** and `code`.\n\n| a | b |\n|---|---|\n| 1 | 2 |\n');
ok('h1 rendered', c.querySelector('h1')?.textContent.includes('Title'));
ok('strong rendered', !!c.querySelector('strong'));
ok('GFM table rendered', c.querySelectorAll('table td').length === 2);

console.log('\n== heading anchors (GitHub-compatible slugs) ==');
c = render('# Hello, World!\n\n## Some *Heading* with `code`\n');
const ids = [...c.querySelectorAll('h1,h2')].map(h => h.id);
ok('slug strips punctuation and lowercases', ids[0] === 'hello-world', `got ${JSON.stringify(ids)}`);
ok('slug handles inline markup', ids[1] === 'some-heading-with-code', `got ${JSON.stringify(ids)}`);

console.log('\n== syntax highlighting ==');
c = render('```csharp\nvar x = 1;\n```\n');
ok('hljs applied to labelled fence', !!c.querySelector('pre.hljs code.language-csharp'));
ok('keyword span emitted', !!c.querySelector('.hljs-keyword'));

console.log('\n== task lists ==');
c = render('- [x] done\n- [ ] not done\n');
const boxes = c.querySelectorAll('li.mdp-task input[type=checkbox]');
ok('two checkboxes emitted', boxes.length === 2, `got ${boxes.length}`);
ok('first is checked', boxes[0]?.checked === true);
ok('second is unchecked', boxes[1]?.checked === false);
ok('both are disabled (read-only preview)', [...boxes].every(b => b.disabled));
ok('marker text stripped', !c.textContent.includes('[x]') && !c.textContent.includes('[ ]'));

console.log('\n== task lists in a loose list (markdown-it wraps in <p>) ==');
c = render('- [ ] first\n\n- [x] second\n');
ok('loose-list checkboxes found', c.querySelectorAll('li.mdp-task input').length === 2);

console.log('\n== front matter ==');
c = render('---\ntitle: Test\ntags: [a, b]\n---\n\n# Body\n');
ok('front matter collapsed into <details>', !!c.querySelector('details.mdp-frontmatter'));
ok('front matter body preserved', c.querySelector('details.mdp-frontmatter pre')?.textContent.includes('title: Test'));
ok('no stray <hr> from the delimiters', !c.querySelector('hr'));
ok('body still renders as h1', c.querySelector('h1')?.textContent.includes('Body'));

c = render('---\ntitle: Hidden\n---\n\n# Body\n', { showFrontMatter: false });
ok('front matter suppressed when disabled', !c.querySelector('details.mdp-frontmatter'));

console.log('\n== relative image resolution ==');
c = render('![alt](images/pic.png)\n');
ok('relative src rewritten to the doc host',
   c.querySelector('img')?.getAttribute('src') === 'https://doc.mdpreview.invalid/notes/images/pic.png',
   `got ${c.querySelector('img')?.getAttribute('src')}`);
ok('lazy loading applied', c.querySelector('img')?.getAttribute('loading') === 'lazy');

c = render('![alt](../shared/pic.png)\n');
ok('parent-relative path resolves within the host',
   c.querySelector('img')?.getAttribute('src') === 'https://doc.mdpreview.invalid/shared/pic.png',
   `got ${c.querySelector('img')?.getAttribute('src')}`);

c = render('![alt](https://cdn.example.com/pic.png)\n');
ok('absolute https image left intact',
   c.querySelector('img')?.getAttribute('src') === 'https://cdn.example.com/pic.png');

console.log('\n== SECURITY: dangerous URL schemes ==');
// markdown-it's own validateLink refuses these outright, so no anchor is emitted
// at all -- the text renders literally. That is a stronger outcome than an inert
// href, and it means our resolveDocumentUrl acts as a second line of defence for
// the raw-HTML path rather than the primary one.
for (const [label, md] of [
  ['javascript: link', '[x](javascript:alert(1))'],
  ['vbscript: link',   '[x](vbscript:msgbox(1))'],
  ['file: link',       '[x](file:///C:/Windows/win.ini)'],
]) {
  c = render(md + '\n');
  ok(`${label} produces no anchor at all`, c.querySelector('a') === null,
     `got href=${JSON.stringify(c.querySelector('a')?.getAttribute('href'))}`);
  ok(`${label} text is preserved`, c.textContent.includes('x'));
}

c = render('![x](javascript:alert(1))\n');
ok('javascript: image produces no <img>', !c.querySelector('img'));

c = render('![x](file:///C:/Windows/win.ini)\n');
ok('file: image dropped entirely', !c.querySelector('img'));

c = render('![x](data:text/html;base64,PHNjcmlwdD4=)\n');
ok('non-image data: URI dropped', !c.querySelector('img'));

c = render('![x](data:image/png;base64,iVBORw0KGgo=)\n');
ok('image data: URI allowed', !!c.querySelector('img'));

console.log('\n== SECURITY: raw HTML off by default ==');
c = render('<script>window.__pwned = 1;</script>\n\n<img src=x onerror="window.__pwned=2">\n');
ok('no <script> element created', c.querySelectorAll('script').length === 0);
ok('no <img> element created from raw HTML', c.querySelectorAll('img').length === 0);
ok('markup escaped into text', c.textContent.includes('<script>') || c.textContent.includes('onerror'));
ok('nothing executed', window.__pwned === undefined);

c = render('<em>allowed now</em>\n', { allowRawHtml: true });
ok('raw HTML honoured when explicitly enabled', !!c.querySelector('em'));

console.log('\n== SECURITY: raw HTML sanitiser (allowRawHtml = true) ==');
c = render(
  '<script>window.__p1=1;</script>\n\n' +
  '<iframe src="https://evil.example"></iframe>\n\n' +
  '<img src="pic.png" onerror="window.__p2=1" onload="window.__p3=1">\n\n' +
  '<a href="javascript:alert(1)">js</a>\n\n' +
  '<a href="file:///C:/Windows/win.ini">file</a>\n\n' +
  '<a href="https://example.com/ok">ok</a>\n\n' +
  '<svg onload="window.__p4=1"></svg>\n\n' +
  '<form action="https://evil.example"><input name="x"></form>\n\n' +
  '<details><summary>keep me</summary>body</details>\n',
  { allowRawHtml: true });

ok('<script> stripped',  c.querySelectorAll('script').length === 0);
ok('<iframe> stripped',  c.querySelectorAll('iframe').length === 0);
ok('<form> stripped',    c.querySelectorAll('form').length === 0);
ok('<input> stripped',   c.querySelectorAll('input').length === 0);
ok('on* handlers stripped',
   [...c.querySelectorAll('*')].every(e => ![...e.attributes].some(a => a.name.toLowerCase().startsWith('on'))));
ok('javascript: href removed', !c.querySelector('a[href^="javascript"]'));
ok('file: href removed',       !c.querySelector('a[href^="file"]'));
ok('legitimate https href kept', !!c.querySelector('a[href="https://example.com/ok"]'));
ok('relative img src rewritten to the doc host',
   c.querySelector('img')?.getAttribute('src') === 'https://doc.mdpreview.invalid/notes/pic.png',
   `got ${c.querySelector('img')?.getAttribute('src')}`);
ok('benign <details> preserved', !!c.querySelector('details > summary'));
ok('nothing executed', [window.__p1, window.__p2, window.__p3, window.__p4].every(v => v === undefined));

console.log('\n== external link handling ==');
c = render('[out](https://example.com/page)\n');
const anchor = c.querySelector('a');
ok('rel hardened on external links', anchor?.getAttribute('rel') === 'noopener noreferrer nofollow');
ok('marked as external', anchor?.getAttribute('data-mdp-external') === '1');

posted.length = 0;
anchor.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
ok('click routed to the host as openExternal',
   posted.some(m => m.kind === 'openExternal' && m.url === 'https://example.com/page'),
   JSON.stringify(posted));

posted.length = 0;
c = render('# Target\n\n[jump](#target)\n');
c.querySelector('a[href="#target"]').dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
ok('in-page anchor NOT sent to the host', !posted.some(m => m.kind === 'openExternal'));

console.log('\n== document link handling ==');
c = render('[sibling](docs/guide.md)\n');
const docAnchor = c.querySelector('a');
ok('relative link rewritten to the doc host',
   docAnchor?.getAttribute('href') === 'https://doc.mdpreview.invalid/notes/docs/guide.md',
   `got ${docAnchor?.getAttribute('href')}`);
ok('marked as a document link', docAnchor?.getAttribute('data-mdp-doclink') === '1');
ok('not marked external', docAnchor?.getAttribute('data-mdp-external') !== '1');

posted.length = 0;
docAnchor.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
ok('click routed to the host as openDocument',
   posted.some(m => m.kind === 'openDocument' && m.url === 'https://doc.mdpreview.invalid/notes/docs/guide.md'),
   JSON.stringify(posted));
ok('doc link NOT sent as openExternal', !posted.some(m => m.kind === 'openExternal'));

console.log('\n== floating table of contents ==');
const tocEl = window.document.getElementById('toc');
c = render('# One\n\n## Two\n\n## Three\n');
ok('TOC visible for a document with headings', !isHidden(tocEl));
ok('TOC lists every heading', window.document.querySelectorAll('#toc-list a').length === 3,
   `got ${window.document.querySelectorAll('#toc-list a').length}`);
ok('TOC entries link to the heading anchors',
   window.document.querySelector('#toc-list a')?.getAttribute('href') === '#one');

c = render('plain text, no headings\n');
ok('TOC hidden for a document without headings', isHidden(tocEl),
   'the panel is still rendered -- see issue #1');

c = render('# One\n\n## Two\n');
ok('TOC returns for the next heading-ful document', !isHidden(tocEl));

// Collapse: shrinks to the "Contents" pill; independent of visibility; persists.
window.document.getElementById('toc-header').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('header click collapses', tocEl.classList.contains('mdp-collapsed'));
ok('collapse persisted', window.localStorage.getItem('mdp.tocCollapsed') === '1');
window.document.getElementById('toc-collapse').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('chevron click expands again', !tocEl.classList.contains('mdp-collapsed'));

// Hide via the x button; reshow via the host's context-menu message.
window.document.getElementById('toc-hide').dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('x button hides the panel', isHidden(tocEl), 'the x button did not hide it -- see issue #1');
ok('hide persisted', window.localStorage.getItem('mdp.toc') === '0');
listeners[0]({ data: { kind: 'toc' } });
ok('host toc message shows it again', !isHidden(tocEl));

console.log('\n== find in page ==');
const findBar = window.document.getElementById('find');
const findInput = window.document.getElementById('find-input');
const findCount = window.document.getElementById('find-count');
const ctrlF = () => window.document.dispatchEvent(
  new window.KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true, cancelable: true }));

c = render('# Widgets\n\nThe widget counts widgets. A WIDGET is not a gadget.\n');
ok('find bar hidden until asked for', isHidden(findBar),
   'the bar renders on every document -- see issue #1');

ctrlF();
ok('Ctrl+F opens the find bar', !isHidden(findBar));

// runFind is debounced behind the input event; call the same path directly by
// typing and firing input, then waiting past the debounce.
findInput.value = 'widget';
findInput.dispatchEvent(new window.Event('input', { bubbles: true }));
await new Promise(r => setTimeout(r, 200));

// Four matches: the heading, then the three in the paragraph.
let marks = c.querySelectorAll('mark.mdp-find');
ok('all case-insensitive matches highlighted', marks.length === 4, `got ${marks.length}`);
ok('first match is current', marks[0].classList.contains('mdp-find-current'));
ok('counter reads 1/4', findCount.textContent === '1/4', `got ${findCount.textContent}`);
ok('highlight preserves the original casing', marks[3].textContent === 'WIDGET',
   `got ${JSON.stringify(marks[3].textContent)}`);
ok('match inside a heading is found too', marks[0].closest('h1') !== null);
ok('document text is unchanged by highlighting',
   c.textContent.includes('The widget counts widgets. A WIDGET is not a gadget.'));

// Enter cycles forward, Shift+Enter back, both wrapping.
const enter = (shift) => findInput.dispatchEvent(new window.KeyboardEvent('keydown',
  { key: 'Enter', shiftKey: shift, bubbles: true, cancelable: true }));
enter(false);
ok('Enter advances to match 2', findCount.textContent === '2/4', `got ${findCount.textContent}`);
enter(false); enter(false); enter(false);
ok('Enter wraps past the last match', findCount.textContent === '1/4', `got ${findCount.textContent}`);
enter(true);
ok('Shift+Enter wraps backwards', findCount.textContent === '4/4', `got ${findCount.textContent}`);
ok('exactly one current match at a time',
   c.querySelectorAll('mark.mdp-find-current').length === 1);

findInput.value = 'nothingmatchesthis';
findInput.dispatchEvent(new window.Event('input', { bubbles: true }));
await new Promise(r => setTimeout(r, 200));
ok('no matches reports 0/0', findCount.textContent === '0/0', `got ${findCount.textContent}`);
ok('no stray marks left behind', c.querySelectorAll('mark.mdp-find').length === 0);

// Esc closes and removes every highlight, leaving the document text intact.
findInput.value = 'widget';
findInput.dispatchEvent(new window.Event('input', { bubbles: true }));
await new Promise(r => setTimeout(r, 200));
ok('re-search finds matches again', c.querySelectorAll('mark.mdp-find').length === 4);

window.document.dispatchEvent(new window.KeyboardEvent('keydown',
  { key: 'Escape', bubbles: true, cancelable: true }));
ok('Esc hides the find bar', findBar.hidden === true);
ok('Esc clears every highlight', c.querySelectorAll('mark.mdp-find').length === 0);
ok('text survives highlight removal intact',
   c.textContent.includes('The widget counts widgets. A WIDGET is not a gadget.'));

// Rendered diagrams inject <style> blocks whose selectors mention the diagram
// engine; those text nodes are invisible and must not pollute the match count.
c = render('# Diagram\n\nThe mermaid diagram below.\n');
c.insertAdjacentHTML('beforeend',
  '<div class="mermaid-block"><svg><style>#mermaid-1 .node{fill:#fff}' +
  '#mermaid-1 .edge{stroke:#000}</style><text>mermaid label</text></svg></div>');
listeners[0]({ data: { kind: 'find' } });
findInput.value = 'mermaid';
findInput.dispatchEvent(new window.Event('input', { bubbles: true }));
await new Promise(r => setTimeout(r, 200));
ok('style internals still excluded, diagram label now counted',
   findCount.textContent === '1/2',
   `got ${findCount.textContent} — the <style> block holds two more "mermaid" ` +
   `strings that must not count, while the visible <text> label must`);
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

// The host's context-menu entry opens (never toggles) the bar.
c = render('# Widgets\n\nThe widget counts widgets. A WIDGET is not a gadget.\n');
listeners[0]({ data: { kind: 'find' } });
ok('host find message opens the bar', findBar.hidden === false);
listeners[0]({ data: { kind: 'find' } });
ok('a second host find message leaves it open', findBar.hidden === false);
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

console.log('\n== find bar: closing, reopening, moving ==');
// Regression cover for issue #1: every one of these passed against the .hidden
// property while the bar stayed on screen. They assert computed display.
c = render('# Widgets\n\nThe widget counts widgets.\n');
const click = (id) => window.document.getElementById(id)
  .dispatchEvent(new window.MouseEvent('click', { bubbles: true }));

ctrlF();
ok('bar open before closing', !isHidden(findBar));
click('find-close');
ok('close button hides the bar', isHidden(findBar), 'the x did nothing -- see issue #1');
ok('closing clears the highlights', c.querySelectorAll('mark.mdp-find').length === 0);

ctrlF();
ok('Ctrl+F reopens a hidden bar', !isHidden(findBar));
click('find-close');
listeners[0]({ data: { kind: 'find' } });
ok('the context menu reopens a hidden bar', !isHidden(findBar));

// Undragged, the document reserves a strip so the bar cannot cover the title.
ok('document reserves space for the bar',
   window.document.body.classList.contains('mdp-find-reserved'));

// Dragging by the grip moves the bar and releases that strip.
const grip = window.document.getElementById('find-grip');
const pointer = (type, x, y) => window.dispatchEvent(
  new window.MouseEvent(type, { clientX: x, clientY: y, bubbles: true }));
grip.dispatchEvent(new window.MouseEvent('pointerdown',
  { clientX: 20, clientY: 20, button: 0, bubbles: true, cancelable: true }));
pointer('pointermove', 320, 260);
pointer('pointerup', 320, 260);

ok('drag moved the bar', findBar.style.left !== '' && findBar.style.top !== '',
   `left=${findBar.style.left} top=${findBar.style.top}`);
ok('a moved bar releases the reserved strip',
   !window.document.body.classList.contains('mdp-find-reserved'));
ok('position persisted for the next document',
   JSON.parse(window.localStorage.getItem('mdp.findPos') || 'null')?.left > 0);

click('find-close');
ok('a moved bar still closes', isHidden(findBar));

console.log('\n== find options: case, whole word, regex ==');
// All three are independent and combine; regex + whole word wraps the pattern
// as \\b(?:...)\\b, which is why a pattern edged with non-word characters
// legitimately finds nothing.
const setOpt = (id, on) => {
  const box = window.document.getElementById(id);
  box.checked = on;
  box.dispatchEvent(new window.Event('change', { bubbles: true }));
};
const search = async (text) => {
  findInput.value = text;
  findInput.dispatchEvent(new window.Event('input', { bubbles: true }));
  await new Promise(r => setTimeout(r, 200));
  return findCount.textContent;
};

c = render('# Widget\n\nwidget WIDGET widgets Widget-maker.\n');
ctrlF();

ok('case-insensitive by default', (await search('widget')) === '1/5',
   `got ${findCount.textContent}`);

setOpt('find-case', true);
ok('match case narrows to exact casing', findCount.textContent === '1/2',
   `got ${findCount.textContent}`);
ok('match case persisted',
   JSON.parse(window.localStorage.getItem('mdp.findOpts')).matchCase === true);

setOpt('find-case', false);
setOpt('find-word', true);
ok('whole word drops the plural but keeps hyphenated',
   findCount.textContent === '1/4', `got ${findCount.textContent}`);

setOpt('find-word', false);
setOpt('find-regex', true);
ok('regex metacharacters are live', (await search('W.dget')) === '1/5',
   `got ${findCount.textContent}`);
setOpt('find-word', true);
ok('regex + whole word combine', (await search('w.dgets')) === '1/1',
   `got ${findCount.textContent}`);
setOpt('find-word', false);

// A half-typed pattern must not throw or silently read as no matches.
await search('widget(');
ok('invalid pattern reported, not thrown', findCount.textContent === 'bad pattern',
   `got ${findCount.textContent}`);
ok('invalid pattern flagged on the bar', findBar.classList.contains('mdp-find-invalid'));

setOpt('find-regex', false);
await search('widget(');
ok('the same text is a literal search once regex is off',
   findCount.textContent === '0/0', `got ${findCount.textContent}`);
ok('invalid flag cleared', !findBar.classList.contains('mdp-find-invalid'));

// The gear panel is a panel like any other: it must actually hide.
const optsPanel = window.document.getElementById('find-opts');
ok('options panel hidden until asked for', isHidden(optsPanel));
click('find-options');
ok('gear opens the options panel', !isHidden(optsPanel));
click('find-options');
ok('gear closes it again', isHidden(optsPanel));
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

console.log('\n== find inside a rendered diagram ==');
// The reported bug: text drawn inside a mermaid SVG was invisible to search.
// It cannot be wrapped in <mark> (an HTML element inside <svg> does not
// render), so it is boxed by an overlay instead.
c = render('# Architecture\n\nThe Domain layer is inside.\n');
c.insertAdjacentHTML('beforeend',
  '<div class="mermaid-block"><svg data-processed="true">' +
  '<style>#mermaid-2 .node{fill:#fff}</style>' +
  '<g><text>Domain</text></g><g><text>Application</text></g></svg></div>');
listeners[0]({ data: { kind: 'find' } });
ok('diagram label is found', (await search('Domain')) === '1/2',
   `got ${findCount.textContent} — the prose match plus the diagram label`);

const overlays = window.document.querySelectorAll('.mdp-find-overlay');
ok('the diagram match is boxed by an overlay', overlays.length === 1,
   `got ${overlays.length}`);
ok('the overlay counts as a match', overlays[0].classList.contains('mdp-find'));

enter(false);
ok('arrows cycle into the diagram match',
   overlays[0].classList.contains('mdp-find-current'), `counter ${findCount.textContent}`);

await search('Application');
ok('a label with no prose twin is still found', findCount.textContent === '1/1',
   `got ${findCount.textContent}`);

window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
ok('closing removes the diagram overlays',
   window.document.querySelectorAll('.mdp-find-overlay').length === 0);

console.log('\n== render completion reporting ==');
c = render('# done\n');
await new Promise(r => setTimeout(r, 60));
let done = posted.find(m => m.kind === 'rendered');
ok('rendered message posted', !!done, JSON.stringify(posted));
ok('echoes the HOST token, not a page-local counter', done?.token === 1,
   `got ${done?.token} -- the host discards any reply whose token it does not recognise`);
ok('reports mermaid/math as unused', done?.usedMermaid === false && done?.usedMath === false);

// The host mints monotonically increasing tokens; every one must come back
// unchanged, or CompleteRender drops the reply and the preview times out.
for (const hostToken of [2, 7, 99, 100000]) {
  posted.length = 0;
  listeners[0]({ data: {
    kind: 'render', token: hostToken, markdown: '# t', theme: 'light',
    docBase: 'https://doc.mdpreview.invalid/notes/',
    settings: { mermaid: false, math: false, taskLists: true, highlight: true, linkify: true },
  }});
  await new Promise(r => setTimeout(r, 30));
  const reply = posted.find(m => m.kind === 'rendered');
  ok(`host token ${hostToken} echoed verbatim`, reply?.token === hostToken, `got ${reply?.token}`);
}

console.log('\n== theme switching ==');
render('# t\n', {}, 'dark');
const media = id => window.document.getElementById(id).media;
ok('dark body stylesheet enabled',  media('css-body-dark') === 'all');
ok('light body stylesheet disabled', media('css-body-light') === 'not all');
ok('dark code stylesheet enabled',  media('css-code-dark') === 'all');
render('# t\n', {}, 'light');
ok('flips back to light', media('css-body-light') === 'all' && media('css-body-dark') === 'not all');

console.log('\n== lazy asset loading (a 2.1 MB / 3.5 MB decision per document) ==');
async function assetsFor(md, settings) {
  requestedAssets.length = 0;
  posted.length = 0;
  render(md, settings);
  await new Promise(r => setTimeout(r, 80));
  return { assets: [...requestedAssets], reply: posted.find(x => x.kind === 'rendered') };
}

let r = await assetsFor('$$x^2$$\n', { math: true });
ok('$$...$$ requests the MathJax config', r.assets.some(a => a.endsWith('mathjax-config.js')), r.assets.join(', '));
ok('config is requested first (it must run before the engine)',
   r.assets.findIndex(a => a.endsWith('mathjax-config.js')) === 0, r.assets.join(', '));
ok('engine NOT requested when the config fails to load',
   !r.assets.some(a => a.endsWith('tex-mml-svg.js')),
   'chaining the engine behind a failed config would run MathJax unconfigured');


r = await assetsFor('\\[x^2\\]\n', { math: true });
ok('\\[...\\] requests MathJax', r.assets.some(a => a.endsWith('mathjax-config.js')));

r = await assetsFor('\\begin{align}x\\end{align}\n', { math: true });
ok('\\begin{align} requests MathJax', r.assets.some(a => a.endsWith('mathjax-config.js')));

r = await assetsFor('costs $5 and $10\n', { math: true });
ok('prose with prices does NOT request MathJax', r.assets.length === 0, r.assets.join(', '));

r = await assetsFor('a $x$ b\n', { math: true, singleDollarMath: true });
ok('single-dollar math requests MathJax when opted in', r.assets.some(a => a.endsWith('mathjax-config.js')));

r = await assetsFor('a $x$ b\n', { math: true, singleDollarMath: false });
ok('single-dollar math ignored by default', r.assets.length === 0, r.assets.join(', '));

r = await assetsFor('# no diagrams\n', { mermaid: true });
ok('document without a mermaid fence does NOT request mermaid', r.assets.length === 0, r.assets.join(', '));

r = await assetsFor('```mermaid\ngraph TD;A-->B;\n```\n', { mermaid: true });
ok('mermaid fence requests mermaid', r.assets.some(a => a.endsWith('mermaid.min.js')), r.assets.join(', '));
ok('mermaid source shown as a pre while loading',
   !!window.document.querySelector('.mermaid-block > pre.mermaid'));

console.log('\n== a failed load is retried; a succeeded load is cached ==');
// loadAsset removes a failed entry from its cache so a later document can retry,
// but keeps a successful one for the life of the process. That asymmetry is what
// makes the second diagram-bearing document fast, so assert both halves.
r = await assetsFor('$$a^2$$\n', { math: true });
ok('a previously failed asset is requested again',
   r.assets.some(a => a.endsWith('mathjax-config.js')), r.assets.join(', '));

succeedAssets.add('mathjax-config.js');
r = await assetsFor('$$b^2$$\n', { math: true });
ok('engine requested once the config succeeds',
   r.assets.some(a => a.endsWith('tex-mml-svg.js')), r.assets.join(', '));
ok('config strictly precedes the engine',
   r.assets.findIndex(a => a.endsWith('mathjax-config.js')) < r.assets.findIndex(a => a.endsWith('tex-mml-svg.js')));
ok('singleDollarMath flag published before the config runs', window.__mdpMathSingleDollar === false);

r = await assetsFor('$$c^2$$\n', { math: true });
ok('a succeeded asset is NOT re-requested',
   !r.assets.some(a => a.endsWith('mathjax-config.js')), r.assets.join(', '));
succeedAssets.delete('mathjax-config.js');

console.log('\n== a failed lazy load still completes the render ==');
r = await assetsFor('```mermaid\ngraph TD;A-->B;\n```\n', { mermaid: true });
ok('render still reports completion', !!r.reply, 'the host would otherwise wait out its 30 s timeout');
ok('failure surfaced as a warning, not a hard failure',
   (r.reply?.warnings ?? []).some(w => w.startsWith('Mermaid')), JSON.stringify(r.reply?.warnings));
ok('document body still rendered', window.document.getElementById('content').children.length > 0);

console.log('\n== truncation notice passthrough ==');
c = render('> **Preview truncated.** Showing the first part of a 12.0 MB file.\n\n# Body\n');
ok('host-injected notice renders as a blockquote', !!c.querySelector('blockquote strong'));

console.log(`\n${'='.repeat(46)}\n  ${pass} passed, ${fail} failed\n${'='.repeat(46)}`);
process.exit(fail === 0 ? 0 : 1);
