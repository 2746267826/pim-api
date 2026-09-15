using System.Net.Sockets;
using Npgsql;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// 真库用例的连接约定（与仓库既定标准一致）：
///
/// 1. 只读环境变量 <c>PIM_TEST_CONN</c>，源码里不内置口令；
/// 2. 拿不到可用的库时用 <see cref="Skip"/> **显式跳过**（CI 报告里显示为 Skipped），
///    而不是静默 <c>return</c> —— 静默返回会让"什么都没验证"的用例长期以 Passed 计入，
///    断言也会退化成恒真式，失去回归防护意义。
///
/// 3. 只有"数据库不可达"才跳过；口令错误、库不存在、权限不足等**配置错误必须让用例失败**，
///    否则错配置会被伪装成绿色 Skip。
///
/// 使用方必须是 <c>[SkippableFact]</c>/<c>[SkippableTheory]</c>，否则 Skip 会被当成失败。
/// </summary>
internal static class RealDbTestConnection
{
    /// <summary>环境变量里的连接串（未设置时为 null）。</summary>
    public static string? FromEnvironment => Environment.GetEnvironmentVariable("PIM_TEST_CONN");

    /// <summary>不可用时抛 <see cref="Xunit.SkipException"/>（用例显示为 Skipped）。</summary>
    public static void SkipIfUnavailable()
    {
        var connectionString = FromEnvironment;
        Skip.If(
            string.IsNullOrWhiteSpace(connectionString),
            "RealDb unavailable (PIM_TEST_CONN not set), skipping test.");

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
            using var command = new NpgsqlCommand("SELECT 1", connection);
            command.ExecuteScalar();
        }
        catch (Exception ex) when (IsServerUnreachable(ex))
        {
            throw new Xunit.SkipException($"RealDb unreachable: {ex.Message}");
        }

        // 其余异常（28P01 口令错误、3D000 库不存在、42501 权限不足…）不在此处捕获：
        // 配置错误必须让用例失败，不能被伪装成 Skip。
    }

    /// <summary>
    /// 只有"服务器不可达"（连接被拒 / DNS 失败 / 连接超时 / 连接池耗尽）才算环境缺失；
    /// 其余数据库异常（认证、库不存在、权限、缺表…）属于配置错误，必须向上抛出。
    ///
    /// 判定沿 InnerException 链（有界深度）进行：Npgsql/EF 会把网络异常包多层。
    /// 注意：连接池耗尽表现为 TimeoutException，与"服务器不可达"一样属于环境性失败，
    /// 因此同样降级为 Skip —— 这是有意为之。
    /// </summary>
    internal static bool IsServerUnreachable(Exception? ex)
    {
        for (var depth = 0; ex is not null && depth < 8; depth++, ex = ex.InnerException)
        {
            if (ex is SocketException or TimeoutException)
                return true;
        }

        return false;
    }

    /// <summary>校验可用并返回连接串。</summary>
    public static string Require()
    {
        SkipIfUnavailable();
        return FromEnvironment!;
    }
}
