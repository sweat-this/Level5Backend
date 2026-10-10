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
    [InlineData("Level5.Domain.Identity")]
    [InlineData("Level5.Domain.Players")]
    [InlineData("Level5.Domain.Social")]
    [InlineData("Level5.Domain.Platform")]
    public void Shared_Platform_domain_namespaces_do_not_depend_on_Level5_game_domains(
        string sharedNamespace)
    {
        var result = Types.InAssembly(DomainAssembly)
            .That().ResideInNamespace(sharedNamespace)
            .Should().NotHaveDependencyOnAny(
                "Level5.Domain.Competition",
                "Level5.Domain.Results",
                "Level5.Domain.Leaderboards")
            .GetResult();

        Assert.True(result.IsSuccessful, Failures(result));
    }

    [Theory]
    [InlineData("Level5.Application.Identity")]
    [InlineData("Level5.Application.Players")]
    [InlineData("Level5.Application.Social")]
    [InlineData("Level5.Application.Platform")]
    public void Shared_Platform_application_namespaces_do_not_depend_on_Level5_game_namespaces(
        string sharedNamespace)
    {
        var result = Types.InAssembly(ApplicationAssembly)
            .That().ResideInNamespace(sharedNamespace)
            .Should().NotHaveDependencyOnAny(
                "Level5.Application.Competition",
                "Level5.Application.Results",
                "Level5.Application.Leaderboards",
                "Level5.Domain.Competition",
                "Level5.Domain.Results",
                "Level5.Domain.Leaderboards")
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

    [Fact]
    public void BloodMoney_has_concrete_domain_and_application_capabilities()
    {
        Assert.Contains(DomainAssembly.GetTypes(), type => type.FullName == "Level5.Domain.BloodMoney.BloodCreditAccount" && type.IsClass && !type.IsAbstract);
        Assert.Contains(DomainAssembly.GetTypes(), type => type.FullName == "Level5.Domain.BloodMoney.BloodCreditTransaction" && type.IsClass && !type.IsAbstract);
        Assert.Contains(DomainAssembly.GetTypes(), type => type.FullName == "Level5.Domain.BloodMoney.BloodCreditReservation" && type.IsClass && !type.IsAbstract);
        Assert.Contains(DomainAssembly.GetTypes(), type => type.FullName == "Level5.Domain.BloodMoney.BloodMoneyChallengeId" && type.IsValueType);
        Assert.Contains(ApplicationAssembly.GetTypes(), type => type.FullName == "Level5.Application.BloodMoney.BloodCreditReservationMutator" && type.IsClass && !type.IsAbstract);
        Assert.Contains(ApplicationAssembly.GetTypes(), type => type.FullName == "Level5.Application.BloodMoney.ReserveBloodCreditsUseCase" && type.IsClass && !type.IsAbstract);
        Assert.Contains(ApplicationAssembly.GetTypes(), type => type.FullName == "Level5.Application.BloodMoney.ReleaseBloodCreditsUseCase" && type.IsClass && !type.IsAbstract);
        Assert.Contains(ApplicationAssembly.GetTypes(), type => type.FullName == "Level5.Application.BloodMoney.IssueBloodCreditsUseCase" && type.IsClass && !type.IsAbstract);
        Assert.Contains(ApplicationAssembly.GetTypes(), type => type.FullName == "Level5.Application.BloodMoney.GetMyBloodCreditBalanceUseCase" && type.IsClass && !type.IsAbstract);
    }

    [Fact]
    public void BloodMoney_domain_does_not_depend_on_Level5_game_domains_or_ids()
        => AssertBoundary(DomainAssembly, "Level5.Domain.BloodMoney", [
            "Level5.Domain.Competition", "Level5.Domain.Results", "Level5.Domain.Leaderboards",
            "Level5.Domain.Ids.VersusSeriesId", "Level5.Domain.Ids.AttemptId", "Level5.Domain.Ids.MatchResultId",
            "Level5.Domain.Ids.AccountId"]);

    [Fact]
    public void BloodMoney_application_does_not_depend_on_Level5_game_contracts_or_private_identity()
        => AssertBoundary(ApplicationAssembly, "Level5.Application.BloodMoney", [
            "Level5.Domain.Competition", "Level5.Domain.Results", "Level5.Domain.Leaderboards",
            "Level5.Application.Competition", "Level5.Application.Results", "Level5.Application.Leaderboards",
            "Level5.Application.Abstractions.IVersusSeriesStore", "Level5.Application.Abstractions.IRulesetCatalog",
            "Level5.Application.Abstractions.IChallengeExpiryPolicy", "Level5.Application.Abstractions.IMatchResultStore",
            "Level5.Application.Abstractions.ILeaderboardQuery", "Level5.Application.Abstractions.ILeaderboardPolicyCatalog",
            "Level5.Domain.Ids.VersusSeriesId", "Level5.Domain.Ids.AttemptId", "Level5.Domain.Ids.MatchResultId",
            "Level5.Domain.Ids.AccountId"]);

    [Theory]
    [InlineData("Competition")]
    [InlineData("Results")]
    [InlineData("Leaderboards")]
    [InlineData("Identity")]
    [InlineData("Players")]
    [InlineData("Social")]
    [InlineData("Platform")]
    public void Level5_and_shared_modules_do_not_depend_on_BloodMoney(string module)
    {
        AssertBoundary(DomainAssembly, $"Level5.Domain.{module}", ["Level5.Domain.BloodMoney"]);
        AssertBoundary(ApplicationAssembly, $"Level5.Application.{module}", ["Level5.Application.BloodMoney", "Level5.Domain.BloodMoney"]);
    }

    private static void AssertBoundary(System.Reflection.Assembly assembly, string sourceNamespace, string[] forbidden)
    {
        Assert.Contains(assembly.GetTypes(), type => type.Namespace == sourceNamespace ||
            type.Namespace?.StartsWith(sourceNamespace + ".", StringComparison.Ordinal) == true);
        var result = Types.InAssembly(assembly).That()
            .ResideInNamespaceMatching("^" + System.Text.RegularExpressions.Regex.Escape(sourceNamespace) + @"(\.|$)")
            .Should().NotHaveDependencyOnAny(forbidden).GetResult();
        Assert.True(result.IsSuccessful, Failures(result));
    }

    private static string Failures(TestResult result)
        => result.FailingTypes is null ? string.Empty : string.Join(", ", result.FailingTypes.Select(t => t.FullName));
}
