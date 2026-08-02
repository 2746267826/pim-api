#!/bin/bash
# PIM Production Log Reader — restricted command wrapper
# Deploy to /usr/local/bin/pim-log-reader.sh on the production server
# Used as the forced command in authorized_keys via command= directive
#
# Only allows read-only access to:
#   - /data/pim/logs/*.jsonl  (tail/cat/ls/head)
#   - systemd journal (journalctl --since=... --no-pager)
#   - du -sh for total size check (NOT actual file content)
#
# Total transfer cap: reads at most MAX_BYTES total across all files in one session.
# This prevents accidental or intentional bulk download.

set -euo pipefail

readonly LOG_DIR="/data/pim/logs"
readonly MAX_BYTES=$(( 10 * 1024 * 1024 ))  # 10 MB per session cap
readonly SESSION_FILE=$(mktemp /tmp/pim-log-session-XXXXXX)
trap 'rm -f "$SESSION_FILE"' EXIT
echo 0 > "$SESSION_FILE"

# Parse the original SSH command (passed via SSH_ORIGINAL_COMMAND)
# If empty, show usage
if [[ -z "${SSH_ORIGINAL_COMMAND:-}" ]]; then
    echo "PIM Production Log Reader (read-only)"
    echo ""
    echo "可用命令:"
    echo "  tail <filename> [lines=N]     — 查看日志文件尾部（默认 50 行，最大 200）"
    echo "  cat <filename> [lines=N]       — 查看日志文件开头（默认 50 行，最大 200）"
    echo "  ls [pattern]                   — 列出日志目录中的文件"
    echo "  journal [since] [lines=N]      — 查看 systemd journal（since 格式: '30 min ago', '2h ago', '2024-01-01'）"
    echo "  du                             — 查看日志目录总大小（不读取内容）"
    echo "  find <keyword> [filename]      — 在日志文件中搜索关键词（默认最近 3 个文件，最大 200 行）"
    echo ""
    echo "安全限制: 每次会话最多读取 10MB 数据"
    echo ""
    echo "示例:"
    echo "  ssh pim-log-prod tail pim-api-20260802.jsonl lines=100"
    echo "  ssh pim-log-prod journal '2h ago' lines=50"
    echo "  ssh pim-log-prod find 'ERROR' pim-api-20260802.jsonl"
    exit 0
fi

# Parse command and arguments
IFS=' ' read -ra ARGS <<< "$SSH_ORIGINAL_COMMAND"
CMD="${ARGS[0]}"
shift 1 2>/dev/null || true

# Reconstruct remaining args
REMAINING=("${ARGS[@]:1}")

# Helper: check transfer quota
check_quota() {
    local bytes="$1"
    local used=$(cat "$SESSION_FILE")
    local total=$(( used + bytes ))
    if (( total > MAX_BYTES )); then
        echo "错误: 会话读取量超过限制 (已用: ${used}B, 请求: ${bytes}B, 上限: ${MAX_BYTES}B)" >&2
        echo "请使用更精确的过滤条件减少数据量，或开始新会话。" >&2
        exit 1
    fi
    echo "$total" > "$SESSION_FILE"
}

# Helper: estimate bytes from lines
lines_to_bytes() {
    local lines="$1"
    # 保守估计每行 2KB（实际 JSONL 行通常几百字节）
    echo $(( lines * 2048 ))
}

# Helper: resolve filename
resolve_file() {
    local name="$1"
    # 如果文件名包含路径，拒绝
    if [[ "$name" == *"/"* ]]; then
        echo "错误: 不允许使用路径，请在 ${LOG_DIR} 下操作" >&2
        exit 1
    fi
    echo "${LOG_DIR}/${name}"
}

# Validate filename (no path traversal, no special chars)
validate_filename() {
    local name="$1"
    if [[ ! "$name" =~ ^[a-zA-Z0-9_.-]+$ ]]; then
        echo "错误: 文件名包含非法字符: ${name}" >&2
        exit 1
    fi
}

validate_lines() {
    local lines="$1"
    if [[ ! "$lines" =~ ^[0-9]+$ ]] || (( lines < 1 )); then
        echo "错误: lines 必须为正整数" >&2
        exit 1
    fi
    if (( lines > 200 )); then
        echo "错误: lines 最大为 200（防止数据量过大）" >&2
        exit 1
    fi
}

