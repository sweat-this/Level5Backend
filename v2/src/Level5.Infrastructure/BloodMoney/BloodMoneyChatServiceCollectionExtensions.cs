using Level5.Application.BloodMoney;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Level5.Infrastructure.BloodMoney;

public static class BloodMoneyChatServiceCollectionExtensions
{
    /// <summary>Opt-in after AddLevel5Infrastructure. The host supplies the versioned rate policy.</summary>
    public static IServiceCollection AddBloodMoneyChat(this IServiceCollection services, BloodMoneyChatRatePolicy ratePolicy)
    {
        services.AddSingleton(ratePolicy);
        services.TryAddScoped<IBloodMoneyChatStore, BloodMoneyChatStore>();
        services.TryAddScoped<SendBloodMoneyChallengeMessageUseCase>();
        return services;
    }
}
