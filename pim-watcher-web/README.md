# PIM Browser Watcher (pim-watcher-web)

Fork of `ActivityWatch/aw-watcher-web` (MPL-2.0) 改为直连 PIM 客户端 `http://localhost:15601`.

## 改动清单（对照 `docs/pim-native-tracker/docs/02-browser-plugin-guide.md`）

- 去掉 `aw-client` 依赖，改用 `fetch` 直连
- `baseURL` 改为 `http://localhost:15601`
- 简化 `heartbeat` 格式：`{url,title,audible,incognito,tabCount,timestamp}`
- 删除 `ensureBucket` / `detectHostname` / `consent` / `hostname` 检测
- `storage.ts` 简化：去掉 `apiKey/hostname/syncStatus`
- `manifest.json` 改名 “PIM Browser Watcher”，host_permissions 新增 `http://localhost:15601/*`
- 启动时 `waitForPimClient` 轮询 `GET /browser/ping`

## 安装

Chrome / Edge 扩展管理页 -> 开发者模式 -> 加载已解压的扩展程序 -> 选择 `pim-watcher-web/dist/`

## 构建

```bash
npm install
npm run build  # 输出 dist/
```

## 通信协议

```
POST http://localhost:15601/browser/heartbeat
{
  "url": "https://github.com/...",
  "title": "GitHub",
  "audible": false,
  "incognito": false,
  "tabCount": 5,
  "timestamp": "2026-08-31T10:00:00Z"
}

GET http://localhost:15601/browser/ping -> { "status":"ok", "version":"1.0.0" }
```

## 仓库

独立仓库 `pim-watcher-web`，本目录为改造参考骨架，完整 fork 需从 `https://github.com/ActivityWatch/aw-watcher-web` 拉取后按上表修改。
