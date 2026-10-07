using System.Text;

namespace Mutate4CSharp.Tests;

public sealed class DeclarationScopeTests
{
    [Fact]
    public void SelectsChangedCurrentDeclarationsAndAccountsForRemovedDeclarations()
    {
        const string before = """
            partial class Widget
            {
                int Field = 1;
                int Keep() => 1;
                int Remove() => 2;
            }
            """;
        const string after = """
            partial class Widget
            {
                int Field = 3;
                int Keep() => 1;
                int Added() => 4;
            }
            """;

        var scope = DeclarationCatalog.Compare("src/Widget.cs", Encoding.UTF8.GetBytes(before),
            Encoding.UTF8.GetBytes(after));

        Assert.Contains(scope.Declarations, declaration => declaration.DisplayName.Contains("Field", StringComparison.Ordinal));
        Assert.Contains(scope.Declarations, declaration => declaration.DisplayName.Contains("Added", StringComparison.Ordinal));
        Assert.DoesNotContain(scope.Declarations, declaration => declaration.DisplayName.Contains("Keep", StringComparison.Ordinal));
        Assert.Contains(scope.Removals, removal => removal.DisplayName.Contains("Remove", StringComparison.Ordinal));
        Assert.All(scope.Declarations, declaration => Assert.True(declaration.EndLine >= declaration.StartLine));
    }

    [Fact]
    public void HeaderOnlyTypeChangeConservativelyRequiresProjectExpansion()
    {
        var before = Encoding.UTF8.GetBytes("class A { int M() => 1; }\n");
        var after = Encoding.UTF8.GetBytes("class A : IDisposable { int M() => 1; public void Dispose() { } }\n");

        var scope = DeclarationCatalog.Compare("src/A.cs", before, after);

        Assert.True(scope.RequiresProjectExpansion);
        Assert.Contains(scope.Reasons, reason => reason.Code == "SHARED_DECLARATION_CHANGED");
    }

    [Fact]
    public void HandlesTopLevelStatementsAndPropertyInitializersWithoutOverlappingAccessors()
    {
        var before = Encoding.UTF8.GetBytes("""
            Console.WriteLine(1);
            class A { int Value { get; } = 1; }
            """);
        var after = Encoding.UTF8.GetBytes("""
            Console.WriteLine(2);
            class A { int Value { get; } = 2; }
            """);

        var scope = DeclarationCatalog.Compare("Program.cs", before, after);

        Assert.Contains(scope.Declarations, declaration => declaration.DisplayName == "top-level statement");
        Assert.Contains(scope.Declarations, declaration => declaration.DisplayName == "property Value");
        Assert.DoesNotContain(scope.Declarations, declaration => declaration.DisplayName.Contains("get", StringComparison.Ordinal));
    }

    [Fact]
    public void WhitespaceOnlyEditsDoNotInventUnsupportedScope()
    {
        var scope = DeclarationCatalog.Compare("A.cs", Encoding.UTF8.GetBytes("class A { }\n"),
            Encoding.UTF8.GetBytes("class A\n{\n}\n"));

        Assert.Empty(scope.Declarations);
        Assert.Empty(scope.Removals);
        Assert.False(scope.RequiresProjectExpansion);
    }
}
