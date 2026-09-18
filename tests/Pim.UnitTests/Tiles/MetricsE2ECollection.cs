using Xunit;

namespace Pim.UnitTests.Tiles;

/// <summary>
/// 依赖进程级 Prometheus 注册表的端到端指标用例，必须**串行**执行。
///
/// prometheus-net 的指标挂在 <c>Metrics.DefaultRegistry</c>（进程级单例），
/// 而 <c>WebApplicationFactory</c> 每个测试宿主都会往同一批计数器里写。
/// 断言"本次请求带来了多少增量"时，任何并行执行的用例只要命中同一路由，
/// 就会污染 before/after 差值，造成随机失败。
///
/// <see cref="CollectionDefinitionAttribute.DisableParallelization"/> 让本 collection
/// 不与**其它 collection** 并行（xUnit 2.8.1 语义：本 collection 独占执行，
/// 它不是程序集级锁，也保护不了**没有**加入本 collection 的同路由用例）。
/// 因此新增"读取 /metrics 并比较增量"且命中同一路由的用例时，必须一并加入本 collection。
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MetricsE2ECollection
{
    public const string Name = "MetricsE2E";
}
