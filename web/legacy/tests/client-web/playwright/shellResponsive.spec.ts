import { test, expect } from '@playwright/test';

const breakpoints = [
  { name: 'mobile', width: 375, height: 812 },
  { name: 'tablet', width: 834, height: 1194 },
  { name: 'desktop', width: 1280, height: 800 },
];

const cPages = ['/workbench', '/app-knowledge-base/categories', '/files', '/data-center', '/audit/1/1'];

for (const vp of breakpoints) {
  for (const path of cPages) {
    test(`${vp.name} ${path} has no horizontal overflow`, async ({ page }) => {
      await page.setViewportSize({ width: vp.width, height: vp.height });
      await page.goto(path);
      const hasOverflow = await page.evaluate(() => document.documentElement.scrollWidth > document.documentElement.clientWidth);
      expect(hasOverflow).toBeFalsy();
    });
  }
}
