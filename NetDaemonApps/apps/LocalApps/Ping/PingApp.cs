using System.Text.Json.Serialization;
using NetDaemon.HassModel.Integration;

namespace NetDaemonApps;

[NetDaemonApp]
public class PingApp
{
    private const string VERSION = "0.3";

    public PingApp(IHaContext ha, ILogger<PingApp> logger)
    {
        logger.LogInformation("Running {appName} {version}", nameof(PingApp), VERSION);
        ha.RegisterServiceCallBack<PingServiceData>("netdaemon_ping", e =>
        {
            logger.LogDebug("Ping {pingData}", e);
            var pong = new PongEventData(e.UniqueId, DateTime.Now);
            ha.SendEvent("netdaemon_pong", pong);
            logger.LogDebug("Pong {pongData}", pong);
        });
    }

    record PingServiceData([property: JsonPropertyName("uniqueId")] string UniqueId);
    record PongEventData([property: JsonPropertyName("uniqueId")] string UniqueId, [property: JsonPropertyName("createdAt")] DateTime CreatedAt);
}
