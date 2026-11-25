#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 校验一个 pre-release tag 是否可以被提升为正式版。
#
# 只相信 tag 名字是不够的。要证明它是「一次已经在 master 上跑过 CI 的构建」：
#   1. tag 形如 <prefix>-vYY.MM.N，N 是该次 CI 的 run number；
#   2. tag 指向的 commit 在当前 checkout 的 master 历史里；
#   3. 该 commit 的仓根 VERSION 等于输入的显示版本；
#   4. 该 commit 上有一个成功的 master push 构建，run number 与 tag 一致，
#      且 run 的 headSha 就是 tag commit；
#   5. release 的 targetCommitish 精确等于 tag commit（不是分支名）；
#   6. release 资产齐全，台账 pim-web-version.json 的 version 等于目标显示版本、
#      apiVersion 等于 tag 的机器版本（26.11.42）；
#   7. 目标 tag 不比当前 /releases/latest 更旧：
#      - 同一个 release 重入 → 允许（只收敛正文/latest）；
#      - 旧 CalVer / 低显示版本 → 拒绝，避免把 latest 指回旧构建。
#
# 历史 release（仍是旧流水线写入机器版本到台账 version、或 targetCommitish 不是
# commit sha）无法证明显示版本绑定，按设计拒绝提升；它们不是「可重入」对象。
#
# 成功时输出 tag/sha/run_id/run_number/is_prerelease/is_latest 到 stdout；
# 有 GITHUB_OUTPUT 时同时写入，供 workflow 直接引用。
#
# 用法：
#   scripts/ci/validate-promote-target.sh --repo owner/repo --tag api-v26.11.42 --version 1.1.0
#
# 注意：gh 2.98 的 `gh release view --json` 没有 isLatest 字段。latest 必须从
# REST /releases/latest 读取；该调用失败时 fail-closed，绝不把 API 错误当成
# 「没有 latest」。
# ---------------------------------------------------------------------------
set -euo pipefail

REPO=""
TAG=""
VERSION=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="${2:-}"; shift 2 ;;
    --tag) TAG="${2:-}"; shift 2 ;;
    --version) VERSION="${2:-}"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

die() {
  echo "::error::$*" >&2
  echo "$*" >&2
  exit 1
}

