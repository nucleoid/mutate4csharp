extern alias dependencyAlias;
namespace GraphFixture.Production;
public static class Subject
{
    public static bool Enabled(int value) => value > dependencyAlias::GraphFixture.Dependency.Minimum.Value + Shared.LinkedValue.One + GeneratedOffset.Value;
}