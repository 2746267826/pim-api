namespace Pim.Core.Data;

/// <summary>
/// 标记「按用户隔离」的实体。实现该接口的实体会在 PimDbContext 上
/// 自动获得全局查询过滤器：业务请求（存在当前用户）只能看到本人的数据；
/// 系统上下文（后台任务、启动引导、无 HttpContext）不受过滤影响。
/// </summary>
public interface IUserOwnedEntity
{
    Guid UserId { get; }
}
