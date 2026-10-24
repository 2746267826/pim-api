import assert from 'node:assert/strict';
import { dataReliabilityApiPaths, dataReliabilityViolationExportLimit } from '../../src/client-web/src/api/dataReliability';

// 路径必须与后端 DataReliabilityEndpoints 的路由逐字对齐（/api/v1 前缀由 apiGet/apiPost 内部拼接）。
assert.equal(dataReliabilityApiPaths.inspection(), '/data-reliability/inspection');
assert.equal(dataReliabilityApiPaths.refresh(), '/data-reliability/inspection/refresh');

assert.equal(
  dataReliabilityApiPaths.violations('S2', 2000),
  '/data-reliability/rules/S2/violations?limit=2000'
);

// 尺子编号必须被 URL 编码，避免奇怪的编号拼出错误路径。
assert.equal(
  dataReliabilityApiPaths.violations('S/2', 25),
  '/data-reliability/rules/S%2F2/violations?limit=25'
);

// 默认导出上限固定为 2000（面板只显示 10 条，完整清单走导出）。
assert.equal(dataReliabilityViolationExportLimit, 2000);
assert.equal(
  dataReliabilityApiPaths.violations('S1'),
  '/data-reliability/rules/S1/violations?limit=2000'
);

console.error('PASS: dataReliabilityApiPath');
