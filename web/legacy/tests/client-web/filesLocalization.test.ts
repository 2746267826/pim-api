import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';

// PR-2 新增的文件板块组件同样要覆盖：否则「文件页有英文文案」会在新组件里漏网
const sourceFiles = [
  'src/client-web/src/pages/FilesPage.tsx',
  'src/client-web/src/components/files/OneDriveBindDialog.tsx',
  'src/client-web/src/components/files/OneDriveFileList.tsx',
  'src/client-web/src/components/files/OneDrivePreviewPane.tsx',
  'src/client-web/src/components/files/OneDriveFileTree.tsx',
  'src/client-web/src/components/files/SyncBanner.tsx',
  'src/client-web/src/components/files/RowMenu.tsx',
  'src/client-web/src/components/files/FileOperationDialogs.tsx',
  'src/client-web/src/components/files/ImageGrid.tsx',
  'src/client-web/src/components/files/FileThumbnail.tsx',
  'src/client-web/src/components/files/upload/TransferPanel.tsx',
];
const source = sourceFiles
  .map(file => readFileSync(resolve(file), 'utf8'))
  .join('\n');

function escapeRegExp(value: string) {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

function hasVisibleText(text: string) {
  const escaped = escapeRegExp(text);
  const quoted = new RegExp(`(['"\`])${escaped}\\1`);
  const jsxText = new RegExp(`>\\s*${escaped}(?=\\s|[<{])`);
  const embeddedPrompt = text.includes('?') && source.includes(text);
  return quoted.test(source) || jsxText.test(source) || embeddedPrompt;
}

const forbiddenVisibleText = [
  'Provider',
  'Loading providers...',
  'No provider connected.',
  'Bind Nextcloud',
  'Internal URL',
  'Username',
  'App password',
  'Folder tree',
  'No child folders here.',
  'Trash',
  'Loading trash...',
  'Trash is empty.',
  'Files',
  'Search files',
  'Hybrid',
  'Keyword',
  'Semantic',
  'Name',
  'Size',
  'Modified',
  'Loading files...',
  'No search results.',
  'No files in this folder.',
  'Semantic hits',
  'Details',
  'Select a file or folder.',
  'Loading details...',
  'Selected item is unavailable.',
  'View primary',
  'Edit separately',
  'Download',
  'Open in Nextcloud',
  'OOXML edit opens the provider editor.',
  'Type',
  'Synced',
  'Rename',
  'Move',
  'Delete to trash',
  'No AI result for current version.',
  'Versions',
  'Loading versions...',
  'No versions.',
  'Restore version',
  'Suggestions',
  'Loading suggestions...',
  'No suggestions for this item.',
  'Accept - mark useful',
  'Dismiss',
  'Nextcloud provider saved.',
  'Connection test passed.',
  'Connection test failed.',
  'Sync completed.',
  'Version restored.',
  'Trash item restored.',
  'Select a provider before restoring trash.',
  'Base URL, username, and app password are required.',
  'Move to path',
  'New name',
  'to trash?',
];

for (const text of forbiddenVisibleText) {
  assert.equal(
    hasVisibleText(text),
    false,
    `FilesPage should not expose English UI text: ${text}`,
  );
}

const requiredChineseText = [
  '文件',
  'OneDrive 个人版',
  '内容留在云端',
  '绑定 OneDrive',
  '还没有绑定 OneDrive',
  'Azure 应用注册的 Client ID',
  '获取设备码',
  '输入设备代码',
  '立即同步',
  '尚未同步',
  '正在同步',
  '同步出错',
  '断开',
  '已断开 OneDrive 绑定',
  '搜索当前文件夹',
  '此文件夹为空',
  '没有匹配的文件',
  '选择一个文件查看预览',
  '下载',
  '编辑文本',
  '保存到 OneDrive',
  '历史版本',
  '恢复',
  '加载缩略图',
  '列表视图',
  '网格视图',
  '文件列表',
  '大小',
  '修改时间',
];

for (const text of requiredChineseText) {
  assert.match(source, new RegExp(escapeRegExp(text)), `FilesPage should include Chinese UI text: ${text}`);
}
