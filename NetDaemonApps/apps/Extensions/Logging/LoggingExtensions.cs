using System.Text.Json.Serialization;

namespace NetDaemonApps.Logging;

public static class LoggingExtensions
{
    public static bool LogToHA(this IHaContext haContext, ILogger logger, HALogData logData)
    {
        try
        {
            haContext.CallService("system_log", "write", data: logData);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error while writing to HA logger - {logData}", logData);
            return false;
        }
    }
}

public static class HALogLevel
{
    public const string Info = "info";
    public const string Error = "error";
    public const string Warning = "warning";
}

public record HALogData([property: JsonPropertyName("level")] string Level, [property: JsonPropertyName("logger")] string Logger, [property: JsonPropertyName("message")] string Message);
