#if INHERITED_GRAPH
namespace GraphFixture.Production;
public static class ReleaseOnly { public static bool Positive(int value) => value > 0; }
#else
#error Inherited compiler definitions were lost.
#endif