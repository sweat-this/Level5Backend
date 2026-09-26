using Level5.Api;
using Level5.Application.Competition;
using Level5.Domain.Competition;
using Level5.Infrastructure.Persistence;
using NetArchTest.Rules;
using Xunit;

namespace Level5.Architecture.Tests;

/// <summary>
/// Enforces the Clean Architecture dependency rules from the V2 ADR at the assembly level, so a
/// violation fails the build instead of surviving as an unenforced convention.
/// </summary>
public sealed class DependencyRuleTests
{
    private static readonly System.Reflection.Assembly DomainAssembly = typeof(VersusSeries).Assembly;
    private static readonly System.Reflection.Assembly ApplicationAssembly = typeof(CreateChallengeUseCase).Assembly;
    private static readonly System.Reflection.Assembly InfrastructureAssembly = typeof(Level5V2DbContext).Assembly;
    private static readonly System.Reflection.Assembly ApiAssembly = typeof(Program).Assembly;

    [Fact]
    public void Domain_does_not_depend_on_Application()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should().NotHaveDependencyOn("Level5.Application")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Fact]
    public void Domain_does_not_depend_on_Infrastructure_or_Api()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should().NotHaveDependencyOnAny("Level5.Infrastructure", "Level5.Api")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Fact]
    public void Domain_does_not_reference_EfCore_AspNetCore_or_Unity()
    {
        var result = Types.InAssembly(DomainAssembly)
            .Should().NotHaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql",
                "UnityEngine",
                "UnityEditor")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Fact]
    public void Application_does_not_depend_on_Infrastructure_or_Api()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .Should().NotHaveDependencyOnAny("Level5.Infrastructure", "Level5.Api")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Fact]
    public void Application_does_not_reference_EfCore_or_AspNetCore()
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .Should().NotHaveDependencyOnAny(
                "Microsoft.EntityFrameworkCore",
                "Microsoft.AspNetCore",
                "Npgsql")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Fact]
    public void Api_does_not_reference_persistence_libraries_directly()
    {
        // The composition root delegates all persistence wiring to AddLevel5Infrastructure, and
        // database error translation happens behind IUnitOfWork - so no EF Core or Npgsql type
        // should be reachable from Api code. Without this rule, something like catching
        // DbUpdateException in the HTTP error handler compiles happily and silently couples the
        // API's error contract to the current database provider.
        var result = Types.InAssembly(ApiAssembly)
            .Should().NotHaveDependencyOnAny("Microsoft.EntityFrameworkCore", "Npgsql")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Fact]
    public void Infrastructure_does_not_depend_on_Api()
    {
        var result = Types.InAssembly(InfrastructureAssembly)
            .Should().NotHaveDependencyOn("Level5.Api")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Theory]
    [MemberData(nameof(AllV2Assemblies))]
    public void No_V2_assembly_references_the_legacy_backend(System.Reflection.Assembly assembly)
    {
        var referencesLegacy = assembly.GetReferencedAssemblies()
            .Any(a => a.Name == "Level5Backend");

        Assert.False(referencesLegacy, $"{assembly.GetName().Name} must not reference the legacy Level5Backend assembly.");
    }

    public static IEnumerable<object[]> AllV2Assemblies()
    {
        yield return [DomainAssembly];
        yield return [ApplicationAssembly];
        yield return [InfrastructureAssembly];
        yield return [ApiAssembly];
    }

    private static string Failures(TestResult result)
        => result.FailingTypes is null ? string.Empty : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
