using Level5.Application.Abstractions;
using Level5.E2E.Fixtures;
using Level5.Infrastructure.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

if (args.Length != 2 || !string.Equals(args[0], "seed", StringComparison.OrdinalIgnoreCase))
{
    await Console.Error.WriteLineAsync("Usage: Level5.E2E.Fixtures seed <scenario>");
    await Console.Error.WriteLineAsync($"Known scenarios: {string.Join(", ", Scenarios.All.Keys)}");
    return 1;
}

var scenarioName = args[1];
if (!Scenarios.All.TryGetValue(scenarioName, out var scenario))
{
    await Console.Error.WriteLineAsync($"Unknown scenario '{scenarioName}'. Known scenarios: {string.Join(", ", Scenarios.All.Keys)}");
    return 1;
}

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var connectionString = configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings__DefaultConnection is not set. This tool is invoked by v2/scripts/e2e.ps1, which sets it to the local level5_v2_e2e connection string.");

// The hard safety rail (spec section 12/29): this tool refuses to run against anything but the
// literal local E2E database name, no matter what connection string it is handed - it never
// trusts a caller-supplied string alone to prove the target is safe to seed into.
var databaseName = new NpgsqlConnectionStringBuilder(connectionString).Database;
const string RequiredDatabaseName = "level5_v2_e2e";
if (!string.Equals(databaseName, RequiredDatabaseName, StringComparison.Ordinal))
{
    await Console.Error.WriteLineAsync(
        $"Refusing to seed: target database is '{databaseName}', but this tool only ever seeds '{RequiredDatabaseName}'. " +
        "This is a hardcoded safety rail, not something the caller can override.");
    return 1;
}

var services = new ServiceCollection();
// AddLevel5Infrastructure resolves IConfiguration from the container at DbContext-options-build
// time (not from the local variable above) - ASP.NET Core hosts register this automatically, a
// bare ServiceCollection does not.
services.AddSingleton<IConfiguration>(configuration);
services.AddLevel5Infrastructure(configuration);

// Substituted after AddLevel5Infrastructure so this registration wins on resolution (the DI
// container resolves the LAST registration of a service) - every use case below gets this
// deterministic clock instead of Infrastructure's real SystemClock.
var clock = new DeterministicClock();
services.AddSingleton<IClock>(clock);

services.AddScoped<Level5.Application.Social.SendFriendRequestUseCase>();
services.AddScoped<Level5.Application.Social.AcceptFriendRequestUseCase>();
services.AddScoped<Level5.Application.Competition.CreateChallengeUseCase>();
services.AddScoped<Level5.Application.Competition.AcceptChallengeUseCase>();
services.AddScoped<Level5.Application.Competition.DeclineChallengeUseCase>();
services.AddScoped<Level5.Application.Competition.CancelChallengeUseCase>();
services.AddScoped<Level5.Application.Competition.StartAttemptUseCase>();
services.AddScoped<Level5.Application.Competition.CompleteAttemptUseCase>();

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();

var ctx = new FixtureContext(scope.ServiceProvider, clock);
await scenario(ctx, CancellationToken.None);

Console.WriteLine($"Seeded scenario '{scenarioName}' into {RequiredDatabaseName}.");
return 0;
