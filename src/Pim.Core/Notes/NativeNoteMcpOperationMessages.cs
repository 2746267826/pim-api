namespace Pim.Core.Notes;

/// <summary>
/// Caller-facing copy for native-note requestId reconciliation. These messages deliberately
/// avoid request identifiers and other internal state so the API sanitizer can return them
/// unchanged to ordinary users.
/// </summary>
public static class NativeNoteMcpOperationMessages
{
    public const string ResponseExpired =
        "该操作结果已超过保留期，请先读取最新状态，再使用新的请求标识继续";

    public const string ResponseNoLongerRetained =
        "该操作结果已不再保留，请先读取最新状态，再使用新的请求标识继续";

    public const string IdentityCapacityExceeded =
        "该账号的原生笔记写入幂等记录已达容量上限，暂时无法开始新的写入。" +
        "已有写入的重试与状态查询不受影响；请联系管理员扩容后再试。";
}
