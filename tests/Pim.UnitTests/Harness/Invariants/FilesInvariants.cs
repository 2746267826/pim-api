using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Pim.UnitTests.Harness.Invariants;

/// <summary>
/// 文件模块不变量定义
/// 每条不变量均为可量化断言，用于属性测试与回归校验
/// </summary>
public static class FilesInvariants
{
    /// <summary>
    /// INV-F01: 索引去重 —— 同一文件同一版本多次索引后，Qdrant PointId / Chunk 去重后数量 == 去重前 distinct 数量
    /// threshold: 重复 PointId 数阈值 0，tolerance: 0 条重复即 FAIL
    /// 不变量: distinct(PointId) == total && 重复提交后 chunkCount 不增长
    /// </summary>
    public static (bool pass, string detail) CheckIndexingDedup(
        List<(Guid fileItemId, Guid versionId, int chunkIndex, string pointId)> chunks,
        int tolerance = 0)
    {
        var duplicateGroups = chunks.GroupBy(c => c.pointId).Where(g => g.Count() > 1).ToList();
        if (duplicateGroups.Count > tolerance)
        {
            var worst = duplicateGroups.OrderByDescending(g => g.Count()).First();
            return (false,
                $"INV-F01 FAIL: pointId {worst.Key} duplicated {worst.Count()} times > tolerance {tolerance} threshold 0 duplicates");
        }

        // 同一 (fileItemId, versionId, chunkIndex) 不应产生不同 pointId
        var compositeDupes = chunks.GroupBy(c => (c.fileItemId, c.versionId, c.chunkIndex)).Where(g => g.Select(x => x.pointId).Distinct().Count() > 1).ToList();
        if (compositeDupes.Count > tolerance)
        {
            var worst = compositeDupes.First();
            return (false,
                $"INV-F01 FAIL: composite key {worst.Key.fileItemId:N}/{worst.Key.versionId:N}/{worst.Key.chunkIndex} maps to {worst.Select(x => x.pointId).Distinct().Count()} pointIds > tolerance {tolerance} threshold 1");
        }

        // 重复索引后数量应等于首次索引数量（调用方传入两次结果对比）
        var distinct = chunks.Select(c => c.pointId).Distinct().Count();
        if (distinct != chunks.Count && duplicateGroups.Count > 0)
        {
            return (false,
                $"INV-F01 FAIL: chunks {chunks.Count} != distinct {distinct} threshold distinct==total tolerance {tolerance}");
        }

        return (true, "INV-F01 PASS");
    }

    /// <summary>
    /// INV-F02: 向量维度 384 且归一化 —— 任意 embedding 向量长度 == 384 且 L2 范数在 [0.99, 1.01] 或零向量
    /// threshold: Dimensions == 384 (±0 必须精确)，normTolerance = 0.01
    /// 不变量: vector.Length == 384 && (|norm - 1| &lt;= 0.01 || norm == 0)
    /// 阈值来源: HashingFileEmbeddingService.DefaultDimensions / OpenAiFileEmbeddingService Dimensions
    /// </summary>
    public static (bool pass, string detail) CheckEmbeddingDimensions(
        List<float[]> vectors,
        int expectedDimensions = 384,
        double normTolerance = 0.01)
    {
        foreach (var v in vectors)
        {
            if (v.Length != expectedDimensions)
                return (false, $"INV-F02 FAIL: vector length {v.Length} != expected {expectedDimensions} threshold exact tolerance 0");

            var magnitudeSquared = 0f;
            foreach (var f in v) magnitudeSquared += f * f;
            var magnitude = MathF.Sqrt(magnitudeSquared);
            // 零向量允许（空文本），非零向量必须归一化到 1.0 ± tolerance
            if (magnitudeSquared != 0f && Math.Abs(magnitude - 1.0f) > normTolerance)
                return (false, $"INV-F02 FAIL: vector norm {magnitude:F4} not in [1-{normTolerance},1+{normTolerance}] threshold 384 dims tolerance {normTolerance}");
        }

        return (true, "INV-F02 PASS");
    }

    /// <summary>
    /// INV-F03: 禁用路径不计费 —— path 命中 disabledPaths 时计费字段必须为 0
    /// threshold: billedTokens / billedCost 阈值 0，tolerance: 0 允许计费 0 条超限
    /// 不变量: disabled => billedTokens == 0 && billedCost == 0
    /// </summary>
    public static (bool pass, string detail) CheckDisabledPathNotBilled(
        List<(string path, bool isDisabled, int billedTokens, double billedCost)> items,
        int tolerance = 0)
    {
        var violations = items.Where(i => i.isDisabled && (i.billedTokens != 0 || Math.Abs(i.billedCost) > 1e-9)).ToList();
        if (violations.Count > tolerance)
        {
            var worst = violations.First();
            return (false,
                $"INV-F03 FAIL: disabled path '{worst.path}' billed tokens {worst.billedTokens} cost {worst.billedCost:F4} > threshold 0 tolerance {tolerance}");
        }

        return (true, "INV-F03 PASS");
    }

