import assert from 'node:assert/strict';
import { describe, it, before } from 'node:test';
import { createRequire } from 'node:module';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { JSDOM } = requireFromWeb('jsdom') as typeof import('jsdom');

let looksLikeHtml: (value: string) => boolean;
let sanitizeDescriptionHtml: (value: string) => string;
let htmlToTextSummary: (value: string) => string;

before(async () => {
  const dom = new JSDOM('<!DOCTYPE html><html><body></body></html>');
  globalThis.window = dom.window as unknown as Window & typeof globalThis;
  globalThis.document = dom.window.document;
  globalThis.Node = dom.window.Node;
  globalThis.DocumentFragment = dom.window.DocumentFragment;
  globalThis.Element = dom.window.Element;
  globalThis.HTMLElement = dom.window.HTMLElement;
  globalThis.HTMLDocument = dom.window.HTMLDocument;

  const mod = await import('../../src/client-web/src/utils/safeHtml');
  looksLikeHtml = mod.looksLikeHtml;
  sanitizeDescriptionHtml = mod.sanitizeDescriptionHtml;
  htmlToTextSummary = mod.htmlToTextSummary;
});

describe('looksLikeHtml', () => {
  it('detects <div> tags', () => {
    assert.equal(looksLikeHtml('<div>hello</div>'), true);
  });

  it('detects <b> tags', () => {
    assert.equal(looksLikeHtml('<b>bold</b>'), true);
  });

  it('detects <a href=...> tags', () => {
    assert.equal(looksLikeHtml('<a href="https://example.com">link</a>'), true);
  });

  it('returns false for plain text', () => {
    assert.equal(looksLikeHtml('just some plain text'), false);
  });

  it('returns false for empty string', () => {
    assert.equal(looksLikeHtml(''), false);
  });

  it('returns false for "a < b and c > d"', () => {
    assert.equal(looksLikeHtml('a < b and c > d'), false);
  });
});