[[ -n "$REPO" ]] || die "--repo is required"
[[ -n "$TAG" ]] || die "--tag is required"
[[ -n "$VERSION" ]] || die "--version is required"
[[ "$VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] \
  || die "显示版本必须是 x.y.z（收到 '${VERSION}'）"

command -v gh >/dev/null 2>&1 || die "gh is required"
command -v jq >/dev/null 2>&1 || die "jq is required"
command -v git >/dev/null 2>&1 || die "git is required"

REPO_NAME="${REPO##*/}"
PREFIX="${REPO_NAME#pim-}"
if [[ -z "$PREFIX" || "$PREFIX" == "$REPO_NAME" ]]; then
  PREFIX="${PIM_REPO_PREFIX:-pim}"
fi
if [[ ! "$TAG" =~ ^${PREFIX}-v([0-9]{2})\.([0-9]{2})\.([0-9]+)$ ]]; then
  die "tag 不符合本仓格式 ${PREFIX}-vYY.MM.N：'${TAG}'"
fi
TAG_YY="${BASH_REMATCH[1]}"
TAG_MM="${BASH_REMATCH[2]}"
TAG_RUN_NUMBER="${BASH_REMATCH[3]}"
MACHINE_VERSION="$((10#$TAG_YY)).${TAG_MM}.$((10#$TAG_RUN_NUMBER))"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-promote-target.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

# gh 2.98 的 release view 支持字段：apiUrl, assets, author, body, createdAt,
# databaseId, id, isDraft, isImmutable, isPrerelease, name, publishedAt,
# tagName, tarballUrl, targetCommitish, uploadUrl, url, zipballUrl。
# isLatest 不在其中 —— 这里只请求真实字段，latest 走 REST。
RELEASE_JSON="$(gh release view "$TAG" --repo "$REPO" \
  --json databaseId,tagName,isPrerelease,isDraft,targetCommitish,assets)" \
  || die "找不到 release：${TAG}"

RELEASE_ID="$(jq -r '.databaseId // empty' <<<"$RELEASE_JSON")"
TAG_NAME="$(jq -r '.tagName // empty' <<<"$RELEASE_JSON")"
IS_DRAFT="$(jq -r 'if has("isDraft") then .isDraft else "missing" end' <<<"$RELEASE_JSON")"
IS_PRE="$(jq -r 'if has("isPrerelease") then .isPrerelease else "missing" end' <<<"$RELEASE_JSON")"
TARGET_COMMITISH="$(jq -r '.targetCommitish // empty' <<<"$RELEASE_JSON")"

[[ "$RELEASE_ID" =~ ^[0-9]+$ ]] || die "release ${TAG} 没有数字 databaseId（收到 '${RELEASE_ID}'）"
[[ "$TAG_NAME" == "$TAG" ]] || die "release 返回的 tagName 不是 '${TAG}'（收到 '${TAG_NAME}'）"
[[ "$IS_DRAFT" == "false" ]] || die "release ${TAG} 仍是 draft，不能提升"
[[ "$IS_PRE" == "true" || "$IS_PRE" == "false" ]] \
  || die "release ${TAG} 的 isPrerelease 不是布尔值（收到 '${IS_PRE}'）"

ASSET_NAMES="$(jq -r '.assets[]?.name // empty' <<<"$RELEASE_JSON")"
require_asset() {
  local pattern="$1" label="$2"
  if ! grep -qE -- "$pattern" <<<"$ASSET_NAMES"; then
    die "release ${TAG} 缺少资产：${label}"
  fi
}
require_asset '^pim-api-v[^/]+\.tar\.gz$' 'pim-api-v*.tar.gz'
require_asset '^pim-api-server-[^/]+\.tar\.gz$' 'pim-api-server-*.tar.gz'
require_asset '^pim-mcp-server-[^/]+\.tar\.gz$' 'pim-mcp-server-*.tar.gz'
if ! grep -qx 'pim-web-version.json' <<<"$ASSET_NAMES"; then
  die "release ${TAG} 缺少资产：pim-web-version.json"
fi

if ! gh release download "$TAG" --repo "$REPO" --pattern pim-web-version.json --dir "$TMP" >/dev/null 2>&1; then
  die "无法下载 release ${TAG} 的 pim-web-version.json"
fi
LEDGER_VERSION="$(jq -r '.version // empty' "$TMP/pim-web-version.json" 2>/dev/null || true)"
LEDGER_API_VERSION="$(jq -r '.apiVersion // empty' "$TMP/pim-web-version.json" 2>/dev/null || true)"
LEDGER_GIT_SHA="$(jq -r '.gitSha // empty' "$TMP/pim-web-version.json" 2>/dev/null || true)"
[[ "$LEDGER_VERSION" == "$VERSION" ]] \
  || die "台账显示版本 '${LEDGER_VERSION}' 与目标版本 '${VERSION}' 不一致（镜像里没烘焙出目标显示版本，或是旧流水线产物）"
[[ "$LEDGER_API_VERSION" == "$MACHINE_VERSION" ]] \
  || die "台账 apiVersion '${LEDGER_API_VERSION}' 与 tag 机器版本 '${MACHINE_VERSION}' 不一致（资产不是这次 run 的台账）"
[[ "$LEDGER_GIT_SHA" =~ ^[0-9a-f]{7}$ ]] \
  || die "台账 gitSha 必须是 7 位小写十六进制（收到 '${LEDGER_GIT_SHA}'）"

REF_JSON="$(gh api "repos/${REPO}/git/ref/tags/${TAG}")" \
  || die "找不到 tag ref：${TAG}"
OBJECT_TYPE="$(jq -r '.object.type // empty' <<<"$REF_JSON")"
TAG_SHA="$(jq -r '.object.sha // empty' <<<"$REF_JSON")"
if [[ "$OBJECT_TYPE" == "tag" ]]; then
  TAG_SHA="$(gh api "repos/${REPO}/git/tags/${TAG_SHA}" | jq -r '.object.sha // empty')" \
    || die "无法解析 annotated tag ${TAG}"
elif [[ "$OBJECT_TYPE" != "commit" ]]; then
  die "tag ${TAG} 指向未知对象类型 '${OBJECT_TYPE}'"
fi
[[ "$TAG_SHA" =~ ^[0-9a-f]{40}$ ]] || die "tag ${TAG} 没有解析出 40 位 commit sha"

# 必须精确绑定到 commit sha。分支名（master）会随 master 移动，无法证明这个
# release 的资产就是该 commit 的构建。
[[ "$TARGET_COMMITISH" =~ ^[0-9a-f]{40}$ ]] \
  || die "release targetCommitish 不是 40 位 commit sha（收到 '${TARGET_COMMITISH}'），无法证明资产与构建提交绑定"
[[ "$TARGET_COMMITISH" == "$TAG_SHA" ]] \
  || die "release targetCommitish ${TARGET_COMMITISH} 与 tag commit ${TAG_SHA} 不一致"

git cat-file -e "${TAG_SHA}^{commit}" 2>/dev/null \
  || die "tag commit ${TAG_SHA} 不在当前 checkout 里（需要 fetch-depth: 0）"
git merge-base --is-ancestor "$TAG_SHA" HEAD \
  || die "tag commit ${TAG_SHA} 不在当前 master 历史里"

HEAD_VERSION="$(head -n 1 VERSION | tr -d '\r' | tr -d '[:space:]')"
[[ "$HEAD_VERSION" == "$VERSION" ]] \
  || die "当前 checkout 的 VERSION 是 '${HEAD_VERSION}'，不是目标版本 '${VERSION}'；release.yml 的 apply 作业必须先同步到当前 master"

VERSION_AT_COMMIT="$(git show "${TAG_SHA}:VERSION" | head -n 1 | tr -d '\r' | tr -d '[:space:]')"
[[ "$VERSION_AT_COMMIT" == "$VERSION" ]] \
  || die "tag commit 的 VERSION 是 '${VERSION_AT_COMMIT}'，不是目标版本 '${VERSION}'"

# tag 的 run number 必须对应这个 commit 上一次成功的 master push 构建。
# headSha 显式再比一次：gh run list --commit 是过滤条件，不作为证明本身。
RUNS_JSON="$(gh run list --repo "$REPO" --workflow ci.yml --commit "$TAG_SHA" --limit 50 \
  --json databaseId,event,headBranch,headSha,conclusion,number,status)" \
  || die "无法读取 commit ${TAG_SHA} 的 CI run"
RUN_JSON="$(jq -c --arg sha "$TAG_SHA" \
  '[.[] | select(.event == "push" and .headBranch == "master" and .headSha == $sha
                 and .status == "completed" and .conclusion == "success")]
   | sort_by(.number) | last // empty' <<<"$RUNS_JSON")"
