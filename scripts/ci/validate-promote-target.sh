#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 校验一个 pre-release tag 是否可以被提升为正式版。
#
# 只相信 tag 名字是不够的。要证明它是「一次已经在 master 上跑过 CI 的构建」：
#   1. tag 形如 <prefix>-vYY.MM.N，N 是该次 CI 的 run number；
#   2. tag 指向的 commit 在当前 checkout 的 master 历史里；
#   3. 该 commit 的仓根 VERSION 等于输入的显示版本；
#   4. 该 commit 上有一个成功的 master push 构建，run number 与 tag 一致；
#   5. release 资产齐全，且镜像台账里的显示版本也等于输入版本。
#
# 已经提升过的正式版也允许通过（提升步骤本身可重入，会再收敛 notes/latest）。
#
# 用法：
#   scripts/ci/validate-promote-target.sh --repo owner/repo --tag api-v26.11.42 --version 1.1.0
#
# 成功时输出 tag/sha/run_id/run_number/is_prerelease/is_latest 到 stdout；
# 有 GITHUB_OUTPUT 时同时写入，供 workflow 直接引用。
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
TAG_RUN_NUMBER="${BASH_REMATCH[3]}"

RELEASE_JSON="$(gh release view "$TAG" --repo "$REPO" \
  --json tagName,isPrerelease,isDraft,isLatest,targetCommitish,assets)" \
  || die "找不到 release：${TAG}"

TAG_NAME="$(jq -r '.tagName // empty' <<<"$RELEASE_JSON")"
[[ "$TAG_NAME" == "$TAG" ]] || die "release 返回的 tagName 不是 '${TAG}'（收到 '${TAG_NAME}'）"
[[ "$(jq -r '.isDraft' <<<"$RELEASE_JSON")" == "false" ]] \
  || die "release ${TAG} 仍是 draft，不能提升"

ASSET_NAMES="$(jq -r '.assets[].name' <<<"$RELEASE_JSON")"
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

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-promote-target.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT
if ! gh release download "$TAG" --repo "$REPO" --pattern pim-web-version.json --dir "$TMP" >/dev/null 2>&1; then
  die "无法下载 release ${TAG} 的 pim-web-version.json"
fi
LEDGER_VERSION="$(jq -r '.version // empty' "$TMP/pim-web-version.json" 2>/dev/null || true)"
[[ "$LEDGER_VERSION" == "$VERSION" ]] \
  || die "台账显示版本 '${LEDGER_VERSION}' 与目标版本 '${VERSION}' 不一致（镜像里没烘焙出目标显示版本）"

REF_JSON="$(gh api "repos/${REPO}/git/ref/tags/${TAG}")" \
  || die "找不到 tag ref：${TAG}"
OBJECT_TYPE="$(jq -r '.object.type // empty' <<<"$REF_JSON")"
TAG_SHA="$(jq -r '.object.sha // empty' <<<"$REF_JSON")"
if [[ "$OBJECT_TYPE" == "tag" ]]; then
  TAG_SHA="$(gh api "repos/${REPO}/git/tags/${TAG_SHA}" | jq -r '.object.sha // empty')"
fi
[[ "$TAG_SHA" =~ ^[0-9a-f]{40}$ ]] || die "tag ${TAG} 没有解析出 40 位 commit sha"

TARGET_COMMITISH="$(jq -r '.targetCommitish // empty' <<<"$RELEASE_JSON")"
if [[ "$TARGET_COMMITISH" =~ ^[0-9a-f]{40}$ && "$TARGET_COMMITISH" != "$TAG_SHA" ]]; then
  die "release targetCommitish ${TARGET_COMMITISH} 与 tag commit ${TAG_SHA} 不一致"
fi

git cat-file -e "${TAG_SHA}^{commit}" 2>/dev/null \
  || die "tag commit ${TAG_SHA} 不在当前 checkout 里（需要 fetch-depth: 0）"
git merge-base --is-ancestor "$TAG_SHA" HEAD \
  || die "tag commit ${TAG_SHA} 不在当前 master 历史里"

VERSION_AT_COMMIT="$(git show "${TAG_SHA}:VERSION" | head -n 1 | tr -d '\r' | tr -d '[:space:]')"
[[ "$VERSION_AT_COMMIT" == "$VERSION" ]] \
  || die "tag commit 的 VERSION 是 '${VERSION_AT_COMMIT}'，不是目标版本 '${VERSION}'"

RUNS_JSON="$(gh run list --repo "$REPO" --workflow ci.yml --commit "$TAG_SHA" --limit 50 \
  --json databaseId,event,headBranch,conclusion,number)" \
  || die "无法读取 commit ${TAG_SHA} 的 CI run"
RUN_JSON="$(jq -c \
  '[.[] | select(.event == "push" and .headBranch == "master" and .conclusion == "success")] | sort_by(.number) | last // empty' \
  <<<"$RUNS_JSON")"
[[ -n "$RUN_JSON" && "$RUN_JSON" != "null" ]] \
  || die "commit ${TAG_SHA} 没有成功的 master push 构建，不能提升"
RUN_ID="$(jq -r '.databaseId' <<<"$RUN_JSON")"
RUN_NUMBER="$(jq -r '.number' <<<"$RUN_JSON")"
if (( 10#$RUN_NUMBER != 10#$TAG_RUN_NUMBER )); then
  die "tag 里的 run number ${TAG_RUN_NUMBER} 与该 commit 成功构建的 run number ${RUN_NUMBER} 不一致"
fi

IS_PRE="$(jq -r '.isPrerelease' <<<"$RELEASE_JSON")"
IS_LATEST="$(jq -r '.isLatest' <<<"$RELEASE_JSON")"

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
