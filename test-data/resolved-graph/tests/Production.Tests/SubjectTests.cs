namespace GraphFixture.Tests;
public sealed class SubjectTests
{
    [Xunit.Theory]
    [Xunit.InlineData(2, false)]
    [Xunit.InlineData(3, false)]
    [Xunit.InlineData(4, true)]
    public void Strict_boundary(int value, bool expected) => Xunit.Assert.Equal(expected, Production.Subject.Enabled(value));
    [Xunit.Theory]
    [Xunit.InlineData(-1, false)]
    [Xunit.InlineData(0, false)]
    [Xunit.InlineData(1, true)]
    public void Conditional_release_source(int value, bool expected) => Xunit.Assert.Equal(expected, Production.ReleaseOnly.Positive(value));
}