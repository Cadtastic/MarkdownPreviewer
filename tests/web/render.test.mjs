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

function render(markdown, settings = {}, theme = 'light', envelope = {}) {
  posted.length = 0;
  listeners[0]({
    data: {
      kind: 'render',
      token: 1,
      markdown,
      theme,
      docBase: 'https://doc.mdpreview.invalid/notes/',
      // One generation by default, so a bare render() reads as "the same
      // document again" and does not clear the find bar out from under a test.
      // Pass a different one through `envelope` to model a new selection.
      documentGeneration: 1,
      documentName: 'notes.md',
      taskEditable: true,
      trusted: false,
      trustable: true,
      ...envelope,
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

console.log('\n== remote images follow the permission, not the cache ==');
// The host refuses these requests too — that is the boundary — but the page
// withholds the URL so no request is issued at all. Without that, withdrawing
// trust left already-fetched images on screen: the <img> was recreated with the
// same src and answered from the browser cache, so the host's gate was never
// consulted.
const remoteDoc = '![alt](https://cdn.example.com/pic.png)\n';

c = render(remoteDoc);
ok('untrusted document emits no remote <img> at all', c.querySelector('img') === null);
ok('the alt text survives as a placeholder',
   c.querySelector('span.mdp-broken')?.textContent === 'alt');
ok('and it reads as blocked, not as broken', !!c.querySelector('span.mdp-blocked'));
ok('the placeholder says how to undo it',
   (c.querySelector('span.mdp-blocked')?.getAttribute('title') ?? '').includes('Trust this document'));

c = render(remoteDoc, {}, 'light', { trusted: true });
ok('a trusted document emits the real src',
   c.querySelector('img')?.getAttribute('src') === 'https://cdn.example.com/pic.png');

// The regression the user hit: withdrawing has to take effect on this render,
// not on some later one.
c = render(remoteDoc, {}, 'light', { trusted: false });
ok('withdrawing trust drops the image on the very next render',
   c.querySelector('img') === null && !!c.querySelector('span.mdp-blocked'));

// The standing preference is a separate door to the same room, and the page has
// to honour it or setting AllowRemoteImages would appear to do nothing.
c = render(remoteDoc, { allowRemoteImages: true });
ok('AllowRemoteImages alone is enough, without trusting the document',
   c.querySelector('img')?.getAttribute('src') === 'https://cdn.example.com/pic.png');

// A path that cannot be resolved is not the same as one held back, and must not
// claim the reader can fix it from the toolbar. (markdown-it refuses file: and
// javascript: itself, before our rule runs — ms-appx: reaches us.)
c = render('![missing](ms-appx:///images/logo.png)\n');
ok('an unusable scheme is broken, not blocked',
   !!c.querySelector('span.mdp-broken') && !c.querySelector('span.mdp-blocked'),
   c.innerHTML);

// Raw HTML goes through the sanitiser rather than the image rule; it is the
// same funnel underneath, so it must reach the same answer.
c = render('<img src="https://cdn.example.com/raw.png" alt="raw">\n', { allowRawHtml: true });
ok('raw-HTML remote images are stripped too when untrusted',
   c.querySelector('img')?.hasAttribute('src') !== true);
c = render('<img src="https://cdn.example.com/raw.png" alt="raw">\n',
           { allowRawHtml: true }, 'light', { trusted: true });
ok('and kept when the document is trusted',
   c.querySelector('img')?.getAttribute('src') === 'https://cdn.example.com/raw.png');

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

console.log('\n== where a local link opens ==');
// Two modes: reveal the file in Explorer (default) or open it in the default
// app. The page only declares the reader's choice per click; the host decides
// what happens to the path.
const linkModeToggle = window.document.getElementById('link-mode');
ok('defaults to revealing in Explorer', linkModeToggle.getAttribute('aria-pressed') === 'false');

posted.length = 0;
docAnchor.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
ok('a link click declares navigate mode',
   posted.some(m => m.kind === 'openDocument' && m.mode === 'navigate'),
   JSON.stringify(posted));

linkModeToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('toggling flips to app mode', linkModeToggle.getAttribute('aria-pressed') === 'true');
ok('the choice is persisted', window.localStorage.getItem('mdp.linkMode') === 'app');
ok('the tooltip states the current behaviour',
   (linkModeToggle.getAttribute('data-mdp-tip') || '').startsWith('Links open in the default app'),
   linkModeToggle.getAttribute('data-mdp-tip'));

posted.length = 0;
docAnchor.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
ok('a link click now declares app mode',
   posted.some(m => m.kind === 'openDocument' && m.mode === 'app'),
   JSON.stringify(posted));

linkModeToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('toggling back returns to navigate', linkModeToggle.getAttribute('aria-pressed') === 'false' &&
   window.localStorage.getItem('mdp.linkMode') === 'navigate');

posted.length = 0;
docAnchor.dispatchEvent(new window.MouseEvent('click', { bubbles: true, cancelable: true }));
ok('click routed to the host as openDocument',
   posted.some(m => m.kind === 'openDocument' && m.url === 'https://doc.mdpreview.invalid/notes/docs/guide.md'),
   JSON.stringify(posted));
ok('doc link NOT sent as openExternal', !posted.some(m => m.kind === 'openExternal'));

console.log('\n== editable task checkboxes ==');
// Off by default, per document rather than per user, and a checkbox click is a
// write — so the page has to be exact about which source line it names.
const taskEditToggle = window.document.getElementById('task-edit');
const tipOf = (el) => el.getAttribute('data-mdp-tip') || '';
const taskDoc = '# Todo\n\n- [ ] first\n- [x] second\n';

c = render(taskDoc);
ok('checkboxes are read-only by default',
   [...c.querySelectorAll('li.mdp-task input')].every(b => b.disabled));
ok('the toggle starts unpressed', taskEditToggle.getAttribute('aria-pressed') === 'false');
ok('the tooltip discloses the save behaviour',
   tipOf(taskEditToggle).includes('save to the file'), tipOf(taskEditToggle));
ok('the tooltip is themed, not a native title',
   !taskEditToggle.hasAttribute('title'));
ok('the tooltip breaks its detail onto another line',
   tipOf(taskEditToggle).includes('\n'), JSON.stringify(tipOf(taskEditToggle)));

posted.length = 0;
c.querySelector('li.mdp-task input').click();
ok('a click while read-only writes nothing',
   !posted.some(m => m.kind === 'toggleTask'), JSON.stringify(posted));

posted.length = 0;
taskEditToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('toggling makes the boxes editable',
   [...c.querySelectorAll('li.mdp-task input')].every(b => !b.disabled));
ok('the choice goes to the host, which owns it per document',
   posted.some(m => m.kind === 'setTaskEdit' && m.enabled === true), JSON.stringify(posted));
ok('nothing is written to shared browser storage',
   window.localStorage.getItem('mdp.editTasks') === null);
ok('the tooltip now reads as already-on',
   tipOf(taskEditToggle).startsWith('Checkboxes are editable'), tipOf(taskEditToggle));

// Line numbers are what the host writes against, so they are the thing to pin.
// Each render carries taskEditOn, because the host is the authority on it.
const editable = { taskEditOn: true };

posted.length = 0;
c = render(taskDoc, {}, 'light', editable);
const taskBoxes = c.querySelectorAll('li.mdp-task input');
taskBoxes[0].click();
ok('checking names its own source line and state',
   posted.some(m => m.kind === 'toggleTask' && m.line === 2 && m.checked === true),
   JSON.stringify(posted));

posted.length = 0;
taskBoxes[1].click();
ok('unchecking names the second line',
   posted.some(m => m.kind === 'toggleTask' && m.line === 3 && m.checked === false),
   JSON.stringify(posted));

// Front matter is stripped before parsing, so its lines have to be added back
// or every edit would land that many lines too early in the file.
posted.length = 0;
c = render('---\ntitle: T\ntags: [a]\n---\n\n- [ ] after front matter\n', {}, 'light', editable);
c.querySelector('li.mdp-task input').click();
ok('front-matter lines are counted back in',
   posted.some(m => m.kind === 'toggleTask' && m.line === 5),
   JSON.stringify(posted));

// A task inside a fenced block is not a checkbox, so it must not shift the
// line of the real one below it.
posted.length = 0;
c = render('```md\n- [ ] decoy\n```\n\n- [ ] real\n', {}, 'light', editable);
ok('the decoy in the fence is not a checkbox', c.querySelectorAll('li.mdp-task').length === 1);
c.querySelector('li.mdp-task input').click();
ok('the real task still names its own line',
   posted.some(m => m.kind === 'toggleTask' && m.line === 4), JSON.stringify(posted));

// Both hazards at once, and the exact document the C# editor is tested against
// (Toggle_AgreesWithTheLineNumbersThePageSends). The two suites share this
// fixture on purpose: if either side's line arithmetic drifts, one of them
// fails rather than the pair silently disagreeing in production.
posted.length = 0;
c = render('---\ntitle: Demo\n---\n\n# Tasks\n\n```md\n- [ ] decoy in a fence\n```\n\n- [ ] alpha\n- [x] beta\n',
           {}, 'light', editable);
ok('front matter and a fence together still land on the right line',
   posted.length === 0 && c.querySelectorAll('li.mdp-task').length === 2);
c.querySelectorAll('li.mdp-task input')[0].click();
ok('the shared fixture names line 10',
   posted.some(m => m.kind === 'toggleTask' && m.line === 10 && m.checked === true),
   JSON.stringify(posted));

// Editing belongs to the document, not the reader: a file the host says is not
// enabled comes back read-only however the last one was left.
c = render(taskDoc);
ok('the next document does not inherit the choice',
   taskEditToggle.getAttribute('aria-pressed') === 'false' &&
   [...c.querySelectorAll('li.mdp-task input')].every(b => b.disabled));
c = render(taskDoc, {}, 'light', editable);
ok('and a document the host says is enabled comes back editable',
   taskEditToggle.getAttribute('aria-pressed') === 'true' &&
   [...c.querySelectorAll('li.mdp-task input')].every(b => !b.disabled));

// A document with no file behind it cannot be written to.
c = render(taskDoc, {}, 'light', { taskEditable: false });
ok('an unwritable document disables the toggle', taskEditToggle.disabled === true);
ok('and says why', tipOf(taskEditToggle).includes('no file to save to'), tipOf(taskEditToggle));
ok('its checkboxes stay read-only',
   [...c.querySelectorAll('li.mdp-task input')].every(b => b.disabled));

posted.length = 0;
c.querySelector('li.mdp-task input').click();
ok('and a click on one writes nothing', !posted.some(m => m.kind === 'toggleTask'));

// Nothing to edit is its own reason, and a different one to say.
c = render('# Just prose\n\nNo task list here.\n', {}, 'light', editable);
ok('a document with no task list disables the toggle', taskEditToggle.disabled === true);
ok('and says that is why', tipOf(taskEditToggle).includes('no task list'), tipOf(taskEditToggle));

console.log('\n== contents rail ==');
const tocEl = window.document.getElementById('toc');
const findBarEl = window.document.getElementById('toolbar');
const tocToggle = window.document.getElementById('toc-toggle');
const openBar = () => window.document.dispatchEvent(
  new window.KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true, cancelable: true }));

window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
c = render('# One\n\n## Two\n\n## Three\n');
ok('a new document brings the toolbar back after Esc', !isHidden(findBarEl));
ok('rail comes up with the toolbar on a heading-ful document', !isHidden(tocEl));
openBar();
ok('Ctrl+F on an open toolbar leaves the rail alone', !isHidden(tocEl));
ok('rail lists every heading', window.document.querySelectorAll('#toc-list a').length === 3,
   `got ${window.document.querySelectorAll('#toc-list a').length}`);
ok('rail entries link to the heading anchors',
   window.document.querySelector('#toc-list a')?.getAttribute('href') === '#one');
ok('Contents button reads as expanded', tocToggle.getAttribute('aria-expanded') === 'true');

// The toolbar's Contents button is the rail's only control.
tocToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('Contents button hides the rail', isHidden(tocEl));
ok('hide persisted', window.localStorage.getItem('mdp.toc') === '0');
tocToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('Contents button brings it back', !isHidden(tocEl));

c = render('plain text, no headings\n');
ok('rail hidden for a document without headings', isHidden(tocEl));
ok('Contents button disabled without headings', tocToggle.disabled === true);

c = render('# One\n\n## Two\n');
ok('rail returns for the next heading-ful document', !isHidden(tocEl));
ok('Contents button re-enabled', tocToggle.disabled === false);

// The host's context-menu entry opens the toolbar along with the rail.
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
ok('closing the toolbar takes the rail with it', isHidden(tocEl));
listeners[0]({ data: { kind: 'toc' } });
ok('host toc message reopens toolbar and rail',
   !isHidden(window.document.getElementById('toolbar')) && !isHidden(tocEl));
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

console.log('\n== find in page ==');
const findBar = window.document.getElementById('toolbar');
const findInput = window.document.getElementById('find-input');
const findCount = window.document.getElementById('find-count');
const ctrlF = () => window.document.dispatchEvent(
  new window.KeyboardEvent('keydown', { key: 'f', ctrlKey: true, bubbles: true, cancelable: true }));

c = render('# Widgets\n\nThe widget counts widgets. A WIDGET is not a gadget.\n');
ok('toolbar visible by default', !isHidden(findBar));

ctrlF();
ok('Ctrl+F leaves an already-open toolbar open', !isHidden(findBar));
ok('the document is pushed below the toolbar',
   window.document.documentElement.style.getPropertyValue('--mdp-bar-offset') !== '' &&
   window.document.documentElement.style.getPropertyValue('--mdp-bar-offset') !== '0px');

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

console.log('\n== toolbar: closing and reopening ==');
// Regression cover for issue #1: these once passed against the .hidden
// property while the chrome stayed on screen. They assert computed display.
c = render('# Widgets\n\nThe widget counts widgets.\n');
const click = (id) => window.document.getElementById(id)
  .dispatchEvent(new window.MouseEvent('click', { bubbles: true }));

ctrlF();
ok('toolbar open before closing', !isHidden(findBar));
click('toolbar-close');
ok('close button hides the toolbar', isHidden(findBar));
ok('closing clears the highlights', c.querySelectorAll('mark.mdp-find').length === 0);
ok('closing releases the document strip',
   window.document.documentElement.style.getPropertyValue('--mdp-bar-offset') === '0px');

ctrlF();
ok('Ctrl+F reopens a closed toolbar', !isHidden(findBar));
click('toolbar-close');
listeners[0]({ data: { kind: 'find' } });
ok('the context menu reopens a closed toolbar', !isHidden(findBar));
click('toolbar-close');

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

// The options row is part of the toolbar, and it remembers being open.
const optsPanel = window.document.getElementById('find-opts');
ok('options row hidden until asked for', isHidden(optsPanel));
click('find-options');
ok('gear opens the options row', !isHidden(optsPanel));
ok('row open state persisted', window.localStorage.getItem('mdp.optsRow') === '1');
const offsetWithRow = window.document.documentElement.style.getPropertyValue('--mdp-bar-offset');
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
ctrlF();
ok('reopened toolbar restores the open row', !isHidden(optsPanel));
click('find-options');
ok('gear closes the row again', isHidden(optsPanel));
ok('row closed state persisted', window.localStorage.getItem('mdp.optsRow') === '0');
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
void offsetWithRow;

console.log('\n== the cycle buttons only exist when there is somewhere to go ==');
// Two dead arrows in an empty search field are clutter; with one match there is
// no "next" to reach either.
const findCycle = window.document.getElementById('find-cycle');
c = render('# Notes\n\nExactly one widget lives here.\n');
ctrlF();
ok('hidden before anything is typed', isHidden(findCycle));

await search('nothing-matches-this');
ok('hidden when the search finds nothing', isHidden(findCycle));

await search('widget');
ok('hidden on a single match', isHidden(findCycle), findCount.textContent);

c = render('# Widgets\n\nThe widget counts widgets.\n');
await search('widget');
ok('shown once there are two or more', !isHidden(findCycle), findCount.textContent);

await search('');
ok('hidden again when the query is cleared', isHidden(findCycle));

// They live inside the field now, not out in the toolbar row.
ok('the cycle pair sits inside the search field',
   window.document.querySelector('.mdp-find-field .mdp-find-cycle') !== null);
ok('the match count sits inside it too',
   window.document.querySelector('.mdp-find-field .mdp-find-count') !== null);
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

console.log('\n== expand preview width ==');
const expandToggle = window.document.getElementById('expand-view');
c = render('# Wide\n\nBody text.\n');
ok('starts at the centred reading width', !c.classList.contains('mdp-expanded'));
ok('and reads as unpressed', expandToggle.getAttribute('aria-pressed') === 'false');
ok('the tooltip offers to expand',
   (expandToggle.getAttribute('data-mdp-tip') || '').startsWith('Expand preview width'),
   expandToggle.getAttribute('data-mdp-tip'));

expandToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('expanding drops the width cap', c.classList.contains('mdp-expanded'));
ok('and reads as pressed', expandToggle.getAttribute('aria-pressed') === 'true');
ok('the tooltip now offers the way back',
   (expandToggle.getAttribute('data-mdp-tip') || '').startsWith('Restore preview width'),
   expandToggle.getAttribute('data-mdp-tip'));
ok('the choice is persisted', window.localStorage.getItem('mdp.expanded') === '1');

// The cap has one definition, in the stylesheet; collapsing restores it by
// dropping the class rather than by writing a width back.
expandToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
ok('collapsing restores the previous width', !c.classList.contains('mdp-expanded'));
ok('with the stylesheet cap intact',
   window.getComputedStyle(c).maxWidth === '980px', window.getComputedStyle(c).maxWidth);
ok('and the choice persisted', window.localStorage.getItem('mdp.expanded') === '0');

// It survives a render, because it belongs to the reader rather than the file.
expandToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));
c = render('# Another\n\nBody.\n', {}, 'light', { documentGeneration: 99 });
ok('expansion holds across documents', c.classList.contains('mdp-expanded'));
expandToggle.dispatchEvent(new window.MouseEvent('click', { bubbles: true }));

