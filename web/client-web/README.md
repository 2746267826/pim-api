# PIM 前端（client-web）

基于 `../frontend-rebuild-spec/` 重建的 PIM 前端：一套 React 19 SPA，服务 Windows 与 Android（手机/平板），双壳分发（Capacitor Android / Tauri Windows）。

## 文档

- [docs/DESIGN.md](docs/DESIGN.md) —— 视觉规范（浅色效率 · 效率蓝 · 柔和描边 · 紧凑密度），tokens 唯一来源 `src/styles/tokens.css`
- [docs/CONVENTIONS.md](docs/CONVENTIONS.md) —— 工程约定（网络层/轮询/枚举/URL 状态/坑位自检清单）

## 常用命令

```bash
npm install          # 安装依赖
npm run dev          # 开发服务器（/api 代理 → 测试 API，可用 PIM_API_TARGET 覆盖）
npm run test         # vitest 单测
npm run build        # tsc + vite build → dist/
```

### 后端地址与测试账号

服务器地址与测试账号不写在源码里，统一放在 `client-web/.env`（已 gitignore）。首次使用先复制模板：

```bash
cp .env.example .env    # 然后填入 PIM_API_TARGET / PIM_TEST_USER / PIM_TEST_PASS
```

开发服务器把 `/api` 代理到 `.env` 中的 `PIM_API_TARGET`；命令行传入的同名环境变量优先，未配置时回落到 `http://localhost:5858`：

```bash
PIM_API_TARGET=http://localhost:5858 npm run dev   # 临时指向本机后端
```

`scripts/` 下的自验与 QA 脚本（`verify-*.mjs`、`qa-*.mjs`）同样从 `.env` 取地址和账号，缺失时会直接报错提示。

也可在应用内「设置 → 服务器」直接填写 API 地址（不依赖代理；跨域需后端 CORS 白名单放行该来源）。

> `scripts/mock-api.mjs` 是**已停用**的离线 mock（原用于无后端时的视觉走查），仅在没有可用后端时临时启用。
> 它**不再默认占用 5858**（该端口通常是真实 PIM API）：默认绑内核分配的空闲端口并在启动行打印实际端口；
> 需要固定端口时显式 `MOCK_PORT=xxxx node scripts/mock-api.mjs`。

### Android 壳（Capacitor）

```bash
npm run build && npx cap sync android
cd android && ./gradlew assembleDebug   # 产物 ~9.2MB
# 产物：android/app/build/outputs/apk/debug/app-debug.apk
```

本机构建要求（已验证通过）：
- `ANDROID_HOME` 指向 `%LOCALAPPDATA%\Android\Sdk`
- `JAVA_HOME` 需指向**完整 JDK**（默认 Adoptium 是 JRE 无 javac；已验证用 Android Studio 自带 `C:\Program Files\Android\Android Studio\jbr`，JDK 21）
- 本沙箱网络需 JVM 代理：`-Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=12000 -Dhttp.proxyHost=... -Dhttp.proxyPort=...`（仓库同时配置了阿里云镜像作为无代理环境的回退）

完整命令：

```bash
cd android && JAVA_HOME="C:\Program Files\Android\Android Studio\jbr" \
  ./gradlew assembleDebug -Dhttps.proxyHost=127.0.0.1 -Dhttps.proxyPort=12000 -Dhttp.proxyHost=127.0.0.1 -Dhttp.proxyPort=12000
```

壳内首次启动会进入 `/setup` 填写 API 地址（`lib/apiBase.ts`，存 `pim.apiBase`）。

### Windows 壳（Tauri 2，已交付）

`src-tauri/` 为严格远程加载壳：安装包只含 `shell-dist/` 引导页（记忆服务器地址 → 倒计时自动进入），React 应用不进安装包（更新只发服务端）。产物 `PIM_<版本>_x64-setup.exe`（~1.8MiB）。

```bash
npx tauri build   # 需 Rust stable-msvc + MSVC Build Tools；国内网络建议 ~/.cargo/config.toml 配 rsproxy 镜像
npx tauri dev     # 开发调试（加载 Vite dev server）
```

## 目录

```
src/app        路由/守卫/外壳/providers/nav
src/api        域 client + 类型（apiFetch 401 单飞刷新）
src/lib        apiBase / polling / enums / businessDay / storage / notify
src/components/ui    设计系统原语
src/features/<域>    pages / components / hooks / queries
src/embed/android    无壳内嵌页 + pimAndroid 桥（P4）
android/       Capacitor Android 工程
```

## 实施阶段

P0 骨架与标准 ✅ → P1 今日/日历/任务/确认 → P2 工作台/提醒/报告/习惯/快速记录/数据中心 → P3 PC/浏览器/手机/位置/设备/状态 → P4 文件/设置域/知识库/内嵌+桥 → P5 双壳与打磨。
