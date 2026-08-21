using Pim.Api.Infrastructure.Ops;
using Xunit;

public class SqlAstValidatorTests
{
    [Theory]
    [InlineData("SELECT * FROM users", false)]
    [InlineData("SELECT password_hash FROM users", false)]
    [InlineData("DELETE FROM users", false)]
    [InlineData("SELECT id, username FROM users", true)]
    [InlineData("WITH c AS (SELECT id FROM users) SELECT id FROM c", true)]
    public void Validate_ReturnsExpected(string sql, bool allowed)
    {
        var v = new SqlAstValidator();
        var r = v.Validate(sql);
        Assert.Equal(allowed, r.IsValid);
    }

    [Theory]
    [InlineData("SELECT u.* FROM users u", false)]
    [InlineData("SELECT id FROM users WHERE password_hash = 'x'", false)]
    [InlineData("SELECT token_hash FROM refresh_tokens", false)]
    [InlineData("SELECT id FROM pg_catalog.pg_tables", false)]
    [InlineData("SELECT id FROM users; DROP TABLE users", false)]
    [InlineData("UPDATE users SET username='x'", false)]
    [InlineData("INSERT INTO users (id) VALUES (1)", false)]
    [InlineData("SELECT id, username FROM users WHERE id = 1", true)]
    [InlineData("SELECT count(*) FROM users", true)]
    public void Validate_Extended(string sql, bool allowed)
    {
        var v = new SqlAstValidator();
        var r = v.Validate(sql);
        Assert.Equal(allowed, r.IsValid);
    }

    [Fact]
    public void Validate_Empty_ReturnsInvalid()
    {
        var v = new SqlAstValidator();
        var r = v.Validate("");
        Assert.False(r.IsValid);
    }

    [Fact]
    public void Validate_SelectStar_Fails()
    {
        var v = new SqlAstValidator();
        Assert.False(v.Validate("SELECT * FROM users").IsValid);
        Assert.False(v.Validate("SELECT  *  FROM users").IsValid);
        Assert.False(v.Validate("select * from users").IsValid);
    }

    [Fact]
    public void Validate_TableStar_Fails()
    {
        var v = new SqlAstValidator();
        Assert.False(v.Validate("SELECT users.* FROM users").IsValid);
    }
}