console.log('\n== themed tooltips ==');
// Native titles cannot take the theme, cannot break where we want them to, and
// appear on the OS's own delay. Every toolbar control uses the themed one.
const tipEl = window.document.getElementById('mdp-tip');
ok('the tooltip element exists and starts hidden', !!tipEl && isHidden(tipEl));
ok('no toolbar control still carries a native title',
   window.document.querySelectorAll('#toolbar [title]').length === 0,
   [...window.document.querySelectorAll('#toolbar [title]')].map(e => e.id || e.tagName).join(','));
ok('every tipped control has text to show',
   [...window.document.querySelectorAll('#toolbar [data-mdp-tip]')]
     .every(e => (e.getAttribute('data-mdp-tip') || '').length > 0));

// Focus is a first-class trigger, not just hover.
window.document.getElementById('find-options').dispatchEvent(
  new window.FocusEvent('focusin', { bubbles: true }));
ok('focus shows the tooltip', !isHidden(tipEl));
ok('the label is its own line',
   tipEl.querySelector('b')?.textContent === 'Options',
   tipEl.textContent);

window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
ok('Esc dismisses it', isHidden(tipEl));

// A two-line tip splits into label and detail.
window.document.getElementById('toolbar-close').dispatchEvent(
  new window.FocusEvent('focusin', { bubbles: true }));