case "$CMD" in
    tail)
        if [[ ${#REMAINING[@]} -lt 1 ]]; then
            echo "用法: tail <filename> [lines=N]" >&2
            exit 1
        fi
        FILENAME="${REMAINING[0]}"
        LINES=50
        if [[ ${#REMAINING[@]} -ge 2 ]]; then
            LINES="${REMAINING[1]#lines=}"
            validate_lines "$LINES"
        fi
        validate_filename "$FILENAME"
        FILEPATH=$(resolve_file "$FILENAME")
        if [[ ! -f "$FILEPATH" ]]; then
            echo "错误: 文件不存在: ${FILEPATH}" >&2
            ls -1 "${LOG_DIR}/" 2>/dev/null
            exit 1
        fi
        check_quota $(lines_to_bytes "$LINES")
        exec tail -n "$LINES" "$FILEPATH"
        ;;

    cat)
        if [[ ${#REMAINING[@]} -lt 1 ]]; then
            echo "用法: cat <filename> [lines=N]" >&2
            exit 1
        fi
        FILENAME="${REMAINING[0]}"
        LINES=50
        if [[ ${#REMAINING[@]} -ge 2 ]]; then
            LINES="${REMAINING[1]#lines=}"
            validate_lines "$LINES"
        fi
        validate_filename "$FILENAME"
        FILEPATH=$(resolve_file "$FILENAME")
        if [[ ! -f "$FILEPATH" ]]; then
            echo "错误: 文件不存在: ${FILEPATH}" >&2
            ls -1 "${LOG_DIR}/" 2>/dev/null
            exit 1
        fi
        check_quota $(lines_to_bytes "$LINES")
        exec head -n "$LINES" "$FILEPATH"
        ;;

    ls)
        if [[ ${#REMAINING[@]} -ge 1 ]]; then
            PATTERN="${REMAINING[0]}"
            # Pattern must be safe: only glob chars
            if [[ ! "$PATTERN" =~ ^[a-zA-Z0-9_.*-]+$ ]]; then
                echo "错误: pattern 包含非法字符" >&2
                exit 1
            fi
            # Quick check: ls output is small, < 1MB
            check_quota 50000
            exec ls -lh "${LOG_DIR}/${PATTERN}" 2>/dev/null || { echo "无匹配文件"; exit 0; }
        else
            check_quota 50000
            exec ls -lh "${LOG_DIR}/"
        fi
        ;;

    journal)
        SINCE="24h ago"
        LINES=50
        SINCE_ARGS=()
        # Extract lines=N from args; join the rest as the since value (may contain spaces)
        for arg in "${REMAINING[@]}"; do
            if [[ "$arg" == lines=* ]]; then
                LINES="${arg#lines=}"
                validate_lines "$LINES"
            else
                SINCE_ARGS+=("$arg")
            fi
        done
        if [[ ${#SINCE_ARGS[@]} -gt 0 ]]; then
            SINCE="${SINCE_ARGS[*]}"
        fi
        check_quota $(lines_to_bytes "$LINES")
        exec journalctl --since "$SINCE" --no-pager -n "$LINES" --unit=pim-api.service 2>/dev/null || \
            exec journalctl --since "$SINCE" --no-pager -n "$LINES" 2>/dev/null
        ;;

    du)
        check_quota 50000
        exec du -sh "${LOG_DIR}/"
        ;;

    find)
        if [[ ${#REMAINING[@]} -lt 1 ]]; then
            echo "用法: find <keyword> [filename]" >&2
            exit 1
        fi
        KEYWORD="${REMAINING[0]}"
        MAX_MATCHES=200
        if [[ ${#REMAINING[@]} -ge 2 ]]; then
            FILENAME="${REMAINING[1]}"
            validate_filename "$FILENAME"
            FILEPATH="${LOG_DIR}/${FILENAME}"
            if [[ ! -f "$FILEPATH" ]]; then
                echo "错误: 文件不存在: ${FILEPATH}" >&2
                exit 1
            fi
            check_quota 524288  # 512KB for search
            exec grep -h -i --max-count="$MAX_MATCHES" "$KEYWORD" "$FILEPATH" 2>/dev/null || echo "无匹配"
        else
            # Search recent 3 files
            check_quota 1048576  # 1MB for multi-file search
            FILES=($(ls -t "${LOG_DIR}/"*.jsonl 2>/dev/null | head -3))
            for f in "${FILES[@]}"; do
                echo "=== $(basename "$f") ==="
                grep -h -i --max-count="$MAX_MATCHES" "$KEYWORD" "$f" 2>/dev/null || echo "(无匹配)"
            done
        fi
        ;;

    *)
        echo "错误: 未知命令: ${CMD}" >&2
        echo "可用命令: tail, cat, ls, journal, du, find" >&2
        exit 1
        ;;
esac