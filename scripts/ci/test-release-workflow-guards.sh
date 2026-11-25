#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 发版链路护栏的静态断言。
#
# 这些文件一旦漏接一个字段，行为上可能仍然全绿：镜像里显示 0.0.0-local、
# 旧 CI 重跑把正式 tag 降回 pre-release、promote_tag 跳过来源校验……
# 只有真发一次版才会暴露。这里把跨文件接线钉死，失败信息直接指向缺的行。
# ---------------------------------------------------------------------------
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"

pass=0
fail=0
check() { # <label> <file> <literal>
  local label="$1" file="$2" literal="$3"
  if grep -Fq -- "$literal" "$ROOT/$file"; then
    echo "  ✅ $label"
    pass=$((pass + 1))
  else
    echo "  ❌ $label — $file 缺少 [$literal]"
    fail=$((fail + 1))
  fi
}

check_absent() { # <label> <file> <literal>
  local label="$1" file="$2" literal="$3"
  if grep -Fq -- "$literal" "$ROOT/$file"; then
    echo "  ❌ $label — $file 仍包含 [$literal]"
    fail=$((fail + 1))
  else
    echo "  ✅ $label"
    pass=$((pass + 1))
  fi
}

job_block() { # <file> <job-name>
  awk -v job="$2" '
    $0 ~ "^  " job ":$" { inside=1; next }
    inside && /^  [A-Za-z0-9_-]+:$/ { exit }
    inside { print }
  ' "$ROOT/$1"
}

check_in_job() { # <label> <file> <job> <literal>
  local label="$1" file="$2" job="$3" literal="$4"
  if job_block "$file" "$job" | grep -Fq -- "$literal"; then
    echo "  ✅ $label"
    pass=$((pass + 1))
  else
    echo "  ❌ $label — $file 的 job [$job] 缺少 [$literal]"
    fail=$((fail + 1))
  fi
}

check_not_in_job() { # <label> <file> <job> <literal>
  local label="$1" file="$2" job="$3" literal="$4"
  if job_block "$file" "$job" | grep -Fq -- "$literal"; then
    echo "  ❌ $label — $file 的 job [$job] 仍包含 [$literal]"
    fail=$((fail + 1))
  else
    echo "  ✅ $label"
    pass=$((pass + 1))
  fi
}

echo "== release workflow guards =="

# ---- 显示版本必须真的进入镜像，而不只是进 CI 的构建摘要 ----
check "Dockerfile 接收 display version" src/Pim.Api/Dockerfile 'ARG PIM_WEB_DISPLAY_VERSION'
check "Dockerfile 把它注入 Vite" src/Pim.Api/Dockerfile 'VITE_DISPLAY_VERSION="$PIM_WEB_DISPLAY_VERSION"'
check "Dockerfile 用它写 web-version.json" src/Pim.Api/Dockerfile '--version "$PIM_WEB_DISPLAY_VERSION"'
check "Dockerfile 构建期校验注入" src/Pim.Api/Dockerfile 'grep -R -q -F -- "$PIM_WEB_DISPLAY_VERSION" dist/assets'
check "build-docker.yml 声明 display_version 入参" .github/workflows/build-docker.yml 'display_version:'
check "build-docker.yml 传 PIM_WEB_DISPLAY_VERSION" .github/workflows/build-docker.yml 'PIM_WEB_DISPLAY_VERSION=${{ steps.ver.outputs.display_version }}'
check "build-docker.yml 真实镜像 smoke 读 /web-version.json" .github/workflows/build-docker.yml 'GET /web-version.json HTTP/1.0'
check "build-docker.yml 真实镜像 smoke 读 /api/version" .github/workflows/build-docker.yml 'GET /api/version HTTP/1.0'
check "ci.yml 把 display_version 传给 build-docker" .github/workflows/ci.yml 'display_version: ${{ needs.resolve-version.outputs.display_version }}'

# ---- VERSION-only PR 也要跑显示版本注入校验 ----
check "changes 透出 version 过滤器" .github/workflows/ci.yml 'version: ${{ steps.filter.outputs.version }}'
check "web-build 由 VERSION 变化触发" .github/workflows/ci.yml "needs.changes.outputs.version == 'true'"
check "web-build 实际校验注入值" .github/workflows/ci.yml 'Verify display version injection'
check "CI Gate 认识 version 触发" scripts/ci/evaluate-gate.sh 'W_VERSION'