ok('the detail goes in its own element',
   tipEl.querySelector('b')?.textContent === 'Close the toolbar' &&
   tipEl.querySelector('span')?.textContent === 'Esc',
   tipEl.textContent);
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

console.log('\n== find inside images: alt, title, extracted SVG text ==');
// An <img>-loaded SVG is a separate document the page cannot see into; the
// HOST extracts its text and posts it back. Alt and title need no host at
// all. Either way the match boxes the whole image.
posted.length = 0;
c = render('# Pics\n\n![layer diagram](images/arch.svg)\n\n![screenshot](images/shot.png \"login screen\")\n');

const textReq = posted.find(m => m.kind === 'imageTextRequest');
ok('page asks the host for SVG text only', !!textReq && textReq.urls.length === 1 &&
   textReq.urls[0] === 'https://doc.mdpreview.invalid/notes/images/arch.svg',
   JSON.stringify(textReq));

ctrlF();
await search('screenshot');
ok('alt text matches as an image box', findCount.textContent === '1/1',
   `got ${findCount.textContent}`);
ok('the box is an image overlay',
   window.document.querySelectorAll('.mdp-find-overlay.mdp-find-image').length === 1);

await search('login');
ok('title text matches too', findCount.textContent === '1/1', `got ${findCount.textContent}`);

