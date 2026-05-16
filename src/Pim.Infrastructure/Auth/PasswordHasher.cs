namespace Pim.Infrastructure.Auth;

public static class PasswordHasher
{
    public static string Hash(string password)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be null or whitespace.", nameof(password));

        return BCrypt.Net.BCrypt.HashPassword(password, workFactor: 10);
    }

    public static bool Verify(string password, string hash)
    {
        if (string.IsNullOrWhiteSpace(password))
            throw new ArgumentException("Password cannot be null or whitespace.", nameof(password));
        if (string.IsNullOrWhiteSpace(hash))
            throw new ArgumentException("Hash cannot be null or whitespace.", nameof(hash));

        return BCrypt.Net.BCrypt.Verify(password, hash);
    }
}
