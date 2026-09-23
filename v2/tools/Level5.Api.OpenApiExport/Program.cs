using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Level5.Api.OpenApiExport;

// Deliberately not top-level statements: those implicitly generate their own `Program` class in
// this assembly, which would shadow the `Program` imported from Level5.Api (via its
// `public partial class Program` - see Level5.Api/Program.cs) and make
// `WebApplicationFactory<Program>` below boot *this* empty program instead of the real Api host.
internal static class EntryPoint
{
    /// <summary>
    /// Emits the V2 API's current OpenAPI document to a file, deterministically, without a
    /// manually started server and without any production/external dependency (no reachable
    /// Postgres, no real JWT secret) - see v2/openapi/README.md. This is the single source
    /// `scripts/generate-openapi.sh` and `scripts/check-openapi-drift.sh` both drive.
    /// </summary>
    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1)
        {
            await Console.Error.WriteLineAsync("Usage: Level5.Api.OpenApiExport <output-file-path>");
            return 1;
        }

        var outputPath = args[0];

        await using var factory = new OpenApiExportFactory();
        using var client = factory.CreateClient();

        // Swashbuckle's default document name, set by AddSwaggerGen()'s own default in Program.cs.
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        if (!response.IsSuccessStatusCode)
        {
            await Console.Error.WriteLineAsync(
                $"Failed to fetch the OpenAPI document: {(int)response.StatusCode} {response.StatusCode}");
            return 1;
        }

        var body = await response.Content.ReadAsStringAsync();

        // Re-serialized (not written raw) so the committed file has stable, reviewable
        // formatting independent of whatever Swashbuckle's own serializer settings produce -
        // JsonNode parsing preserves member order from the source document, so this changes
        // formatting only, never content or ordering.
        var document = JsonNode.Parse(body) ?? throw new InvalidOperationException("Empty OpenAPI document.");
        var formatted = document.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        var directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // A trailing newline keeps the file POSIX-text-file-clean and diff-friendly.
        await File.WriteAllTextAsync(outputPath, formatted + Environment.NewLine);

        Console.WriteLine($"Wrote OpenAPI document to {outputPath}");
        return 0;
    }
}

/// <summary>
/// Boots the real Api host exactly like Level5.Api.IntegrationTests' ApiFactory, minus the real
/// Postgres container: OpenAPI generation only inspects route/DTO metadata, so a syntactically
/// valid-but-unreachable connection string is enough - nothing here ever executes a query. Jwt/
/// Session options still need real-shaped values because they're validated at host startup
/// (ValidateOnStart) regardless of whether the database is ever touched.
/// </summary>
internal sealed class OpenApiExportFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Swagger/SwaggerUI is only mapped in Development (see Program.cs) - deliberately, so
        // production never exposes it. Generation therefore runs in that same environment
        // rather than adding a generation-specific carve-out to the app's own middleware setup.
        builder.UseEnvironment("Development");

        // WebApplicationFactory's default content-root heuristic assumes a "test project sits
        // next to the app project" layout and fails to locate Level5.Api from here (this project
        // lives under v2/tools, not v2/tests). Resolving it relative to the .sln instead matches
        // Level5.Api's actual location regardless of where this tool is invoked from.
        builder.UseContentRoot(Path.Combine(FindSolutionDirectory(), "src", "Level5.Api"));

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] =
                    "Host=localhost;Port=5432;Database=level5_v2_openapi_export;Username=openapi;Password=openapi",
                ["Jwt:Key"] = "openapi-export-signing-key-not-for-production-use-32chars",
                ["Jwt:Issuer"] = "Level5BackendV2.OpenApiExport",
                ["Jwt:Audience"] = "Level5Client.OpenApiExport"
            });
        });
    }

    /// <summary>Walks up from the running assembly to find the directory containing the V2 .sln.</summary>
    private static string FindSolutionDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Level5BackendV2.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate Level5BackendV2.sln above " + AppContext.BaseDirectory);
    }
}