await search('Adapters');
ok('nothing before the host answers', findCount.textContent === '0/0',
   `got ${findCount.textContent}`);

listeners[0]({ data: { kind: 'imageText', images: [
  { url: 'https://doc.mdpreview.invalid/notes/images/arch.svg',
    text: 'Domain Application Adapters Shell' }
] } });
await new Promise(r => setTimeout(r, 30));
ok('host-extracted SVG text turns into a match', findCount.textContent === '1/1',
   `got ${findCount.textContent}`);
ok('the SVG match boxes its image',
   window.document.querySelectorAll('.mdp-find-overlay.mdp-find-image').length === 1);

// One box per image, however often the pattern occurs inside it: 'a' hits
// the SVG image's alt AND its extracted text, several times each.
await search('a');
const imageBoxes = window.document.querySelectorAll('.mdp-find-overlay.mdp-find-image').length;
ok('an image is never boxed twice for one query', imageBoxes === 1, `got ${imageBoxes}`);

window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
ok('closing removes image overlays too',
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
const rootEl = window.document.documentElement;
const media = id => window.document.getElementById(id).media;
const themeSelect = window.document.getElementById('theme-select');

render('# t\n', {}, 'dark');
ok('dark body stylesheet enabled',  media('css-body-dark') === 'all');
ok('light body stylesheet disabled', media('css-body-light') === 'not all');
ok('dark code stylesheet enabled',  media('css-code-dark') === 'all');
ok('System dark maps to the stock GitHub palette',
   rootEl.getAttribute('data-mdp-theme') === 'github-dark');
render('# t\n', {}, 'light');
ok('flips back to light', media('css-body-light') === 'all' && media('css-body-dark') === 'not all');
ok('System light maps to the stock GitHub palette',
   rootEl.getAttribute('data-mdp-theme') === 'github-light');

// Named palettes: fixed lightness, host signal stops mattering.
themeSelect.value = 'harbor';
themeSelect.dispatchEvent(new window.Event('change', { bubbles: true }));
ok('named dark palette applies its attribute', rootEl.getAttribute('data-mdp-theme') === 'harbor');
ok('named dark palette forces the dark sheets', media('css-body-dark') === 'all');
ok('theme choice persisted', window.localStorage.getItem('mdp.theme') === 'harbor');
ok('colour-scheme follows the palette', rootEl.style.colorScheme === 'dark');

render('# t\n', {}, 'light');
ok('host light signal does not override a named dark palette',
   rootEl.getAttribute('data-mdp-theme') === 'harbor' && media('css-body-dark') === 'all');

themeSelect.value = 'paper';
themeSelect.dispatchEvent(new window.Event('change', { bubbles: true }));
ok('switching to a light palette flips the sheets',
   rootEl.getAttribute('data-mdp-theme') === 'paper' && media('css-body-light') === 'all');

themeSelect.value = 'system';
themeSelect.dispatchEvent(new window.Event('change', { bubbles: true }));
render('# t\n', {}, 'dark');
ok('back on System, the host signal rules again',
   rootEl.getAttribute('data-mdp-theme') === 'github-dark');
render('# t\n', {}, 'light');

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

console.log('\n== the search belongs to the document ==');
// Explorer reuses one page for every selection, so a query typed against one
// file used to follow the reader to the next and report a count for a document
// they had already left.
c = render('# Widgets\n\nThe widget counts widgets.\n');
ctrlF();
ok('a query finds its matches', (await search('widget')) === '1/3', findCount.textContent);

// Same document, drawn again — a theme flip or a trust change. The query has to
// survive, or those controls would wipe out a search in progress.
c = render('# Widgets\n\nThe widget counts widgets.\n');
await new Promise(r => setTimeout(r, 50));
ok('a redraw of the same document keeps the query', findInput.value === 'widget');
ok('and re-runs it against the rebuilt DOM',
   c.querySelectorAll('mark.mdp-find').length === 3,
   `${c.querySelectorAll('mark.mdp-find').length} marks`);

// A different file: the query, the count and the colour all belong to the
// document that is gone.
c = render('# Gadgets\n\nGadgets only.\n', {}, 'light', { documentGeneration: 2 });
await new Promise(r => setTimeout(r, 50));
ok('moving to another document clears the query', findInput.value === '');
ok('and the match count', findCount.textContent === '');
ok('and the state colour', !['hit', 'miss', 'invalid']
   .some(n => findBar.classList.contains(`mdp-find-${n}`)));
ok('nothing is highlighted in the new document',
   c.querySelectorAll('mark.mdp-find').length === 0);

// The debounce is the trap: a pending search for the old text must not land in
// the new document a moment after it was cleared.
findInput.value = 'gadgets';
findInput.dispatchEvent(new window.Event('input', { bubbles: true }));
c = render('# Sprockets\n\nSprockets only.\n', {}, 'light', { documentGeneration: 3 });
await new Promise(r => setTimeout(r, 250));
ok('a debounced search does not fire into the next document',
   findInput.value === '' && c.querySelectorAll('mark.mdp-find').length === 0,
   `value=${JSON.stringify(findInput.value)} marks=${c.querySelectorAll('mark.mdp-find').length}`);
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

console.log('\n== toolbar: visible by default, dismissed per document ==');
// The toolbar is the only way to reach search, the contents rail, the theme
// and the trust control, so closing it is scoped to the document on screen.
// It must still genuinely close -- that half is issue #1.
c = render('# Widgets\n\nWidgets everywhere.\n');
ok('a fresh document shows the toolbar', !isHidden(findBar));
click('toolbar-close');
ok('close still hides it', isHidden(findBar));
c = render('# Gadgets\n\nGadgets now.\n');
ok('the next document brings it back', !isHidden(findBar));
ok('closing is NOT remembered as a preference',
   window.localStorage.getItem('mdp.toolbar') === null);

console.log('\n== toolbar geometry ==');
// The close button is the last thing in the row, and the group holding it is
// exactly as wide as the contents rail -- which is what puts the Contents
// button's left edge on the rail's left edge.
const toolbarRow = window.document.querySelector('.mdp-toolbar-row');
ok('close button is the last control in the row',
   [...toolbarRow.querySelectorAll('button')].pop().id === 'toolbar-close');
ok('Contents button leads the right-hand group',
   toolbarRow.querySelector('.mdp-toolbar-right').firstElementChild.id === 'toc-toggle');
// The view controls sit between Contents and the close button, right-aligned,
// rather than trailing the search field.
ok('options and expand sit between Contents and close',
   [...toolbarRow.querySelectorAll('.mdp-toolbar-view button')].map(b => b.id).join(',')
     === 'find-options,expand-view,toolbar-close',
   [...toolbarRow.querySelectorAll('.mdp-toolbar-view button')].map(b => b.id).join(','));
ok('neither of them trails the search field',
   toolbarRow.querySelector('.mdp-find-field ~ button') === null);
// The alignment is only durable if both widths come off the same token, so
// that is what is asserted -- jsdom does not resolve custom properties, and a
// resolved pixel figure would not prove they are tied together anyway.
const railWidth = window.getComputedStyle(tocEl).width;
const groupWidth = window.getComputedStyle(window.document.querySelector('.mdp-toolbar-right')).width;
ok('the rail takes its width from the shared token',
   railWidth === 'var(--mdp-toc-width)', railWidth);
ok('the right-hand group is that same width less the row padding',
   groupWidth === 'calc(var(--mdp-toc-width) - var(--mdp-toolbar-pad))', groupWidth);

// Every glyph is a 24px icon in a button that adds no more than 2px around it.
const icons = [...window.document.querySelectorAll('.mdp-toolbar .mdp-icon')];
ok('the toolbar is drawn with icons, not glyphs', icons.length >= 5, `${icons.length} icons`);
ok('every toolbar icon is 24px square',
   icons.every(i => window.getComputedStyle(i).width === '24px' &&
                    window.getComputedStyle(i).height === '24px'));
ok('icon buttons pad by 2px',
   window.getComputedStyle(window.document.getElementById('find-options')).padding === '2px',
   window.getComputedStyle(window.document.getElementById('find-options')).padding);

console.log('\n== search field state colours ==');
// Green with matches, red without, red for a pattern that will not compile,
// and neutral when there is nothing to say.
ctrlF();
const findState = () => ['hit', 'miss', 'invalid']
  .filter(n => findBar.classList.contains(`mdp-find-${n}`)).join(',') || 'neutral';

c = render('# Widgets\n\nThe widget counts widgets.\n');
ctrlF();
await search('widget');
ok('matches turn the field green', findState() === 'hit', findState());
await search('nothing-here');
ok('no matches turn it red', findState() === 'miss', findState());
await search('');
ok('an empty query is neutral again', findState() === 'neutral', findState());

setOpt('find-regex', true);
await search('widget(');
ok('an uncompilable pattern reads as red, not as a miss',
   findState() === 'invalid', findState());
ok('the counter says why', findCount.textContent === 'bad pattern');
await search('widget');
ok('a valid pattern goes green', findState() === 'hit', findState());
setOpt('find-regex', false);
await search('');
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));

