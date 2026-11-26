using Pim.Core.Exceptions;

namespace Pim.Core.Notes;

/// <summary>
/// A native-note MCP operation failed or has an unconfirmed outcome. The attached receipt is
/// safe to return to the caller and lets it reconcile the requestId without exposing internal
/// task, attempt, step, request or log identifiers.
/// </summary>
public sealed class NativeNoteMcpOperationException : DomainException
{
    public NativeNoteMcpOperationException(int errorCode, string message,
        NativeNoteMcpReceiptDto receipt)
        : base(errorCode, message)
    {
        Receipt = receipt;
    }

    public NativeNoteMcpReceiptDto Receipt { get; }
}