[[ -n "$RUN_JSON" && "$RUN_JSON" != "null" ]] \
  || die "commit ${TAG_SHA} 没有成功的 master push 构建，不能提升"
RUN_ID="$(jq -r '.databaseId' <<<"$RUN_JSON")"
RUN_NUMBER="$(jq -r '.number' <<<"$RUN_JSON")"
if (( 10#$RUN_NUMBER != 10#$TAG_RUN_NUMBER )); then
  die "tag 里的 run number ${TAG_RUN_NUMBER} 与该 commit 成功构建的 run number ${RUN_NUMBER} 不一致"
fi

RUN_DETAIL="$(gh run view "$RUN_ID" --repo "$REPO" \
  --json databaseId,event,headBranch,headSha,number,status,conclusion)" \
  || die "无法读取 run ${RUN_ID} 的详情"
[[ "$(jq -r '.headSha // empty' <<<"$RUN_DETAIL")" == "$TAG_SHA" ]] \
  || die "run ${RUN_ID} 的 headSha 不是 tag commit ${TAG_SHA}"
[[ "$(jq -r '.event // empty' <<<"$RUN_DETAIL")" == "push" ]] \
  || die "run ${RUN_ID} 不是 push 构建"
[[ "$(jq -r '.headBranch // empty' <<<"$RUN_DETAIL")" == "master" ]] \
  || die "run ${RUN_ID} 不是 master 分支构建"
[[ "$(jq -r '.conclusion // empty' <<<"$RUN_DETAIL")" == "success" ]] \
  || die "run ${RUN_ID} 不是成功构建"
[[ "$(jq -r '.status // empty' <<<"$RUN_DETAIL")" == "completed" ]] \
  || die "run ${RUN_ID} 还未完成"
[[ "$(jq -r '.number // empty' <<<"$RUN_DETAIL")" == "$RUN_NUMBER" ]] \
  || die "run ${RUN_ID} 的 number 与列表结果不一致"

tag_rank() { # <tag> → 零填充的 year/month/run key
  local raw="$1" base y4 mm num
  base="${raw%%+*}"
  if [[ "$base" =~ ^[a-z]+-v([0-9]{4})\.([0-9]{2})\.([0-9]+)$ ]]; then
    y4="${BASH_REMATCH[1]}"; mm="${BASH_REMATCH[2]}"; num="${BASH_REMATCH[3]}"
  elif [[ "$base" =~ ^[a-z]+-v([0-9]{2})\.([0-9]{2})\.([0-9]+)$ ]]; then
    y4="20${BASH_REMATCH[1]}"; mm="${BASH_REMATCH[2]}"; num="${BASH_REMATCH[3]}"
  elif [[ "$base" =~ ^v([0-9]{4})\.([0-9]{2})\.([0-9]+)$ ]]; then
    y4="${BASH_REMATCH[1]}"; mm="${BASH_REMATCH[2]}"; num="${BASH_REMATCH[3]}"
  elif [[ "$base" =~ ^v([0-9]{2})\.([0-9]{2})\.([0-9]+)$ ]]; then
    y4="20${BASH_REMATCH[1]}"; mm="${BASH_REMATCH[2]}"; num="${BASH_REMATCH[3]}"
  else
    return 1
  fi
  printf '%04d%02d%012d' "$((10#$y4))" "$((10#$mm))" "$((10#$num))"
}

version_lt() { # a < b (semver x.y.z)
  local a="$1" b="$2" i
  local -a av bv
  IFS=. read -ra av <<<"$a"
  IFS=. read -ra bv <<<"$b"
  for i in 0 1 2; do
    if (( 10#${av[$i]} < 10#${bv[$i]} )); then return 0; fi
    if (( 10#${av[$i]} > 10#${bv[$i]} )); then return 1; fi
  done
  return 1
}

latest_display_version() { # <latest-json> <latest-tag>
  local json="$1" target v
  local title body
  title="$(jq -r '.name // empty' "$json")"
  # 只认 release.yml 写入的 "PIM API vX.Y.Z (tag)"，不要把 tag 本身的
  # CalVer（api-v26.11.42）误当成显示版本。
  if [[ "$title" =~ PIM\ API\ v([0-9]+\.[0-9]+\.[0-9]+) ]]; then
    echo "${BASH_REMATCH[1]}"; return 0
  fi
  body="$(jq -r '.body // empty' "$json")"
  if [[ "$body" =~ 显示版本[^\`]*\`([0-9]+\.[0-9]+\.[0-9]+)\` ]]; then
    echo "${BASH_REMATCH[1]}"; return 0
  fi
  if [[ "$body" =~ PIM\ API\ v([0-9]+\.[0-9]+\.[0-9]+) ]]; then
    echo "${BASH_REMATCH[1]}"; return 0
  fi
  target="$(jq -r '.target_commitish // empty' "$json")"
  if [[ "$target" =~ ^[0-9a-f]{40}$ ]] && git cat-file -e "${target}^{commit}" 2>/dev/null; then
    v="$(git show "${target}:VERSION" 2>/dev/null | head -n 1 | tr -d '\r' | tr -d '[:space:]' || true)"
    if [[ "$v" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
      echo "$v"; return 0
    fi
  fi
  return 1
}

IS_LATEST="false"
LATEST_JSON_FILE="$TMP/latest.json"
LATEST_ERR_FILE="$TMP/latest.err"
if gh api "repos/${REPO}/releases/latest" >"$LATEST_JSON_FILE" 2>"$LATEST_ERR_FILE"; then
  LATEST_ID="$(jq -r '.id // empty' "$LATEST_JSON_FILE")"
  LATEST_TAG="$(jq -r '.tag_name // empty' "$LATEST_JSON_FILE")"
  LATEST_PRE="$(jq -r 'if has("prerelease") then .prerelease else "missing" end' "$LATEST_JSON_FILE")"
  LATEST_DRAFT="$(jq -r 'if has("draft") then .draft else "missing" end' "$LATEST_JSON_FILE")"
  [[ "$LATEST_ID" =~ ^[0-9]+$ && -n "$LATEST_TAG" ]] \
    || die "/releases/latest 返回了不完整的 release 对象（fail-closed）"
  [[ "$LATEST_PRE" == "false" && "$LATEST_DRAFT" == "false" ]] \
    || die "/releases/latest 返回了 pre-release/draft（API 语义异常，fail-closed）"

  if [[ "$LATEST_ID" == "$RELEASE_ID" ]]; then
    IS_LATEST="true"
  else
    CANDIDATE_RANK="$(tag_rank "$TAG")" || die "无法解析目标 tag 的 CalVer：${TAG}"
    LATEST_RANK="$(tag_rank "$LATEST_TAG")" \
      || die "无法解析当前 latest tag '${LATEST_TAG}' 的 CalVer；不能证明提升不会回退 latest（fail-closed）"
    if [[ "$CANDIDATE_RANK" < "$LATEST_RANK" || "$CANDIDATE_RANK" == "$LATEST_RANK" ]]; then
      die "目标 tag ${TAG} 不比当前 latest ${LATEST_TAG} 新；提升会让 latest 回退到旧构建"
    fi
    LATEST_VERSION="$(latest_display_version "$LATEST_JSON_FILE" "$LATEST_TAG")" \
      || die "无法从 latest release ${LATEST_TAG} 证明其显示版本；不能证明提升不会造成显示版本回退（fail-closed）"
    if version_lt "$VERSION" "$LATEST_VERSION"; then
      die "目标显示版本 ${VERSION} 低于当前 latest 的 ${LATEST_VERSION}；提升会把 latest 回退"
    fi
    echo "latest 检查通过：${LATEST_TAG}（v${LATEST_VERSION}）→ ${TAG}（v${VERSION}）" >&2
  fi
elif grep -qE '\(HTTP 404\)|"status"[[:space:]]*:[[:space:]]*404' \
    "$LATEST_JSON_FILE" "$LATEST_ERR_FILE" 2>/dev/null; then
  echo "当前没有 latest release（404），本次提升会成为 latest" >&2
else
  die "无法读取 /releases/latest（fail-closed，不把 API 错误当成『没有 latest』）：$(tr '\n' ' ' < "$LATEST_ERR_FILE")"
fi

emit() {
  printf '%s=%s\n' "$1" "$2"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    printf '%s=%s\n' "$1" "$2" >> "$GITHUB_OUTPUT"
  fi
}
emit tag "$TAG"
emit sha "$TAG_SHA"
emit run_id "$RUN_ID"
emit run_number "$RUN_NUMBER"
emit is_prerelease "$IS_PRE"
emit is_latest "$IS_LATEST"
echo "promote target OK: tag=${TAG} sha=${TAG_SHA} run=${RUN_NUMBER} prerelease=${IS_PRE} latest=${IS_LATEST}" >&2
