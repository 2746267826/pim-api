import assert from 'node:assert/strict';
import { primaryNavItems } from '../../src/client-web/src/layout/Sidebar';

const mobileRecords = primaryNavItems.find(item => item.label === '手机记录');
const locationHistory = primaryNavItems.find(item => item.label === '历史位置');

assert.equal(mobileRecords?.path, '/mobile-records');
assert.equal(locationHistory?.path, '/location-history');
