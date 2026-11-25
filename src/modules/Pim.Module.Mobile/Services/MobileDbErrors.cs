using Microsoft.EntityFrameworkCore;

namespace Pim.Module.Mobile.Services;

internal static class MobileDbErrors
{
    /// <summary>
    /// 只把数据库唯一约束冲突识别为业务 6205。其余 DbUpdateException
    /// （连接失败、约束损坏、触发器失败等）必须继续上抛为服务端错误，
    /// 不能被客户端误读成“坐标重复”。
    /// </summary>
    public static bool IsUniqueConstraintViolation(DbUpdateException exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException { SqlState: "23505" })
                return true;

            if (string.Equals(current.GetType().Name, "SqliteException", StringComparison.Ordinal)
                && current.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
