# 发版通道（显示版本号 / dev 版 / 正式版）

本文只讲一件事：这个仓库怎么发版，为什么有两个版本号，以及「正式版」是怎么来的。
代码位置见 `.github/workflows/ci.yml`（自动构建 + pre-release）和
`.github/workflows/release.yml`（提升正式版）。

## 两个版本号，各管一件事

| 号 | 来源 | 干什么 | 会变吗 |
| --- | --- | --- | --- |
| 显示版本 `x.y.z` | 仓根 `VERSION` 文件（一行） | 给人看：页脚、设置→关于、release 标题 | 只在发正式版时改一次 |
| 机器版本 CalVer `26.11.1234` | `GITHUB_RUN_NUMBER` | 更新检查、产物比较、客户端升级判断 | 每次构建都变 |

两套号互不干扰：更新检查只认机器版本，所以显示号停在 `1.0.0` 期间连着出几十个
dev 构建也不会出现"版本回退"。`versionCode = 100000 + run_number` 单调递增，
客户端永远不会把正式版判成比 dev 版旧。

显示号由 CI 注入：前端用 `VITE_DISPLAY_VERSION`；Docker 的 `web-build` 阶段用
同一个值写 `/app/wwwroot/web-version.json` 与镜像标签 `org.pim.web-version`。
本地 `npm run dev` 没注入时回落成 `0.0.0-local`（不再写死一个看起来像真版本的号）。
`build-docker` 的 smoke 会从运行中的容器 GET `/web-version.json` 和
`/api/version`，断言 `version` / `webVersion` 都是目标显示版本，并检查前端
bundle 里确实出现该版本号。

## 自动发布 = dev 版（pre-release）

每次合并到 master 都会照常构建并发出一条 release，tag 格式、资产名一个字没变
（`api-v26.11.1234` + 三个 tar.gz + `pim-web-version.json`），只是标记为
`prerelease: true`。

关键副作用正好是我们想要的：GitHub 的 `/releases/latest` 天生跳过 pre-release，
所以客户端更新检查（`releases/latest/download/version.json` 那条链路）永远指向
**最近一个正式版**，开发期几十个 dev 版本不会给用户推"有新版本"。

增量构建的基线仍然是「按版本号排序的最新 tag」，pre-release 依然参与，所以
「未变化的组件从上一版搬资产」这套逻辑完全不受影响。

## 正式版 = 提升一次已经过 CI 的构建

```bash
# 正常发版（notes.md 是给人看的说明，必须引用合并过的 PR）
gh workflow run release.yml \
  -f version=1.1.0 \
  -f notes="$(cat notes.md)"

# 只重试「提升」这一步（构建已经好了，或上次提升失败）
gh workflow run release.yml \
  -f version=1.1.0 \
  -f notes="$(cat notes.md)" \
  -f promote_tag=api-v26.11.1234
```

`release.yml` 只能从 master 触发。`promote_tag` 不是「给个 tag 就提升」：
脚本会校验 tag 指向的 commit 在当前 master 历史里、该 commit 的 `VERSION`
等于输入的显示版本、tag 里的 run number 对应一次成功的 master push 构建，
并要求四个 release 资产齐全、`pim-web-version.json` 里的显示版本也一致。
已经提升过的正式版可以重入，提升步骤会继续收敛正文/title 与
`make_latest`，而不是提前退出。

`release.yml` 依次做四件事：

1. 校验版本号（`x.y.z`，且必须大于 master 上 `VERSION` 的值）与发行说明；
2. 开一条只改 `VERSION` 的 PR，然后启用自动合并（PR 门禁照走，不绕过保护）；
3. 等这次合并提交的 master 构建跑完（它按常规发出一条 pre-release）；
4. 把那条 pre-release 改判为正式版（`--prerelease=false`），正文换成人写的说明，
   并把 `make_latest` 指到它。

**为什么不在 `release.yml` 里重新构建**：正式版必须是"已经过完 CI 的那次构建"。
二次构建得到的是另一个产物，等于发了一个没被测过的包。

## 两条必须知道的坑

1. **`GITHUB_TOKEN` 触发不了 workflow。** 用它 push 或开 PR，`pull_request` /
   `push` 事件不会启动任何 workflow —— bump PR 会永远等不到 `CI Gate`，合并后的
   master 构建也不会跑。所以 `release.yml` 用 secret `PIM_RELEASE_TOKEN`
   （PAT 或 GitHub App 令牌：`contents` 写、`pull_requests` 写、`actions` 读）。
   缺这个 secret 时 workflow 会在第一步就明确报错，不会静默卡住。
2. **只改 `VERSION` 的 master 运行强制全量重建**（`FORCE_ON_VERSION`，见
   `scripts/ci/decide-components.sh`）。否则路径过滤会判定"组件都没变"，发布 job
   把上一版的旧资产搬过来 —— 正式版里带的还是旧版本号。PR 上不强制（就一个版本号，
   没必要白跑一次完整构建）。

## 发行说明不许编造

`scripts/ci/validate-release-notes.sh` 机械校验，任何一条不过就失败：

- 不能为空、不能留 `TODO` / `待补充` 之类的占位符；
- 必须有人能读的结构（至少一条 `- 条目`）；
- 必须引用至少一个 PR，且**每个**被引用的 PR 都真的存在并已合并。

写说明时以「上一个正式版..HEAD 的已合并 PR 列表」为唯一事实来源，逐条带上
PR 号；对不上号的条目会被拦下，不会进正式版正文。
