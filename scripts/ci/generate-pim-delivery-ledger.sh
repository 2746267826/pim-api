#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "用法：$0 <PIM功能表.md> <输出台账.md>" >&2
  exit 2
fi

source_table=$1
output_file=$2

mkdir -p "$(dirname "$output_file")"

awk -F'|' '
function trim(value) {
  gsub(/^[ \t]+|[ \t]+$/, "", value)
  return value
}
function platform_for(group) {
  if (group == "L") return "Android；API/Web 衔接"
  if (group == "M") return "Windows；API/Web 衔接"
  if (group == "N") return "Web；API"
  if (group == "C") return "API；Android；Windows（Web 仅查看）"
  if (group == "D") return "API；Android；Windows；Web"
  if (group == "P") return "API；Android；Windows；Web"
  if (group == "O") return "API；Android；Windows；Web"
  return "API；Android；Windows；Web"
}
function agent_for(group) {
  if (group == "L") return "Android Agent；父代理协调"
  if (group == "M") return "Windows Agent；父代理协调"
  if (group == "N") return "API/Web Agent；父代理协调"
  if (group == "R") return "API/MCP Agent；父代理协调"
  return "父代理协调；按适用平台派工"
}
function state_for(mark) {
  if (mark ~ /x/) return "已有实现，按当前版本复验"
  if (mark ~ /~/) return "部分实现，补齐缺口后复验"
  if (mark ~ /\?/) return "需求或实现仍需核对"
  return "未实现，待排期与验收"
}
function decision_for(id) {
  if (id == "B15") return "原决议：不做长笔记性能验收"
  if (id == "C21") return "原决议：暂缓实时转写"
  if (id == "O04") return "原决议：跳过 OPPO 笔记动画和字体"
  if (id == "Q02") return "原决议：当前不做全量备份与导出"
  if (id == "B07") return "原决议：图片移动不做，采用复制、剪切、粘贴"
  if (id == "C14") return "原决议：并入 C12，由直接采集 Opus/Ogg 取代独立 WAV 转换"
  return "按交付包确认范围执行"
}
function next_for(id, mark) {
  if (id == "B15" || id == "O04" || id == "Q02") return "保留原决议，记录边界，不排入当前实施"
  if (id == "C21") return "保留暂缓决议，不排入当前实施"
  if (id == "B07") return "实现图片块操作，移动按复制/剪切/粘贴验收"
  if (id == "C14") return "随 C12 验收 Opus/Ogg 链路，不单独实现 WAV 转换"
  return state_for(mark)
}
BEGIN {
  print "# PIM 全量交付台账"
  print ""
  print "生成日期：2026-11-23。范围：交付包 A–R、Z，共 160 项。"
  print ""
  print "本台账以当前仓库源码、实际运行流程、契约导出、PR CI 和用户验收证据为准。交付包中的 demo、历史源码核对和旧版本发布材料只作为线索，不能单独把项目记为通过。"
  print ""
  print "状态值使用 `待盘点`、`进行中`、`PASS`、`FAIL`、`BLOCKED`、`NOT-VERIFIED`、`APPROVED-EXCEPTION`。跳过和暂缓沿用交付包原决议，只有用户批准的范围例外才使用 `APPROVED-EXCEPTION`。"
  print ""
  print "## 当前批次"
  print ""
  print "| 批次 | 场景 | 参与仓库 | 依赖 | 状态 | 证据 |"
  print "| --- | --- | --- | --- | --- | --- |"
  print "| 1 | 笔记结束、材料逐项回执、服务端 ASR/整理双链路、换端恢复 | pim-api、pim-android、pim-windows | 当前 API 契约 e1eccd55；客户端各自 master 基线 | 进行中 | 首批审计工单已派发，等待真实流程与 CI 回执 |"
  print ""
  print "## 首批工单"
  print ""
  print "| 工单 | 改动仓/审计仓 | 范围 | 依赖提交 | 验收场景 | 交回证据 |"
  print "| --- | --- | --- | --- | --- | --- |"
  print "| API-1 | pim-api | API/Web 服务器级自动处理设置、双链路幂等编排、状态聚合、材料完成后触发 | pim-api e1eccd55 | 自动关闭待发起；配置齐备时 ASR/整理双任务同时入队；无录音仅整理；owner 隔离；状态可恢复 | commit、聚焦 API 测试、真实 Test Swagger、规范化 contract、Web 生成/check、PR/CI |"
  print "| AUDIT-API-1 | pim-api | 笔记结束、材料逐项回执、双链路、换端恢复当前版本审计 | pim-api e1eccd55 | 当前源码、测试、契约和用户流程分别判定 PASS/PARTIAL/BLOCKED/NOT-VERIFIED | 文件/命令、当前 commit、PR/CI、未验证边界 |"
  print "| AUDIT-ANDROID-1 | pim-android | 首批结束、材料回执、后台队列、服务端状态、换端恢复审计 | Android 7bcedb8；API e1eccd55 | 本机材料保全、断网续传、同 noteId 读取、冲突/恢复 | 文件/命令、当前 commit、客户端 CI/设备边界 |"
  print "| AUDIT-WINDOWS-1 | pim-windows | 首批结束、录音封口、材料回执、后台队列、换端恢复审计 | Windows 3baea3c；API e1eccd55 | 原始材料保全、回执恢复、服务端状态、版本顺序/冲突 | 文件/命令、当前 commit、客户端 CI/实机边界 |"
  print "| AUDIT-API-AK | pim-api | A-K 全量源码与证据盘点 | pim-api e1eccd55 | 逐项适用平台、确认条件、当前实现、依赖和下一工单 | 逐项状态、精确文件/命令、PR/commit、NOT-VERIFIED 边界 |"
  print "| AUDIT-API-Q-RZ | pim-api | Q01-Q08、R01-R05、Z01-Z02 全量源码与证据盘点 | pim-api e1eccd55 | MCP/API 合同、服务器中心边界、基础服务安全和恢复 | 逐项状态、精确文件/命令、PR/commit、未验证边界 |"
  print "| AUDIT-ANDROID-LPQ | pim-android | L01-L14、P01-P05、Q01-Q08 盘点 | Android 7bcedb8；API e1eccd55 | 原生入口、权限、采集、基础服务、契约 freshness、设备边界 | 逐项状态、测试/发布/设备证据 |"
  print "| AUDIT-WINDOWS-MNPQRZ | pim-windows | M01-M06、N01-N03、P01-P05、Q01-Q08、R01-R05、Z01-Z02 盘点 | Windows 3baea3c；API e1eccd55 | 原生工作区、扩展、契约生成、托盘、录音、权限和用户流程 | 逐项状态、测试/CI/实机边界 |"
  print ""
  print "## 全量清单"
  print ""
  print "| 项目 | 原始状态 | 适用平台 | 范围决议 | 验收条件 | 当前实现 | 依赖 | 负责 Agent | PR | 证据 | 下一步 |"
  print "| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |"
}
/^## [A-R] / {
  heading = trim(substr($0, 4))
  group = substr(heading, 1, 1)
  next
}
/^## Z / {
  heading = trim(substr($0, 4))
  group = "Z"
  next
}
/^\| \[[x~? ]\]/ {
  mark = trim($2)
  id = trim($3)
  feature = trim($4)
  source = trim($5)
  current = trim($6)
  gsub(/\|/, "／", feature)
  gsub(/\|/, "／", current)
  gsub(/\|/, "／", source)
  acceptance = "按交付包中 " id " 的确认规则完成端到端用户流程，保留错误、重试、权限、冲突和恢复分支，并以当前版本实际执行证据收口。"
  implementation = "交付包原始功能表（2026-11-05）记录：" current "；当前版本复验待执行。"
  dependency = "服务器唯一事实源、当前 OpenAPI/生成契约及该项目记录列出的前置能力。"
  evidence = "交付包/00-交付说明/原始输入/PIM功能表.md；交付包/01-讨论记录/" id "；当前源码、测试、PR CI 待补。"
  print "|" id " | " mark " | " platform_for(group) " | " decision_for(id) " | " acceptance " | " implementation " | " dependency " | " agent_for(group) " | 待创建 | " evidence " | " next_for(id, mark) " |"
}
END {
  print ""
  print "## 复验与交付规则"
  print ""
  print "- 已有功能必须在当前提交或当前发布版本上重新执行对应用户流程，旧版本 APK、历史 CI、demo 或源码存在不单独构成通过证据。"
  print "- API/Web 同仓的接口或状态形状变化必须同步导出并规范化 `contract/openapi.json`，再刷新客户端生成契约。"
  print "- Android 和 Windows 只从已合并 API 契约对应提交刷新，客户端 PR、CI、发布版本和实际流程单独记账。"
  print "- 每个批次必须记录工单范围、依赖提交、验收场景、实际命令或设备流程、PR、CI 和未验证边界。"
  print "- B15、图片移动、C21、O04、Q02 沿用交付包原决议。它们不会被误写成已完成，除非用户重新批准范围。"
}
' "$source_table" > "$output_file"
