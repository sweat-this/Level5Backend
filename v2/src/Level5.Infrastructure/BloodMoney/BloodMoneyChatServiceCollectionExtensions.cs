using Level5.Application.BloodMoney;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Level5.Infrastructure.Identity;

namespace Level5.Infrastructure.BloodMoney;

public static class BloodMoneyChatServiceCollectionExtensions
{
    /// <summary>Opt-in after AddLevel5Infrastructure. The host supplies rate/coalescing policies
    /// and the scoped shared notification writer.</summary>
    public static IServiceCollection AddBloodMoneyChat(this IServiceCollection services, BloodMoneyChatRatePolicy ratePolicy,
        BloodMoneyChatNotificationPolicy notificationPolicy)
    {
        services.AddSingleton(ratePolicy);
        services.AddSingleton(notificationPolicy);
        // Reuse deployment's stable, shared server secret with a separate cryptographic purpose.
        // No process-local key or committed fallback; existing JwtOptions startup validation applies.
        services.TryAddSingleton<IBloodMoneyChatCursorCodec>(provider =>
            new HmacBloodMoneyChatCursorCodec(provider.GetRequiredService<IOptions<JwtOptions>>().Value.Key));
        services.TryAddScoped<IBloodMoneyChatStore, BloodMoneyChatStore>();
        services.TryAddScoped<SendBloodMoneyChallengeMessageUseCase>();
        services.TryAddScoped<ListBloodMoneyChallengeMessagesUseCase>();
        services.TryAddScoped<AdvanceBloodMoneyChatReadPositionUseCase>();
        services.TryAddScoped<SetBloodMoneyChatNotificationsMutedUseCase>();
        services.TryAddScoped<ReportBloodMoneyChatMessageUseCase>();
        return services;
    }
}
