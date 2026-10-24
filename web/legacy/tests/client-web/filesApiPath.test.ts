import assert from 'node:assert/strict';
import { fileApiPaths } from '../../src/client-web/src/api/files';

const providerId = '11111111-1111-1111-1111-111111111111';
const itemId = '22222222-2222-2222-2222-222222222222';
const versionId = '33333333-3333-3333-3333-333333333333';
const suggestionId = '44444444-4444-4444-4444-444444444444';

assert.equal(fileApiPaths.providers(), '/files/providers');
assert.equal(fileApiPaths.bindNextcloud(), '/files/providers/nextcloud');
assert.equal(fileApiPaths.providerTest(providerId), `/files/providers/${providerId}/test`);
assert.equal(fileApiPaths.providerSync(providerId), `/files/providers/${providerId}/sync`);
assert.equal(fileApiPaths.items({ path: '/Reports' }), '/files/items?path=%2FReports');
assert.equal(fileApiPaths.items(), '/files/items?path=%2F');
// REQ-3/REQ-8/REQ-9：分页、当前文件夹过滤、排序与目录过滤都要真的进查询串
assert.equal(
  fileApiPaths.items({ path: '/图片/Screenshots', page: 3, pageSize: 100 }),
  '/files/items?path=%2F%E5%9B%BE%E7%89%87%2FScreenshots&page=3&pageSize=100',
);
assert.equal(
  fileApiPaths.items({ path: '/文档', q: '报告', sort: 'modified', order: 'desc' }),
  '/files/items?path=%2F%E6%96%87%E6%A1%A3&q=%E6%8A%A5%E5%91%8A&sort=modified&order=desc',
);
assert.equal(fileApiPaths.items({ path: '/', type: 'folder' }), '/files/items?path=%2F&type=folder');
assert.equal(fileApiPaths.item(itemId), `/files/items/${itemId}`);
assert.equal(fileApiPaths.upload(), '/files/items/upload');
assert.equal(fileApiPaths.itemDownload(itemId), `/files/items/${itemId}/download`);
// REQ-20 / F-3：下载直链改走 JSON 端点，页面不再 fetch 跟随 302（那样会把文件体多传一遍）
assert.equal(fileApiPaths.itemDownloadUrl(itemId), `/files/items/${itemId}/download-url`);
assert.equal(fileApiPaths.move(itemId), `/files/items/${itemId}/move`);
assert.equal(fileApiPaths.rename(itemId), `/files/items/${itemId}/rename`);
assert.equal(fileApiPaths.trash(), '/files/trash');
assert.equal(fileApiPaths.trashRestore(providerId, 'trash/report.docx'), `/files/trash/${providerId}/restore?trashId=trash%2Freport.docx`);
assert.equal(fileApiPaths.versions(itemId), `/files/items/${itemId}/versions`);
assert.equal(fileApiPaths.versionDownload(itemId, versionId), `/files/items/${itemId}/versions/${versionId}/download`);
assert.equal(fileApiPaths.versionRestorePreview(itemId, versionId), `/files/items/${itemId}/versions/${versionId}/restore-preview`);
assert.equal(fileApiPaths.versionRestore(itemId, versionId), `/files/items/${itemId}/versions/${versionId}/restore`);
assert.equal(fileApiPaths.index(itemId), `/files/items/${itemId}/index`);
assert.equal(fileApiPaths.search('budget report', 'hybrid'), '/files/search?q=budget+report&mode=hybrid');
// REQ-8 / P7：全盘搜索结果按 100/页翻页
assert.equal(
  fileApiPaths.search('报告', 'keyword', 2, 100),
  '/files/search?q=%E6%8A%A5%E5%91%8A&mode=keyword&page=2&pageSize=100',
);
assert.equal(fileApiPaths.suggestions(), '/files/suggestions');
assert.equal(fileApiPaths.dismissSuggestion(suggestionId), `/files/suggestions/${suggestionId}/dismiss`);
assert.equal(fileApiPaths.acceptSuggestion(suggestionId), `/files/suggestions/${suggestionId}/accept`);
assert.equal(fileApiPaths.openLink(itemId, 'nextcloud'), `/files/items/${itemId}/open-link?mode=nextcloud`);
