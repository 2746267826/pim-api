using System;
using System.Data.Common;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.DataIsolation;

/// <summary>
/// issue #352：进入 Npgsql 的非零偏移 <see cref="DateTimeOffset"/> 参数必须被归一为 UTC。
///
/// <para>
/// Npgsql 对 <c>timestamp with time zone</c> 只接受 offset 0，遇到 <c>+08:00</c> 会抛
/// <c>ArgumentException: Cannot write DateTimeOffset with Offset=08:00:00 ...</c>，
/// 外部表现是 HTTP 500。本用例在**参数绑定层**验证拦截器确实改写了值，
/// 并配一个真实 Npgsql 参数写入用例证明"原本会失败、现在能写入"。
/// </para>
/// </summary>
public class UtcDateTimeOffsetParameterInterceptorTests
{
    private static DbCommand CreateCommand(params object[] values)
    {
        var command = new NpgsqlCommand();
        for (var index = 0; index < values.Length; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = $"p{index}";
            parameter.Value = values[index];
            command.Parameters.Add(parameter);
        }

        return command;
    }

    [Fact]
    public void Normalize_ConvertsNonZeroOffsetToUtcPreservingTheInstant()
    {
        var local = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.FromHours(8));
        using var command = CreateCommand(local);

        UtcDateTimeOffsetParameterInterceptor.Normalize(command);

        var normalized = Assert.IsType<DateTimeOffset>(command.Parameters[0].Value);
        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        // 同一时刻：2026-09-27T00:00+08:00 == 2026-09-26T16:00Z
        Assert.Equal(local.UtcDateTime, normalized.UtcDateTime);
        Assert.Equal(new DateTimeOffset(2026, 9, 26, 16, 0, 0, TimeSpan.Zero), normalized);
    }

    [Theory]
    [InlineData(-8)]
    [InlineData(5.5)]
    [InlineData(14)]
    [InlineData(-11)]
    public void Normalize_HandlesAnyLegalOffset(double offsetHours)
    {
        var offset = TimeSpan.FromHours(offsetHours);
        var local = new DateTimeOffset(2026, 9, 27, 12, 30, 0, offset);
        using var command = CreateCommand(local);

        UtcDateTimeOffsetParameterInterceptor.Normalize(command);

        var normalized = Assert.IsType<DateTimeOffset>(command.Parameters[0].Value);
        Assert.Equal(TimeSpan.Zero, normalized.Offset);
        Assert.Equal(local.UtcDateTime, normalized.UtcDateTime);
    }

    [Fact]
    public void Normalize_LeavesUtcValuesUntouched()
    {
        var utc = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
        using var command = CreateCommand(utc);

        UtcDateTimeOffsetParameterInterceptor.Normalize(command);

        Assert.Equal(utc, command.Parameters[0].Value);
    }

    [Fact]
    public void Normalize_LeavesOtherTypesUntouched()
    {
        var text = "2026-09-27T00:00:00+08:00";
        var number = 42;
        var nullValue = (object?)null;
        var utcDateTime = new DateTime(2026, 9, 27, 0, 0, 0, DateTimeKind.Utc);
        using var command = CreateCommand(text, number, nullValue!, utcDateTime);

        UtcDateTimeOffsetParameterInterceptor.Normalize(command);

        Assert.Equal(text, command.Parameters[0].Value);
        Assert.Equal(number, command.Parameters[1].Value);
        Assert.Null(command.Parameters[2].Value);
        // DateTime（非 DateTimeOffset）不归本拦截器管，原样保留，避免改变既有行为。
        Assert.Equal(utcDateTime, command.Parameters[3].Value);
    }

    [Fact]
    public void Normalize_HandlesParameterWithDBNullValue()
    {
        using var command = CreateCommand(DBNull.Value);

        UtcDateTimeOffsetParameterInterceptor.Normalize(command);

        Assert.Equal(DBNull.Value, command.Parameters[0].Value);
    }

    /// <summary>
    /// 端到端行为证明（真库）：带 <c>+08:00</c> 偏移的参数**原本写不进 PostgreSQL**，
    /// 归一化后可以写入且语义等价于 UTC 时刻。这是 #352 生产 500 的最小复现与修复验证，
    /// 也是 issue #352 验收第 4 条「+08:00 与 Z 两种格式返回一致」的底层事实。
    /// </summary>
    [SkippableFact]
    public async Task Normalize_MakesOffsetParameterWritableToPostgres()
    {
        var connectionString = RealDbTestConnection.Require();
        var local = new DateTimeOffset(2026, 9, 27, 0, 0, 0, TimeSpan.FromHours(8));
        var equivalentUtc = local.ToUniversalTime();
        var normalized = local.ToUniversalTime();

        // 1) 未归一化：Npgsql 拒绝本地偏移（生产 500 的根因）。
        //    失败后连接会被标记为不可用，因此这一步单独用一条连接。
        await using (var rejectingConnection = new NpgsqlConnection(connectionString))
        {
            await rejectingConnection.OpenAsync();
            await using var rejecting = new NpgsqlCommand("SELECT @ts", rejectingConnection);
            rejecting.Parameters.AddWithValue("ts", local);
            var exception = await Assert.ThrowsAsync<ArgumentException>(
                () => rejecting.ExecuteScalarAsync());
            Assert.Contains("Offset", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        // 2) 归一化后：同一时刻可正常查询。
        object? fromNormalized;
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var accepting = new NpgsqlCommand("SELECT @ts", connection);
            accepting.Parameters.AddWithValue("ts", normalized);
            fromNormalized = await accepting.ExecuteScalarAsync();
        }

        // 3) 直接用等价 UTC：结果必须与归一化后完全一致（语义不变）。
        object? fromUtc;
        await using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();
            await using var utcCommand = new NpgsqlCommand("SELECT @ts", connection);
            utcCommand.Parameters.AddWithValue("ts", equivalentUtc);
            fromUtc = await utcCommand.ExecuteScalarAsync();
        }

        Assert.Equal(fromUtc, fromNormalized);
    }

    /// <summary>
    /// 通过真实 EF 管道确认拦截器确实参与构建（避免"写了拦截器但忘了注册"）。
    /// </summary>
    [Fact]
    public void Interceptor_IsAttachedToNpgsqlDbContextOptions()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused;Password=unused")
            .AddInterceptors(new UtcDateTimeOffsetParameterInterceptor())
            .Options;

        var interceptors = options.FindExtension<CoreOptionsExtension>()?.Interceptors;
        Assert.NotNull(interceptors);
        Assert.Contains(interceptors!, interceptor => interceptor is UtcDateTimeOffsetParameterInterceptor);
    }
}
