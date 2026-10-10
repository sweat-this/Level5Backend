using Level5.Application.BloodMoney;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Level5.Infrastructure.BloodMoney;

public static class BloodMoneyChallengeServiceCollectionExtensions
{
    /// <summary>Opt-in composition after AddLevel5Infrastructure. The host must supply approved timing windows.</summary>
    public static IServiceCollection AddBloodMoneyChallenges(this IServiceCollection services, BloodMoneyChallengeTimingPolicy timing)
    {
        services.AddSingleton(timing);
        services.TryAddScoped<BloodCreditReservationMutator>();
        services.TryAddScoped<IBloodMoneyChallengeStore, BloodMoneyChallengeStore>();
        services.TryAddScoped<BloodMoneyChallengeLifecycle>();
        services.TryAddScoped<CreateBloodMoneyChallengeUseCase>();
        services.TryAddScoped<AcceptBloodMoneyChallengeUseCase>();
        services.TryAddScoped<DeclineBloodMoneyChallengeUseCase>();
        services.TryAddScoped<CancelBloodMoneyChallengeUseCase>();
        services.TryAddScoped<ExpirePendingBloodMoneyChallengeUseCase>();
        services.TryAddScoped<GetBloodMoneyChallengeUseCase>();
        return services;
    }
}
