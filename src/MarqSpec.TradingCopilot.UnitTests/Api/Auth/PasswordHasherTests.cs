using MarqSpec.TradingCopilot.Api.Auth;

namespace MarqSpec.TradingCopilot.UnitTests.Api.Auth;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Verify_ShouldReturnTrue_ForTheOriginalPassword()
    {
        string hash = _hasher.Hash("correct horse battery staple");

        _hasher.Verify(hash, "correct horse battery staple").Should().BeTrue();
    }

    [Fact]
    public void Verify_ShouldReturnFalse_ForAWrongPassword()
    {
        string hash = _hasher.Hash("correct horse battery staple");

        _hasher.Verify(hash, "Tr0ub4dour").Should().BeFalse();
    }

    [Fact]
    public void Hash_ShouldNotReturnThePlaintext()
    {
        _hasher.Hash("s3cr3t").Should().NotBe("s3cr3t");
    }
}
