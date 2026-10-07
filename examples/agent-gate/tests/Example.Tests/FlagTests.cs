using Example;
using Xunit;

public sealed class FlagTests
{
    [Fact]
    public void EnabledRequiresConfiguration() => Assert.False(Flag.IsEnabled(false));
}
