using System;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Pim.Infrastructure.Data;

/// <summary>
/// 把进入 Npgsql 的**非零偏移** <see cref="DateTimeOffset"/> 参数归一为 UTC。
///
/// <para>
/// Npgsql 对 <c>timestamp with time zone</c> 只接受 offset 0 的值，遇到
/// <c>+08:00</c> 这类本地偏移会抛
/// <c>ArgumentException: Cannot write DateTimeOffset with Offset=08:00:00 ... only offset 0 (UTC) is supported</c>，
/// 外部表现就是 HTTP 500。这不是某一条 SQL 的问题，而是**数据库边界上的硬约束**：
/// 任何调用方（外部查询参数、请求体、后台任务、未来新增端点）只要把本地偏移的
/// <see cref="DateTimeOffset"/> 直接交给 EF，就会踩到。
/// </para>
///
/// <para>
/// 历史上有两次同根因缺陷：#313 是服务端自建的本地午夜窗口，修在构造点；
/// #352 是外部 <c>start/end</c> 查询参数，实测 <c>/layers</c>、<c>/events</c>、
/// <c>/export-ics</c>、<c>/mobile/summary</c>、<c>/mobile/timeline</c>、
/// <c>/mobile/location/history</c> 六个端点同时 500。修在构造点的做法会不断漏，
/// 因为约束在边界、归一化却散落在调用点。这里把归一化收到边界本身，
/// 使这一类缺陷在结构上不可能再出现。
/// </para>
///
/// <para>
/// <b>语义安全性</b>：对 <c>timestamptz</c> 而言，<c>2026-09-27T00:00:00+08:00</c> 与
/// <c>2026-09-26T16:00:00Z</c> 表示同一时刻，归一化是**恒等变换**，不改变查询语义
/// （全库核查：不存在任何 <c>timestamp without time zone</c> 列，因此也不存在
/// "归一化会挪动墙上时间" 的场景）。
/// </para>
///
/// <para>
/// 只改写<b>参数值</b>，不碰 SQL 文本，也不吞异常；偏移已为 0 的值原样放行。
/// 其他类型（含 <see cref="DateTime"/>）不在本拦截器范围内。
/// </para>
/// </summary>
public sealed class UtcDateTimeOffsetParameterInterceptor : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        Normalize(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Normalize(command);
        return new ValueTask<InterceptionResult<DbDataReader>>(result);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result)
    {
        Normalize(command);
        return result;
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Normalize(command);
        return new ValueTask<InterceptionResult<object>>(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result)
    {
        Normalize(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Normalize(command);
        return new ValueTask<InterceptionResult<int>>(result);
    }

    /// <summary>就地把所有非零偏移的 <see cref="DateTimeOffset"/> 参数值改写为 UTC。</summary>
    internal static void Normalize(DbCommand command)
    {
        foreach (DbParameter parameter in command.Parameters)
        {
            if (parameter.Value is DateTimeOffset value && value.Offset != TimeSpan.Zero)
            {
                parameter.Value = value.ToUniversalTime();
            }
        }
    }
}
