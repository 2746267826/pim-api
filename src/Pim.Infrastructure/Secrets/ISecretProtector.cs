namespace Pim.Infrastructure.Secrets;

public interface ISecretProtector
{
    string Protect(string value);

    string Unprotect(string protectedValue);
}
