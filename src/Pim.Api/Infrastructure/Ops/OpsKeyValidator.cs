using System.Security.Cryptography;
using System.Text;

namespace Pim.Api.Infrastructure.Ops;

public sealed class OpsKeyValidator
{
    private readonly string[] _keys;

    public OpsKeyValidator(string? configured)
    {
        _keys = (configured ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool HasKeys => _keys.Length > 0;

    public bool IsValid(string? provided)
    {
        if (string.IsNullOrWhiteSpace(provided) || _keys.Length == 0) return false;
        var p = Encoding.UTF8.GetBytes(provided.Trim());
        foreach (var k in _keys)
        {
            var kb = Encoding.UTF8.GetBytes(k);
            if (p.Length == kb.Length && CryptographicOperations.FixedTimeEquals(p, kb)) return true;
        }
        return false;
    }
}
