#!/bin/bash
# PIM container log reader - restricted forced command for user pimlog
set -euo pipefail

readonly LOG_DIR="/data/pim/logs"
readonly MAX_BYTES=$(( 10 * 1024 * 1024 ))  # 10 MB per session cap
readonly SESSION_FILE=$(mktemp /tmp/pim-log-session-XXXXXX)
trap 'rm -f "$SESSION_FILE"' EXIT
echo 0 > "$SESSION_FILE"

if [[ -z "${SSH_ORIGINAL_COMMAND:-}" ]]; then
    echo "PIM container log reader (read-only)"
    echo ""
    echo "可用命令:"
    echo "  tail <filename> [lines=N]   — 查看日志文件尾部（默认 50，最大 200）"
    echo "  cat <filename> [lines=N]    — 查看日志文件开头（默认 50，最大 200）"
    echo "  ls [pattern]                — 列出日志目录中的文件"
    echo "  find <keyword> [filename]   — 搜索关键词（默认最近 3 个文件，最大 200 行）"
    echo "  du                          — 查看日志目录总大小（不读取内容）"
    echo ""
    echo "安全限制: 每次会话最多读取 10MB 数据，文件名仅允许字母数字._-"
    echo ""
    echo "示例:"
    echo "  ssh pim@host tail pim-api-20260810.jsonl lines=100"
    echo "  ssh pim@host find 'ERROR'"
    exit 0
fi

IFS=' ' read -ra ARGS <<< "$SSH_ORIGINAL_COMMAND"
CMD="${ARGS[0]}"
REMAINING=("${ARGS[@]:1}")

check_quota() {
    local bytes="$1"
    local used=$(cat "$SESSION_FILE")
    local total=$(( used + bytes ))
    if (( total > MAX_BYTES )); then
        echo "错误: 会话读取量超过限制 (已用: ${used}B, 请求: ${bytes}B, 上限: ${MAX_BYTES}B)" >&2
        exit 1
    fi
    echo "$total" > "$SESSION_FILE"
}

lines_to_bytes() { echo $(( $1 * 2048 )); }

validate_filename() {
    local name="$1"
    if [[ ! "$name" =~ ^[a-zA-Z0-9_.-]+$ ]]; then
        echo "错误: 文件名包含非法字符: ${name}" >&2
        exit 1
    fi
    if [[ "$name" == *"/"* ]]; then
        echo "错误: 不允许使用路径，请在 ${LOG_DIR} 下操作" >&2
        exit 1
    fi
}

validate_lines() {
    local lines="$1"
    if [[ ! "$lines" =~ ^[0-9]+$ ]] || (( lines < 1 )) || (( lines > 200 )); then
        echo "错误: lines 必须为 1-200 的整数" >&2
        exit 1
    fi
}

case "$CMD" in
    tail)
        [[ ${#REMAINING[@]} -ge 1 ]] || { echo "用法: tail <filename> [lines=N]" >&2; exit 1; }
        FILENAME="${REMAINING[0]}"; LINES=50
        [[ ${#REMAINING[@]} -ge 2 ]] && { LINES="${REMAINING[1]#lines=}"; validate_lines "$LINES"; }
        validate_filename "$FILENAME"
        check_quota $(lines_to_bytes "$LINES")
        exec tail -n "$LINES" "${LOG_DIR}/${FILENAME}"
        ;;
    cat)
        [[ ${#REMAINING[@]} -ge 1 ]] || { echo "用法: cat <filename> [lines=N]" >&2; exit 1; }
        FILENAME="${REMAINING[0]}"; LINES=50
        [[ ${#REMAINING[@]} -ge 2 ]] && { LINES="${REMAINING[1]#lines=}"; validate_lines "$LINES"; }
        validate_filename "$FILENAME"
        check_quota $(lines_to_bytes "$LINES")
        exec head -n "$LINES" "${LOG_DIR}/${FILENAME}"
        ;;
    ls)
        check_quota 50000
        if [[ ${#REMAINING[@]} -ge 1 ]]; then
            PATTERN="${REMAINING[0]}"
            if [[ ! "$PATTERN" =~ ^[a-zA-Z0-9_.*-]+$ ]]; then
                echo "错误: pattern 包含非法字符" >&2
                exit 1
            fi
            ls -lh ${LOG_DIR}/${PATTERN} 2>/dev/null || echo "无匹配文件"
        else
            exec ls -lh "${LOG_DIR}/"
        fi
        ;;
    find)
        [[ ${#REMAINING[@]} -ge 1 ]] || { echo "用法: find <keyword> [filename]" >&2; exit 1; }
        KEYWORD="${REMAINING[0]}"; MAX_MATCHES=200
        if [[ ${#REMAINING[@]} -ge 2 ]]; then
            FILENAME="${REMAINING[1]}"; validate_filename "$FILENAME"
            check_quota 524288
            grep -h -i --max-count="$MAX_MATCHES" "$KEYWORD" "${LOG_DIR}/${FILENAME}" 2>/dev/null || echo "无匹配"
        else
            check_quota 1048576
            FILES=($(ls -t "${LOG_DIR}/"*.jsonl 2>/dev/null | head -3))
            for f in "${FILES[@]}"; do
                echo "=== $(basename "$f") ==="
                grep -h -i --max-count="$MAX_MATCHES" "$KEYWORD" "$f" 2>/dev/null || echo "(无匹配)"
            done
        fi
        ;;
    du)
        check_quota 50000
        exec du -sh "${LOG_DIR}/"
        ;;
    *)
        echo "错误: 未知命令: ${CMD}" >&2
        echo "可用命令: tail, cat, ls, find, du" >&2
        exit 1
        ;;
esac