describe('sanitizeDescriptionHtml', () => {
  it('allows <b> and <i>', () => {
    const result = sanitizeDescriptionHtml('<b>bold</b> and <i>italic</i>');
    assert.equal(result, '<b>bold</b> and <i>italic</i>');
  });

  it('allows <em> and <strong>', () => {
    const result = sanitizeDescriptionHtml('<em>emphasized</em> and <strong>strong</strong>');
    assert.equal(result, '<em>emphasized</em> and <strong>strong</strong>');
  });

  it('allows <a> with href', () => {
    const result = sanitizeDescriptionHtml('<a href="https://example.com">link</a>');
    assert.equal(result, '<a href="https://example.com">link</a>');
  });

  it('allows <p>, <br>, <ul>, <ol>, <li>', () => {
    const input = '<p>para</p><br><ul><li>item</li></ul><ol><li>ordered</li></ol>';
    const result = sanitizeDescriptionHtml(input);
    assert.ok(result.includes('<p>para</p>'));
    assert.ok(result.includes('<br>') || result.includes('<br/>'));
    assert.ok(result.includes('<ul>'));
    assert.ok(result.includes('<li>item</li>'));
    assert.ok(result.includes('<ol>'));
  });

  it('allows <h2> and <h3> headings', () => {
    const result = sanitizeDescriptionHtml('<h2>标题</h2><h3>子标题</h3>');
    assert.equal(result, '<h2>标题</h2><h3>子标题</h3>');
  });

  it('allows <blockquote>, <pre>, <code>, <u> and <s>', () => {
    const result = sanitizeDescriptionHtml(
      '<blockquote>引用</blockquote><pre><code>const x = 1;</code></pre><u>下划线</u><s>删除线</s>'
    );
    assert.ok(result.includes('<blockquote>引用</blockquote>'));
    assert.ok(result.includes('<pre><code>const x = 1;</code></pre>'));
    assert.ok(result.includes('<u>下划线</u>'));
    assert.ok(result.includes('<s>删除线</s>'));
  });

  it('allows <a> with href, target and rel', () => {
    const result = sanitizeDescriptionHtml(
      '<a href="https://example.com" target="_blank" rel="noopener noreferrer">link</a>'
    );
    assert.equal(
      result,
      '<a href="https://example.com" target="_blank" rel="noopener noreferrer">link</a>'
    );
  });

  it('adds a secure rel with noopener and noreferrer to target=_blank links', () => {
    const result = sanitizeDescriptionHtml('<a href="https://example.com" target="_blank">link</a>');
    assert.ok(result.includes('href="https://example.com"'));
    assert.ok(result.includes('target="_blank"'));
    const rel = result.match(/rel="([^"]*)"/)?.[1] ?? '';
    assert.ok(rel.includes('noopener'));
    assert.ok(rel.includes('noreferrer'));
  });

  it('preserves existing safe rel tokens while enforcing noopener noreferrer', () => {
    const result = sanitizeDescriptionHtml(
      '<a href="https://example.com" target="_blank" rel="nofollow">link</a>'
    );
    const rel = result.match(/rel="([^"]*)"/)?.[1] ?? '';
    assert.ok(rel.includes('noopener'));
    assert.ok(rel.includes('noreferrer'));
    assert.ok(rel.includes('nofollow'));
  });

  it('does not add rel to links without target=_blank', () => {
    const result = sanitizeDescriptionHtml('<a href="https://example.com">link</a>');
    assert.equal(result, '<a href="https://example.com">link</a>');
  });

  it('does not add rel to links with other targets', () => {
    const result = sanitizeDescriptionHtml('<a href="https://example.com" target="_self">link</a>');
    assert.equal(result, '<a href="https://example.com" target="_self">link</a>');
  });

  it('treats target=_blank variants case-insensitively when enforcing secure rel', () => {
    for (const target of ['_BLANK', '_Blank', '_bLaNk']) {
      const result = sanitizeDescriptionHtml(`<a href="https://example.com" target="${target}">link</a>`);
      assert.ok(result.includes(`target="${target}"`), `preserves target=${target}`);
      const rel = result.match(/rel="([^"]*)"/)?.[1] ?? '';
      assert.ok(rel.includes('noopener'), `adds noopener for target=${target}`);
      assert.ok(rel.includes('noreferrer'), `adds noreferrer for target=${target}`);
    }
  });

  it('adds a secure rel to named targets that open a new browsing context', () => {
    const result = sanitizeDescriptionHtml(
      '<a href="https://example.com" target="external-window">link</a>'
    );
    assert.ok(result.includes('target="external-window"'));
    const rel = result.match(/rel="([^"]*)"/)?.[1] ?? '';
    assert.ok(rel.includes('noopener'));
    assert.ok(rel.includes('noreferrer'));
  });

  it('adds a secure rel to multi-token or non-keyword targets', () => {
    const result = sanitizeDescriptionHtml(
      '<a href="https://example.com" target="_blank foo">link</a>'
    );
    assert.ok(result.includes('target="_blank foo"'));
    const rel = result.match(/rel="([^"]*)"/)?.[1] ?? '';
    assert.ok(rel.includes('noopener'));
    assert.ok(rel.includes('noreferrer'));
  });

  it('leaves _parent and _top targets unmodified', () => {
    for (const target of ['_parent', '_top']) {
      const result = sanitizeDescriptionHtml(
        `<a href="https://example.com" target="${target}">link</a>`
      );
      assert.equal(result, `<a href="https://example.com" target="${target}">link</a>`);
    }
  });

  it('keeps safe mailto links', () => {
    const result = sanitizeDescriptionHtml('<a href="mailto:user@example.com">mail</a>');
    assert.equal(result, '<a href="mailto:user@example.com">mail</a>');
  });

  it('strips data-* attributes', () => {
    const result = sanitizeDescriptionHtml('<p data-track="x" data-custom="1">text</p>');
    assert.equal(result, '<p>text</p>');
  });

  it('strips data: URLs on links', () => {
    const result = sanitizeDescriptionHtml('<a href="data:text/html;base64,PHNjcmlwdD4=">bad</a>');
    assert.equal(result, '<a>bad</a>');
  });

  it('strips file: URLs on links', () => {
    const result = sanitizeDescriptionHtml('<a href="file:///etc/passwd">bad</a>');
    assert.equal(result, '<a>bad</a>');
  });

  it('strips unsafe content in one combined input', () => {
    const result = sanitizeDescriptionHtml(
      '<script>alert(1)</script><iframe src="https://evil.com"></iframe>' +
      '<img src="x" onerror="alert(1)"><p style="color:red" onclick="alert(1)">text</p>' +
      '<a href="javascript:alert(1)" data-id="7">click</a>'
    );
    assert.ok(!result.includes('script'));
    assert.ok(!result.includes('iframe'));
    assert.ok(!result.includes('img'));
    assert.ok(!result.includes('onerror'));
    assert.ok(!result.includes('onclick'));
    assert.ok(!result.includes('style='));
    assert.ok(!result.includes('data-id'));
    assert.ok(!result.includes('javascript:'));
  });

  it('removes <script> tags', () => {
    const result = sanitizeDescriptionHtml('<script>alert("xss")</script>hello');
    assert.equal(result, 'hello');
  });

  it('removes <iframe> tags', () => {
    const result = sanitizeDescriptionHtml('<iframe src="https://evil.com"></iframe>safe');
    assert.equal(result, 'safe');
  });

  it('removes <img> tags', () => {
    const result = sanitizeDescriptionHtml('<img src="x" onerror="alert(1)">text');
    assert.equal(result, 'text');
  });

  it('removes on* event handlers', () => {
    const result = sanitizeDescriptionHtml('<p onclick="alert(1)">click</p>');
    assert.equal(result, '<p>click</p>');
  });

  it('removes onerror handlers', () => {
    const result = sanitizeDescriptionHtml('<b onerror="alert(1)">safe</b>');
    assert.equal(result, '<b>safe</b>');
  });

  it('removes style attributes', () => {
    const result = sanitizeDescriptionHtml('<p style="color:red">text</p>');
    assert.equal(result, '<p>text</p>');
  });

  it('removes javascript: href', () => {
    const result = sanitizeDescriptionHtml('<a href="javascript:alert(1)">click</a>');
    assert.equal(result, '<a>click</a>');
  });

  it('returns empty string for empty input', () => {
    assert.equal(sanitizeDescriptionHtml(''), '');
  });

  it('preserves safe text content', () => {
    const result = sanitizeDescriptionHtml('just plain text');
    assert.equal(result, 'just plain text');
  });

  it('removes disallowed tags but keeps their text', () => {
    const result = sanitizeDescriptionHtml('<div>text</div><span>more</span>');
    assert.equal(result, 'textmore');
  });
});

