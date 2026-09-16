using Level5.Application.Abstractions;
using Level5.Infrastructure.Health;
using Level5.Infrastructure.Identity;
using Level5.Infrastructure.Persistence;
using Level5.Infrastructure.Persistence.Repositories;
using Level5.Infrastructure.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Level5.Infrastructure.DependencyInjection;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Wires every Infrastructure implementation behind its Application port. This is the only
    /// place outside the Api composition root that knows concrete infrastructure types exist.
    /// </summary>
    public static IServiceCollection AddLevel5Infrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured. Set it via user-secrets (local dev) or the ConnectionStrings__DefaultConnection environment variable (production).");

        services.AddDbContext<Level5V2DbContext>(options => options.UseNpgsql(connectionString));

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IAccountStore, AccountStore>();
        services.AddScoped<IPlayerProfileStore, PlayerProfileStore>();
        services.AddScoped<IFriendshipStore, FriendshipStore>();
        services.AddScoped<IVersusSeriesStore, VersusSeriesStore>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();

        services.AddSingleton<IPasswordHasher, AspNetPasswordHasher>();
        services.AddSingleton<ITokenIssuer, JwtTokenIssuer>();
        services.AddSingleton<IClock, SystemClock>();

        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);

        return services;
    }
}
