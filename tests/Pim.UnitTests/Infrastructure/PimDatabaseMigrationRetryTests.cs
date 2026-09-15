using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.InfrastructureCoverage;

/// <summary>
/// 启动期迁移重试策略：DB 慢启动要能自愈，但真正失败必须把异常抛出去让进程退出，
/// 不能再降级成一个 Warning 后带病启动（B2）。
/// </summary>
public sealed class PimDatabaseMigrationRetryTests
{
    [Fact]
    public async Task ExecuteAsync_SucceedsOnFirstAttemptWithoutRetrying()
    {
        var attempts = 0;
        var retries = new List<int>();

        await PimDatabaseMigrationRetry.ExecuteAsync(
            _ =>
            {
                attempts++;
                return Task.CompletedTask;
            },
            onRetry: (_, attempt) =>
            {
                retries.Add(attempt);
                return Task.CompletedTask;
            },
            delay: TimeSpan.Zero);

        Assert.Equal(1, attempts);
        Assert.Empty(retries);
    }

    [Fact]
    public async Task ExecuteAsync_RetriesUntilSuccessAndReportsAttemptNumbers()
    {
        var attempts = 0;
        var retries = new List<(int Attempt, string Message)>();

        await PimDatabaseMigrationRetry.ExecuteAsync(
            _ =>
            {
                attempts++;
                if (attempts < 3)
                    throw new InvalidOperationException($"boom-{attempts}");
                return Task.CompletedTask;
            },
            onRetry: (ex, attempt) =>
            {
                retries.Add((attempt, ex.Message));
                return Task.CompletedTask;
            },
            maxAttempts: 5,
            delay: TimeSpan.Zero);

        Assert.Equal(3, attempts);
        Assert.Equal([(1, "boom-1"), (2, "boom-2")], retries);
    }

    [Fact]
    public async Task ExecuteAsync_ThrowsTheLastExceptionWhenAttemptsAreExhausted()
    {
        var attempts = 0;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            PimDatabaseMigrationRetry.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    throw new InvalidOperationException($"boom-{attempts}");
                },
                maxAttempts: 3,
                delay: TimeSpan.Zero));

        Assert.Equal(3, attempts);
        Assert.Equal("boom-3", error.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotRetryWhenCancelled()
    {
        using var cts = new CancellationTokenSource();
        var attempts = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PimDatabaseMigrationRetry.ExecuteAsync(
                _ =>
                {
                    attempts++;
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                },
                maxAttempts: 5,
                delay: TimeSpan.Zero,
                ct: cts.Token));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsNonPositiveMaxAttempts()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            PimDatabaseMigrationRetry.ExecuteAsync(_ => Task.CompletedTask, maxAttempts: 0));
    }
}