console.log('\n== trust external links ==');
const trustToggle = window.document.getElementById('trust-toggle');
const trustDialog = window.document.getElementById('trust-dialog');

c = render('# Remote\n\n![pixel](https://example.invalid/p.png)\n');
ok('untrusted document reads as not pressed',
   trustToggle.getAttribute('aria-pressed') === 'false');
ok('trust dialog closed to begin with', isHidden(trustDialog));

// Granting asks first, and the question must name what it is about.
click('trust-toggle');
ok('clicking trust opens the dialog', !isHidden(trustDialog));
ok('the dialog names the document',
   window.document.getElementById('trust-dialog-name').textContent === 'notes.md');
ok('nothing is granted merely by asking',
   !posted.some(m => m.kind === 'trustDocument'), JSON.stringify(posted));
ok('the toggle stays off while the dialog is up',
   trustToggle.getAttribute('aria-pressed') === 'false');

// Cancel is the default action, and Esc chooses it.
click('trust-cancel');
ok('cancel closes the dialog', isHidden(trustDialog));
ok('cancel grants nothing', !posted.some(m => m.kind === 'trustDocument'));
ok('cancel leaves the toggle off', trustToggle.getAttribute('aria-pressed') === 'false');

click('trust-toggle');
window.document.dispatchEvent(new window.KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
ok('Esc closes the dialog', isHidden(trustDialog));
ok('Esc grants nothing', !posted.some(m => m.kind === 'trustDocument'));
ok('Esc closed the dialog without also closing the toolbar', !isHidden(findBar));

// Confirming is the only path to a grant.
click('trust-toggle');
click('trust-confirm');
ok('confirming closes the dialog', isHidden(trustDialog));
ok('confirming asks the host to record trust',
   posted.some(m => m.kind === 'trustDocument' && m.trusted === true), JSON.stringify(posted));
ok('the toggle reads as pressed', trustToggle.getAttribute('aria-pressed') === 'true');

// Withdrawing a permission is safe, so it does not ask.
posted.length = 0;
click('trust-toggle');
ok('withdrawing skips the dialog', isHidden(trustDialog));
ok('withdrawing is sent to the host',
   posted.some(m => m.kind === 'trustDocument' && m.trusted === false), JSON.stringify(posted));
ok('the toggle reads as unpressed again',
   trustToggle.getAttribute('aria-pressed') === 'false');

// The host is the authority; the page repaints from what it is told.
c = render('# Remote\n', {}, 'light', { trusted: true });
ok('a trusted render lights the toggle up',
   trustToggle.getAttribute('aria-pressed') === 'true');
c = render('# Remote\n', {}, 'light', { trusted: false });
ok('the next document does not inherit the grant',
   trustToggle.getAttribute('aria-pressed') === 'false');

// An item with no file behind it has nothing to record a grant against.
c = render('# Streamed\n', {}, 'light', { trustable: false, documentName: '' });
ok('an unlocatable item cannot be trusted', trustToggle.disabled === true);
ok('and it says why',
   (trustToggle.getAttribute('data-mdp-tip') || '').includes('no file on disk'),
   trustToggle.getAttribute('data-mdp-tip'));
c = render('# Located\n\n![badge](https://cdn.example.com/b.png)\n', {}, 'light', { trustable: true });
ok('a real file with a remote image can be trusted again', trustToggle.disabled === false);

// Trust is about remote images, so a document without any has nothing to
// grant: the toggle disables itself and says that, not "cannot be trusted".
c = render('# Local only\n\n![local](images/pic.png)\n');
ok('a document with no external image links disables the toggle',
   trustToggle.disabled === true);
ok('and the tooltip says there is nothing to trust',
   (trustToggle.getAttribute('data-mdp-tip') || '').includes('no external image links'),
   trustToggle.getAttribute('data-mdp-tip'));
ok('the tooltip names images, not links in general',
   (trustToggle.getAttribute('data-mdp-tip') || '').startsWith('Trust external image links'));

// A raw-HTML image is a remote image too; the sanitiser path must count it.
c = render('<img src="https://cdn.example.com/raw.png" alt="raw">\n', { allowRawHtml: true });
ok('a raw-HTML remote image is enough to enable the toggle', trustToggle.disabled === false);

// And the trusted state names images as well.
c = render('![b](https://cdn.example.com/b.png)\n', {}, 'light', { trusted: true });
ok('the trusted tooltip says external image links are trusted',
   (trustToggle.getAttribute('data-mdp-tip') || '').startsWith('External image links are trusted'),
   trustToggle.getAttribute('data-mdp-tip'));

console.log(`\n${'='.repeat(46)}\n  ${pass} passed, ${fail} failed\n${'='.repeat(46)}`);
process.exit(fail === 0 ? 0 : 1);
