using Microsoft.AspNetCore.SignalR.Client;

namespace Yaat.Client.Services;

/// <summary>
/// SignalR reconnect schedule that survives a full server redeploy. The default
/// <c>WithAutomaticReconnect()</c> policy retries at 0/2/10/30s and then gives up after ~42s —
/// far short of the ~7-10 minutes a droplet deploy is down while the container rebuilds. This
/// policy retries quickly at first (transient blips) and then every 15s until a 15-minute window
/// has elapsed, so the client is still trying when the server comes back and the user's session
/// can resume automatically.
/// </summary>
public sealed class YaatReconnectPolicy : IRetryPolicy
{
    private static readonly TimeSpan ReconnectWindow = TimeSpan.FromMinutes(15);

    public TimeSpan? NextRetryDelay(RetryContext retryContext)
    {
        if (retryContext.ElapsedTime >= ReconnectWindow)
        {
            return null;
        }

        return retryContext.PreviousRetryCount switch
        {
            0 => TimeSpan.Zero,
            1 => TimeSpan.FromSeconds(2),
            2 => TimeSpan.FromSeconds(5),
            3 => TimeSpan.FromSeconds(10),
            _ => TimeSpan.FromSeconds(15),
        };
    }
}
