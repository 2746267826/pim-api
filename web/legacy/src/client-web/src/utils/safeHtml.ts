import DOMPurify from 'dompurify';

const ALLOWED_TAGS = [
  'b', 'i', 'em', 'strong', 'a', 'p', 'br', 'ul', 'ol', 'li',
  'h2', 'h3', 'blockquote', 'pre', 'code', 'u', 's',
];
const ALLOWED_ATTR = ['href', 'target', 'rel'];
const BLOCK_TAG_RE =
  /<\/?(?:p|h[1-6]|li|blockquote|pre|ul|ol|div|table|thead|tbody|tfoot|tr|td|th|caption|colgroup|dl|dt|dd|section|article|header|footer|aside|nav|main|figure|figcaption|details|summary|address)\b(?:\s(?:"[^"]*"|'[^']*'|[^>'"])*)?>/gi;
const BR_RE = /<br\b(?:\s(?:"[^"]*"|'[^']*'|[^>'"])*)?\/?>/gi;
const HR_RE = /<hr\b(?:\s(?:"[^"]*"|'[^']*'|[^>'"])*)?\/?>/gi;
const WHITESPACE_ENTITY_RE =
  /&(?:#x0*(?:20|a0|9|a)(?![0-9a-f])|#0*(?:32|160|9|10)(?![0-9])|nbsp|ensp|emsp|thinsp|zwsp|zwnj|lrm|rlm|tab|newline);?/gi;

DOMPurify.addHook('afterSanitizeAttributes', (node) => {
  if (node.tagName !== 'A') return;
  const target = (node.getAttribute('target') ?? '').trim().toLowerCase();
  if (target === '' || target === '_self' || target === '_parent' || target === '_top') return;
  const rel = new Set((node.getAttribute('rel') ?? '').split(/\s+/).filter(Boolean));
  rel.add('noopener');
  rel.add('noreferrer');
  node.setAttribute('rel', Array.from(rel).join(' '));
});

export function looksLikeHtml(value: string): boolean {
  if (!value) return false;
  return /<[a-z][\s\S]*>/i.test(value.trim());
}

export function sanitizeDescriptionHtml(value: string): string {
  if (!value) return '';
  return DOMPurify.sanitize(value, {
    ALLOWED_TAGS,
    ALLOWED_ATTR,
    ALLOW_DATA_ATTR: false,
  });
}

export function htmlToTextSummary(value: string): string {
  if (!value) return '';
  const withSeparators = value
    .replace(WHITESPACE_ENTITY_RE, ' ')
    .replace(BLOCK_TAG_RE, ' ')
    .replace(BR_RE, ' ')
    .replace(HR_RE, ' ');
  const text = DOMPurify.sanitize(withSeparators, {
    ALLOWED_TAGS: [],
    ALLOWED_ATTR: [],
    ALLOW_DATA_ATTR: false,
  });
  return text.replace(WHITESPACE_ENTITY_RE, ' ').replace(/\s+/g, ' ').trim();
}
