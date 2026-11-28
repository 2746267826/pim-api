using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pim.Infrastructure.Notifications;

internal static class WebPushCrypto
{
    private static readonly byte[] RecordDelimiter = [0x02];
    private static readonly byte[] InfoPrefix = "WebPush: info\0"u8.ToArray();
    private static readonly byte[] CekInfo = "Content-Encoding: aes128gcm\0"u8.ToArray();
    private static readonly byte[] NonceInfo = "Content-Encoding: nonce\0"u8.ToArray();

    public static byte[] Encrypt(
        ReadOnlySpan<byte> plaintext,
        ReadOnlySpan<byte> userAgentPublicKey,
        ReadOnlySpan<byte> authSecret,
        out byte[] senderPublicKey)
    {
        if (userAgentPublicKey.Length != 65 || userAgentPublicKey[0] != 0x04)
            throw new CryptographicException("Invalid user-agent public key");
        if (authSecret.Length != 16)
            throw new CryptographicException("Invalid auth secret");
        if (plaintext.Length > 3000)
            throw new CryptographicException("Push payload is too large");

        using var sender = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var senderParameters = sender.ExportParameters(false);
        senderPublicKey = EncodeUncompressedPoint(senderParameters);

        using var receiver = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = userAgentPublicKey[1..33].ToArray(),
                Y = userAgentPublicKey[33..65].ToArray()
            }
        });
        var sharedSecret = sender.DeriveKeyMaterial(receiver.PublicKey);

        var authInfo = new byte[InfoPrefix.Length + userAgentPublicKey.Length + senderPublicKey.Length];
        InfoPrefix.CopyTo(authInfo, 0);
        userAgentPublicKey.CopyTo(authInfo.AsSpan(InfoPrefix.Length));
        senderPublicKey.CopyTo(authInfo.AsSpan(InfoPrefix.Length + userAgentPublicKey.Length));

        var ikm = HkdfExtract(authSecret.ToArray(), sharedSecret);
        ikm = HkdfExpand(ikm, authInfo, 32);

        var salt = RandomNumberGenerator.GetBytes(16);
        var prk = HkdfExtract(salt, ikm);
        var cek = HkdfExpand(prk, CekInfo, 16);
        var nonce = HkdfExpand(prk, NonceInfo, 12);

        var record = new byte[plaintext.Length + RecordDelimiter.Length];
        plaintext.CopyTo(record);
        RecordDelimiter.CopyTo(record, plaintext.Length);

        var ciphertext = new byte[record.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(cek, 16))
            aes.Encrypt(nonce, record, ciphertext, tag);

        // RFC 8291 aes128gcm body: salt(16) | rs(4) | idlen(1) | keyid(65) | ciphertext+tag
        var body = new byte[16 + 4 + 1 + senderPublicKey.Length + ciphertext.Length + tag.Length];
        var offset = 0;
        salt.CopyTo(body, offset);
        offset += salt.Length;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(body.AsSpan(offset), 4096);
        offset += 4;
        body[offset++] = (byte)senderPublicKey.Length;
        senderPublicKey.CopyTo(body, offset);
        offset += senderPublicKey.Length;
        ciphertext.CopyTo(body, offset);
        offset += ciphertext.Length;
        tag.CopyTo(body, offset);
        return body;
    }

    public static string CreateVapidAuthorization(
        WebPushOptions options,
        Uri endpoint,
        DateTimeOffset now)
    {
        if (!TryDecodeKeyPair(options, out var privateKey, out var publicKey))
            throw new CryptographicException("VAPID keys are not configured");

        var header = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            typ = "JWT",
            alg = "ES256"
        }));
        var claims = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            aud = endpoint.GetLeftPart(UriPartial.Authority),
            exp = now.AddHours(12).ToUnixTimeSeconds(),
            sub = string.IsNullOrWhiteSpace(options.Subject)
                ? "mailto:admin@example.invalid"
                : options.Subject
        }));
        var signingInput = Encoding.ASCII.GetBytes($"{header}.{claims}");

        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = privateKey,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] }
        });
        var signature = ecdsa.SignData(
            signingInput,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"vapid t={header}.{claims}.{Base64UrlEncode(signature)},k={Base64UrlEncode(publicKey)}";
    }

    public static bool TryDecodeKeyPair(
        WebPushOptions options,
        out byte[] privateKey,
        out byte[] publicKey)
    {
        privateKey = [];
        publicKey = [];
        if (string.IsNullOrWhiteSpace(options.PublicKey) ||
            string.IsNullOrWhiteSpace(options.PrivateKey))
            return false;
        try
        {
            publicKey = Base64UrlDecode(options.PublicKey);
            privateKey = Base64UrlDecode(options.PrivateKey);
            return publicKey.Length == 65 && publicKey[0] == 0x04 && privateKey.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Trim().Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] EncodeUncompressedPoint(ECParameters parameters)
    {
        var key = new byte[65];
        key[0] = 0x04;
        parameters.Q.X!.CopyTo(key, 1);
        parameters.Q.Y!.CopyTo(key, 33);
        return key;
    }

    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        while (offset < length)
        {
            var input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input, previous.Length);
            input[^1] = counter++;
            using var hmac = new HMACSHA256(prk);
            previous = hmac.ComputeHash(input);
            var take = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, take).CopyTo(output.AsSpan(offset));
            offset += take;
        }
        return output;
    }
}

internal static class WebPushEndpointValidator
{
    public static bool TryValidate(
        string? endpoint,
        IEnumerable<string> allowedHosts,
        out Uri uri,
        out string errorCode)
    {
        uri = null!;
        errorCode = "endpoint_invalid";
        if (string.IsNullOrWhiteSpace(endpoint) || endpoint.Length > 2048)
            return false;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ||
            parsed.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            parsed.Port != 443)
            return false;

        var host = parsed.IdnHost;
        if (string.IsNullOrWhiteSpace(host) ||
            host.Length > 255 ||
            host is "localhost" ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            System.Net.IPAddress.TryParse(host, out _))
            return false;

        var allowed = allowedHosts
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().TrimStart('.').ToLowerInvariant())
            .Any(item => host.Equals(item, StringComparison.OrdinalIgnoreCase) ||
                         host.EndsWith("." + item, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            errorCode = "endpoint_host_not_allowed";
            return false;
        }

        uri = parsed;
        errorCode = string.Empty;
        return true;
    }
}