describe('htmlToTextSummary', () => {
  it('separates adjacent block elements with a readable separator', () => {
    assert.equal(htmlToTextSummary('<p>Hello</p><p>World</p>'), 'Hello World');
  });

  it('keeps a space between adjacent table cell values', () => {
    assert.equal(htmlToTextSummary('<table><tr><td>甲</td><td>乙</td></tr></table>'), '甲 乙');
  });

  it('keeps a space between description-list terms and definitions', () => {
    assert.equal(htmlToTextSummary('<dl><dt>术语</dt><dd>定义</dd></dl>'), '术语 定义');
  });

  it('keeps a space between adjacent section-like blocks', () => {
    assert.equal(htmlToTextSummary('<section>一节</section><article>二节</article>'), '一节 二节');
  });

  it('separates table, section and dl content in one combined sample and stays text-only', () => {
    const input =
      '<table><tr><td>行1</td><td>行2</td></tr></table>' +
      '<section>总结</section><dl><dt>键</dt><dd>值</dd></dl>';
    const result = htmlToTextSummary(input);
    assert.equal(result, '行1 行2 总结 键 值');
    assert.ok(!result.includes('<'));
    assert.ok(!result.includes('>'));
  });

  it('avoids word concatenation when closing tags are omitted', () => {
    assert.equal(htmlToTextSummary('<table><tr><td>a<td>b</tr></table>'), 'a b');
    assert.equal(htmlToTextSummary('<ul><li>甲<li>乙</ul>'), '甲 乙');
  });

  it('drops attribute leftovers when a block tag contains a > inside quoted text', () => {
    assert.equal(htmlToTextSummary('<p title=a" > "b>text</p>'), 'text');
    assert.equal(htmlToTextSummary('<table><tr><td title=a" > "b>甲<td title=a" > "b>乙</tr></table>'), '甲 乙');
  });

  it('treats a quoted > in a block attribute as part of the tag', () => {
    assert.equal(htmlToTextSummary('<p title="a > b">前</p><p>后</p>'), '前 后');
  });

  it('creates a readable separator for attributed <br> and <hr> elements', () => {
    assert.equal(htmlToTextSummary('甲<br class="x">乙'), '甲 乙');
    assert.equal(htmlToTextSummary('甲<hr>乙'), '甲 乙');
    assert.equal(htmlToTextSummary('<p>甲</p><hr/><p>乙</p>'), '甲 乙');
    assert.equal(htmlToTextSummary('甲<br style="mso-data">乙'), '甲 乙');
  });

  it('treats whitespace-entity-only HTML as empty', () => {
    assert.equal(htmlToTextSummary('<p>&nbsp;</p>'), '');
    assert.equal(htmlToTextSummary('<p>&ensp;&emsp;</p>'), '');
    assert.equal(htmlToTextSummary('&#160;'), '');
    assert.equal(htmlToTextSummary('<td>&nbsp;</td><td>&nbsp;</td>'), '');
  });

  it('normalizes whitespace entity spellings between words', () => {
    assert.equal(htmlToTextSummary('A&nbsp;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#160;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#xA0;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#x00a0;B'), 'A B');
    assert.equal(htmlToTextSummary('<p>A&nbsp;B</p>'), 'A B');
    assert.equal(htmlToTextSummary('A&thinsp;B'), 'A B');
    assert.equal(htmlToTextSummary('A&emsp;B'), 'A B');
    assert.equal(htmlToTextSummary('A&nbsp;B&nbsp;C'), 'A B C');
  });

  it('normalizes named and numeric tab/newline entities between words', () => {
    assert.equal(htmlToTextSummary('A&Tab;B'), 'A B');
    assert.equal(htmlToTextSummary('A&NewLine;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#9;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#10;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#x9;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#xA;B'), 'A B');
    assert.equal(htmlToTextSummary('A&#x09;B'), 'A B');
  });
});
