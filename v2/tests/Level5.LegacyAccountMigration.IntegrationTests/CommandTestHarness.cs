using Level5.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.LegacyAccountMigration.IntegrationTests;

/// <summary>Builds the same real DI container Program.cs builds, pointed at the test V2 container, for command-level end-to-end tests.</summary>
internal static class CommandTestHarness
{
    public static ServiceProvider BuildProvider(string v2ConnectionString, Action<IServiceCollection>? configureOverrides = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = v2ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddLevel5Infrastructure(configuration);

        // Applied after AddLevel5Infrastructure so an override registration wins on resolution
        // (the container resolves the LAST registration of a service) - same pattern
        // Level5.E2E.Fixtures uses to substitute its deterministic clock.
        configureOverrides?.Invoke(services);

        return services.BuildServiceProvider();
    }
}
