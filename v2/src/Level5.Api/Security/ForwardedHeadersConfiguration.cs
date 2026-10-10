using System.Net;
using Microsoft.AspNetCore.HttpOverrides;

namespace Level5.Api.Security;

internal static class ForwardedHeadersConfiguration
{
    internal static void Configure(ForwardedHeadersOptions options, IConfiguration configuration)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust only configured proxies/networks and the framework's loopback defaults.
        // Untrusted forwarded headers must not affect authentication rate-limit partitions.
        foreach (var proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        // Preserve the existing configuration key when using the modern framework API.
        foreach (var network in configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
        {
            // Invalid CIDR values fail loudly at startup.
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }
}