    /// <summary>
    /// INV-F04: Chunk 哈希一致 —— TextHash == SHA256(Text) 十六进制小写
    /// threshold: 哈希失配数阈值 0，tolerance: 0 条失配即 FAIL
    /// 不变量: hex(SHA256(chunk.Text)) == chunk.TextHash
    /// </summary>
    public static (bool pass, string detail) CheckChunkHashConsistency(
        List<(string text, string textHash)> chunks,
        int tolerance = 0)
    {
        var mismatches = 0;
        foreach (var c in chunks)
        {
            var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(c.text))).ToLowerInvariant();
            if (!string.Equals(expected, c.textHash, StringComparison.OrdinalIgnoreCase))
            {
                mismatches++;
                if (mismatches > tolerance)
                    return (false,
                        $"INV-F04 FAIL: chunk hash mismatch expected {expected} got {c.textHash} mismatches {mismatches} > tolerance {tolerance} threshold 0");
            }
        }

        return (true, "INV-F04 PASS");
    }

    /// <summary>
    /// INV-F05: 索引幂等 —— 同一版本重复索引后 chunk 数量不变且 TextHash 集合不变
    /// threshold: 数量差异阈值 0，tolerance: 0 条差异即 FAIL
    /// 不变量: countAfter == countBefore && setAfter == setBefore
    /// </summary>
    public static (bool pass, string detail) CheckIndexIdempotency(
        int countBefore,
        int countAfter,
        HashSet<string> hashesBefore,
        HashSet<string> hashesAfter,
        int tolerance = 0)
    {
        var countDiff = Math.Abs(countAfter - countBefore);
        if (countDiff > tolerance)
            return (false, $"INV-F05 FAIL: chunk count {countBefore} -> {countAfter} diff {countDiff} > tolerance {tolerance} threshold 0");

        if (!hashesBefore.SetEquals(hashesAfter))
        {
            var added = hashesAfter.Except(hashesBefore).Count();
            var removed = hashesBefore.Except(hashesAfter).Count();
            if (added + removed > tolerance)
                return (false, $"INV-F05 FAIL: hash set changed added {added} removed {removed} > tolerance {tolerance} threshold 0");
        }

        return (true, "INV-F05 PASS");
    }

    /// <summary>
    /// INV-F06: 检索相关性阈值 —— 搜索结果 score 必须在 [minScore, 1] 且按 score 降序排列
    /// threshold: minScore 默认 0.30（Qdrant 余弦相似度最低可信分），tolerance: 1e-9 浮点误差
    /// 不变量: ∀r: minScore - tolerance &lt;= r.score &lt;= 1 + tolerance 且 ranking[i].score &gt;= ranking[i+1].score - tolerance
    /// </summary>
    public static (bool pass, string detail) CheckSearchRelevanceThreshold(
        List<(string pointId, double score)> results,
        double minScore = 0.30,
        double tolerance = 1e-9)
    {
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.score < minScore - tolerance || r.score > 1.0 + tolerance)
                return (false, $"INV-F06 FAIL: point {r.pointId} score {r.score:F4} out of [{minScore:F2},1] threshold [{minScore:F2},1] tolerance {tolerance}");

            if (i > 0 && r.score > results[i - 1].score + tolerance)
                return (false, $"INV-F06 FAIL: ranking not descending at index {i}: {results[i - 1].score:F4} < {r.score:F4} threshold monotonic tolerance {tolerance}");
        }

        return (true, "INV-F06 PASS");
    }

    /// <summary>
    /// INV-F07: 文件版本单调 —— 同一 fileItemId 的 versionNumber 递增且无重复，createdAt 与 versionNumber 同向单调
    /// threshold: 重复/倒序数阈值 0，tolerance: 0 条倒序/重复即 FAIL，createdAt 允许 1秒 容差
    /// 不变量: distinct(versionNumber) == count && versionNumber 单调递增 && createdAt 单调不减（tolerance 1s）
    /// </summary>
    public static (bool pass, string detail) CheckFileVersionMonotonic(
        List<(Guid fileItemId, int versionNumber, DateTimeOffset createdAt)> versions,
        double toleranceSeconds = 1.0)
    {
        var groups = versions.GroupBy(v => v.fileItemId);
        foreach (var g in groups)
        {
            var ordered = g.OrderBy(v => v.versionNumber).ToList();
            var distinct = ordered.Select(v => v.versionNumber).Distinct().Count();
            if (distinct != ordered.Count)
                return (false, $"INV-F07 FAIL: file {g.Key:N} duplicate versionNumber distinct {distinct} != total {ordered.Count} threshold 0 duplicates tolerance 0");

            for (int i = 1; i < ordered.Count; i++)
            {
                if (ordered[i].versionNumber <= ordered[i - 1].versionNumber)
                    return (false, $"INV-F07 FAIL: file {g.Key:N} version not strictly increasing at index {i}: {ordered[i - 1].versionNumber} >= {ordered[i].versionNumber} threshold increasing tolerance 0");

                // createdAt 应与 versionNumber 同向：version 大的 createdAt 不应早于小的超过 tolerance
                if (ordered[i].createdAt < ordered[i - 1].createdAt.AddSeconds(-toleranceSeconds))
                    return (false, $"INV-F07 FAIL: file {g.Key:N} version {ordered[i].versionNumber} createdAt {ordered[i].createdAt:O} < prev {ordered[i - 1].createdAt:O} threshold monotonic tolerance {toleranceSeconds}s");
            }
        }

        return (true, "INV-F07 PASS");
    }
}
