/// <reference types="vite/client" />

// 构建期注入的变量（见 .github/workflows/ci.yml 的 web-build）。
// 这里显式声明，别依赖 vite/client 的宽松索引签名：
// 打错变量名时会立刻报错，而不是悄悄变成 undefined 再回落到本地标记。
interface ImportMetaEnv {
  /** 给人看的显示版本号，来源是仓根 VERSION 文件（正式版是干净的 x.y.z）。 */
  readonly VITE_DISPLAY_VERSION?: string
  /** 内部 CalVer 版本（机器用，来自 run number）。 */
  readonly VITE_APP_VERSION?: string
  readonly VITE_GIT_SHA?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