# ---- release.yml 的入口、自动 PR 与提升守卫 ----
check "release 只能从 master 触发" .github/workflows/release.yml 'refs/heads/master'
check "bump PR 技术修改章节" .github/workflows/release.yml '## 技术修改 / Technical changes'
check "bump PR 功能变化章节" .github/workflows/release.yml '## 功能变化 / Feature changes'
check "bump PR 如何体验章节" .github/workflows/release.yml '## 如何体验 / How to try it'
check "bump PR 测试章节" .github/workflows/release.yml '## 测试 / Tests'
check "bump PR 需求验收映射" .github/workflows/release.yml '## 需求与验收映射 / Requirement & Acceptance Mapping'
check "未运行的 release 标 NOT-VERIFIED" .github/workflows/release.yml 'NOT-VERIFIED'
check "promote_tag 走来源校验脚本" .github/workflows/release.yml 'scripts/ci/validate-promote-target.sh'
check "promote 校验从 REST 读 latest" scripts/ci/validate-promote-target.sh 'releases/latest'
check_absent "promote 校验不请求不存在的 isLatest 字段" scripts/ci/validate-promote-target.sh 'databaseId,tagName,isPrerelease,isDraft,targetCommitish,assets,isLatest'
check "promote 校验绑定 targetCommitish" scripts/ci/validate-promote-target.sh 'targetCommitish'
check "promote 校验绑定 run headSha" scripts/ci/validate-promote-target.sh 'headSha'
check "promote 校验绑定台账 apiVersion" scripts/ci/validate-promote-target.sh 'LEDGER_API_VERSION'
check "promote 校验当前 manifest VERSION" scripts/ci/validate-promote-target.sh 'HEAD_VERSION'
check "promote 拒绝 latest 回退" scripts/ci/validate-promote-target.sh 'latest 回退'
check "提升后显式 make_latest" .github/workflows/release.yml 'make_latest=true'
check "make_latest 使用 REST 数字 id" .github/workflows/release.yml '--json databaseId'
check "databaseId 必须为纯数字" .github/workflows/release.yml '"$RELEASE_ID" =~ ^[0-9]+$'
check "提升可重入（已正式版不再早退）" .github/workflows/release.yml 'IS_PRE="$(gh release view'
check "提升重入后继续收敛 latest" .github/workflows/release.yml '本次重入只收敛正文与 latest'
check_absent "提升步骤不再提前退出" .github/workflows/release.yml '已经是正式版，幂等退出'
check "提升后回读 /releases/latest" .github/workflows/release.yml 'releases/latest'
check "提升后回读 release 本体" .github/workflows/release.yml 'FINAL_PRE'
check "apply 作业同步到当前 master" .github/workflows/release.yml 'git fetch --prune origin master'
check "prepare 作业不持有发布锁" .github/workflows/release.yml 'prepare:'
check_not_in_job "prepare 作业不占用 pim-release-mutation" .github/workflows/release.yml prepare 'group: pim-release-mutation'
check_in_job "apply 作业持有共享发布锁" .github/workflows/release.yml apply 'group: pim-release-mutation'
check_in_job "apply 作业在锁内校验来源" .github/workflows/release.yml apply 'Validate promote target'
check_in_job "apply 作业在锁内提升" .github/workflows/release.yml apply 'Promote to formal release'
check_in_job "prepare 作业等待 master 构建" .github/workflows/release.yml prepare 'Wait for the master build'
check "bump PR 说明 build-docker 只在合并后 master 跑" .github/workflows/release.yml '在合并后的 master 构建中执行'
check "bump PR 不把 build-docker 当成 PR 证据" .github/workflows/release.yml 'dist/assets 含'

# ---- 旧 CI 重跑不得把正式 release 降回 pre-release ----
check "CI 有正式版守卫步骤" .github/workflows/ci.yml 'Guard existing formal release'
check "CI 发布守卫走行为测试脚本" .github/workflows/ci.yml 'release-publish-guard.sh'
check "CI 发布守传入 run attempt" .github/workflows/ci.yml 'RUN_ATTEMPT: ${{ github.run_attempt }}'
check "CI 发布守传入 commit sha" .github/workflows/ci.yml 'SHA: ${{ github.sha }}'
check "CI release job 持有共享发布锁" .github/workflows/ci.yml 'group: pim-release-mutation'
check "发布守卫正式版不重发" scripts/ci/release-publish-guard.sh 'formal-exists'
check "发布守卫重跑只认当前 master HEAD" scripts/ci/release-publish-guard.sh 'commits/master'
check "发布守卫 API 错误 fail-closed" scripts/ci/release-publish-guard.sh '不把 API 错误当成『不存在』'
check "跳过发布有可见 summary" .github/workflows/ci.yml 'Release publication skipped'
check "发布 step 受 publish 守卫控制" .github/workflows/ci.yml "steps.existing.outputs.publish == 'true'"
check "镜像 :latest 由守卫决定" .github/workflows/build-docker.yml 'image-latest-guard.sh'
check "镜像守卫使用本次 commit sha" .github/workflows/build-docker.yml 'SHA: ${{ github.sha }}'
check "镜像 :latest 不跟旧 run 回退" .github/workflows/build-docker.yml 'steps.latest.outputs.push_latest'

# ---- 文档/自动 PR 的版本来源必须与实现一致 ----
check "web README 说明显示版本来自 VITE_DISPLAY_VERSION" web/README.md '与本次构建注入的 `VITE_DISPLAY_VERSION` 同源'
check "release 文档说明 latest 回退防护" docs/release-channels.md '同显示版本的旧 CalVer 会被拒绝'
check "release 文档说明旧 workflow 定义不可回溯" docs/release-channels.md '触发时那版 workflow 定义'

if [[ "$fail" -ne 0 ]]; then
  echo "release workflow guard tests failed: ${fail} missing, ${pass} passed" >&2
  exit 1
fi
echo "release workflow guard tests passed (${pass} checks)"
