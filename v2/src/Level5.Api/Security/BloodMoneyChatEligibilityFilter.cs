using Level5.Application.Abstractions;
using Level5.Application.BloodMoney;
using Level5.Domain.Identity;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Level5.Api.Security;

/// <summary>Rechecks current Account authority before model binding or resource disclosure.
/// This is deliberately local to chat; MSG-005 can extend it with communication restrictions.</summary>
public sealed class BloodMoneyChatEligibilityFilter(ICurrentAccountAccessor currentAccount, IAccountStore accounts)
    : IAsyncResourceFilter
{
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        var account = await accounts.FindByIdAsync(currentAccount.GetCurrentAccountId(), context.HttpContext.RequestAborted);
        if (account?.Status != AccountStatus.Active)
            throw new BloodMoneyChatException("chat_communication_restricted", "Chat communication is restricted.");
        await next();
    }
}
