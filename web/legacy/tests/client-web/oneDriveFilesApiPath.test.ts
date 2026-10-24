import assert from 'node:assert/strict';
import { fileApiPaths } from '../../src/client-web/src/api/files';

// OneDrive（文件模块 v2）端点路径契约，见 designs/onedrive-files-v2.md
const itemId = '22222222-2222-2222-2222-222222222222';
const snapshotId = '55555555-5555-5555-5555-555555555555';

assert.equal(fileApiPaths.bindOneDrive(), '/files/providers/onedrive');
assert.equal(
  fileApiPaths.oneDriveBindingStatus(itemId),
  `/files/providers/${itemId}/binding-status`,
);
assert.equal(fileApiPaths.provider(itemId), `/files/providers/${itemId}`);
assert.equal(fileApiPaths.itemContent(itemId), `/files/items/${itemId}/content`);
assert.equal(
  fileApiPaths.itemThumbnail(itemId),
  `/files/items/${itemId}/thumbnail?size=medium`,
);
assert.equal(
  fileApiPaths.itemThumbnail(itemId, 'large'),
  `/files/items/${itemId}/thumbnail?size=large`,
);
assert.equal(fileApiPaths.itemPreviewUrl(itemId), `/files/items/${itemId}/preview-url`);
assert.equal(fileApiPaths.itemText(itemId), `/files/items/${itemId}/text`);
assert.equal(fileApiPaths.itemSnapshots(itemId), `/files/items/${itemId}/snapshots`);
assert.equal(
  fileApiPaths.itemSnapshotRestore(itemId, snapshotId),
  `/files/items/${itemId}/snapshots/${snapshotId}/restore`,
);
console.error('PASS: oneDriveFilesApiPath');
