using Level5.Infrastructure.DependencyInjection;
using Level5.Infrastructure.Safety;
using Level5.LegacyScoreMigration.Commands;
using Level5.LegacyScoreMigration.Legacy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

const string Usage = "Usage: Level5.LegacyScoreMigration <audit|migrate|verify> "
    + "[--allow-database <name>] [--legacy-highscore-id <id>] [--limit <n>] [--verbose]";

if (args.Length == 0)
{
    await Console.Error.WriteLineAsync(Usage);
    return 1;
}

var command = args[0].ToLowerInvariant();
if (command is not ("audit" or "migrate" or "verify"))
{
    await Console.Error.WriteLineAsync($"Unknown command '{args[0]}'.");
    await Console.Error.WriteLineAsync(Usage);
    return 1;
}

var verbose = false;
int? legacyHighscoreId = null;
int? limit = null;
var allowedDatabases = new List<string>();

for (var i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--verbose":
            verbose = true;
            break;
        case "--allow-database" when i + 1 < args.Length:
            allowedDatabases.Add(args[++i]);
            break;
        case "--legacy-highscore-id" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out var parsedLegacyHighscoreId))
            {
                await Console.Error.WriteLineAsync($"'--legacy-highscore-id' value '{args[i]}' is not a valid integer.");
                await Console.Error.WriteLineAsync(Usage);
                return 1;
            }

            legacyHighscoreId = parsedLegacyHighscoreId;
            break;
        case "--limit" when i + 1 < args.Length:
            if (!int.TryParse(args[++i], out var parsedLimit))
            {
                await Console.Error.WriteLineAsync($"'--limit' value '{args[i]}' is not a valid integer.");
                await Console.Error.WriteLineAsync(Usage);
                return 1;
            }

            limit = parsedLimit;
            break;
        default:
            await Console.Error.WriteLineAsync($"Unrecognized argument '{args[i]}'.");
            await Console.Error.WriteLineAsync(Usage);
            return 1;
    }
}

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

var v2ConnectionString = configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings__DefaultConnection is not set (the V2 target database).");

var v1ConnectionString = configuration.GetConnectionString("LegacyDefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings__LegacyDefaultConnection is not set (the V1 source database, read-only).");

// The hard safety rail: this tool refuses to run against a V2 database that isn't allow-listed,
// and refuses to run if V1 and V2 resolve to the same database - never trusts a caller-supplied
// connection string alone to prove the target is safe.
var safetyResult = ConnectionStringSafetyRail.Check(v2ConnectionString, v1ConnectionString, allowedDatabases);
if (!safetyResult.Passed)
{
    await Console.Error.WriteLineAsync(safetyResult.RefusalReason);
    return 1;
}

var services = new ServiceCollection();
services.AddSingleton<IConfiguration>(configuration);
services.AddLevel5Infrastructure(configuration);

await using var provider = services.BuildServiceProvider();

// Constructed directly, not via DI: it's a tool-local, connection-string-scoped type that reads
// V1 raw SQL and never needs to be mocked through the container the way V2 stores are in tests.
var legacyReader = new LegacyHighscoreReader(v1ConnectionString);

return command switch
{
    "audit" => await AuditCommand.RunAsync(legacyReader, provider, new AuditOptions(legacyHighscoreId, limit, verbose), CancellationToken.None),
    "migrate" => await MigrateCommand.RunAsync(legacyReader, provider, new MigrateOptions(legacyHighscoreId, limit), CancellationToken.None),
    "verify" => await VerifyCommand.RunAsync(legacyReader, provider, new VerifyOptions(legacyHighscoreId), CancellationToken.None),
    _ => throw new InvalidOperationException($"Unhandled command: {command}")
};

// Referenced only by Level5.Architecture.Tests.DependencyRuleTests via typeof(Program) - a plain
// console Exe's top-level-statement Program class is internal by default, unlike ASP.NET Core's
// web SDK template (which makes it public partial), so this makes it resolvable from the test
// assembly the same way Level5.Api's and Level5.LegacyAccountMigration's own Program already are.
public partial class Program;
