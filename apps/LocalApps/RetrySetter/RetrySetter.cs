using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NetDaemon.HassModel.Entities;
using Nito.AsyncEx;

namespace LocalApps.RetrySetter;

[NetDaemonApp]
public class RetrySetterApp
{
    private const string VERSION = "0.8";
    private const string ON = "on";
    private const string OFF = "off";
    private readonly ILogger<Runner> runnerLogger;
    private readonly ILogger<RetrySetterApp> logger;
    private readonly IHaContext ha;
    private readonly ConcurrentDictionary<string, AsyncLock> entityLocks = new();

    private ImmutableDictionary<string, Runner> runners = ImmutableDictionary<string, Runner>.Empty;
    private long requestCounter = 0;

    private AsyncLock GetAsyncLock(string entityId)
    {
        return entityLocks.GetOrAdd(entityId, (_) => new AsyncLock());
    }

    public RetrySetterApp(IHaContext ha, ILogger<RetrySetterApp> logger, ILoggerFactory loggerFactory)
    {
        this.logger = logger;
        this.ha = ha;
        runnerLogger = loggerFactory.CreateLogger<Runner>();
        logger.LogInformation("Running v{version}", VERSION);
        ha.Events.Where(e => e.EventType == "set_lights" && e.DataElement.HasValue)
            .SelectMany(ToSetLightEvents)
            .SubscribeAsyncConcurrent(HandleSetLights);
    }

    public record SetLightEvent(string EntityId, string Value, DateTime TriggeredAt);

    public IEnumerable<SetLightEvent> ToSetLightEvents(Event genericEvent)
    {
        var triggeredAt = genericEvent.TimeFired ?? DateTime.Now;
        if (!genericEvent.DataElement.HasValue) return [];
        if (!genericEvent.DataElement.Value.TryGetProperty("entityId", out var entityIdElement))
        {
            logger.LogWarning("Unable to parse entityId from event {event}", genericEvent);
            return [];
        }
        string[] entityIds;
        if (entityIdElement.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            var entityIdsLen = entityIdElement.GetArrayLength();
            List<string> entityIdsList = new(entityIdsLen);
            for (var i = 0; i < entityIdsLen; i++)
            {
                var curEntityId = entityIdElement[i].GetString();
                if (string.IsNullOrEmpty(curEntityId)) continue;
                entityIdsList.Add(curEntityId);
            }
            entityIds = entityIdsList.ToArray();
        }
        else if (entityIdElement.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var entityId = entityIdElement.GetString();
            if (string.IsNullOrEmpty(entityId)) return [];
            entityIds = [entityId];
        }
        else
        {
            logger.LogWarning("Unable to parse entityId from event {event}", genericEvent);
            return [];
        }
        if (!genericEvent.DataElement.Value.TryGetProperty("value", out var valueElement))
        {
            logger.LogWarning("Unable to parse value from event {event}", genericEvent);
            return [];
        }
        var value = valueElement.GetString();
        if (string.IsNullOrEmpty(value)) return [];
        return entityIds.Select(entityId => new SetLightEvent(EntityId: entityId, Value: value, TriggeredAt: triggeredAt));
    }

    private async Task HandleSetLights(SetLightEvent setLightEvent)
    {
        var triggeredAt = setLightEvent.TriggeredAt;
        var entityId = setLightEvent.EntityId;
        var newValue = setLightEvent.Value;
        var requestId = Interlocked.Increment(ref requestCounter);

        var entityLock = GetAsyncLock(entityId);

        Runner newRunner;
        using (var locked = await entityLock.LockAsync())
        {
            if (runners.TryGetValue(entityId, out var existingRunner))
            {
                if (string.Equals(existingRunner.Value, newValue, StringComparison.OrdinalIgnoreCase) && (triggeredAt - existingRunner.TriggeredAt) <= TimeSpan.FromSeconds(5))
                {
                    logger.LogInformation("[{requestId}] - Duplicate request ignored for light {entityId} to be set to {value} triggered at {triggeredAt}", requestId, entityId, newValue, triggeredAt.ToString("O"));
                    return;
                }
                await existingRunner.Cancel();
                ImmutableInterlocked.TryRemove(ref runners, entityId, out var _);
            }
            logger.LogInformation("[{requestId}] - Processing request for light {entityId} to be set to {value} triggered at {triggeredAt}", requestId, entityId, newValue, triggeredAt.ToString("O"));
            newRunner = new(requestId: requestId, entityId: entityId, value: newValue, triggeredAt: triggeredAt, cancellationTokenSource: new CancellationTokenSource(), postRun: RemoveRunner, haContext: ha, logger: runnerLogger);
            if (!ImmutableInterlocked.TryAdd(ref runners, entityId, newRunner))
            {
                return;
            }
        }
        await newRunner.Run();
    }

    private void RemoveRunner(string entityId)
    {
        ImmutableInterlocked.TryRemove(ref runners, entityId, out var _);
    }

    private class Runner(long requestId, string entityId, string value, DateTime triggeredAt, CancellationTokenSource cancellationTokenSource, Action<string> postRun, IHaContext haContext, ILogger<Runner> logger, int totalAttempts = 10, int minimumRetryIntervalMsec = 1000)
    {
        private static readonly TimeSpan PollingDelay = TimeSpan.FromMilliseconds(200);

        public DateTime TriggeredAt => triggeredAt;
        private readonly TaskCompletionSource<bool> taskCompletionSource = new();

        public Task CurrentTask => taskCompletionSource.Task;
        public string Value => value;

        public async Task Cancel()
        {
            await cancellationTokenSource.CancelAsync();
            await CurrentTask;
        }

        public async Task<bool> Run()
        {
            var startTime = Stopwatch.GetTimestamp();
            var isSuccessful = false;
            var attemptNumber = 1;
            var firstRetryInterval = Math.Max(200, minimumRetryIntervalMsec);
            int[] delaysMsec = [firstRetryInterval, firstRetryInterval];
            int delaysLength = delaysMsec.Length;
            try
            {
                var cancellationToken = cancellationTokenSource.Token;
                while (!cancellationToken.IsCancellationRequested && attemptNumber <= totalAttempts)
                {
                    logger.LogDebug("[{requestId}] - Setting light {entityId} to {value} - attempt {attemptNum}", requestId, entityId, value, attemptNumber);
                    SetLightValue();

                    int supposedDelay = (delaysLength >= attemptNumber) ? delaysMsec[attemptNumber - 1] : (delaysMsec[delaysLength - 1] * (int)Math.Pow(2, attemptNumber - delaysLength));
                    if (await PollWait(TimeSpan.FromMilliseconds(supposedDelay), cancellationToken))
                    {
                        var timeSpent = Stopwatch.GetElapsedTime(startTime);
                        logger.LogDebug("[{requestId}] - Waited for {delay} ms for attempt {attemptNum}", requestId, supposedDelay, attemptNumber);
                        logger.LogInformation("[{requestId}] - Completed. Light {entityId} set to {value} on attempt {attemptNum} after {timeSpent} ms", requestId, entityId, value, attemptNumber, timeSpent.TotalMilliseconds);
                        return true;
                    }

                    ++attemptNumber;
                }
                if (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning("[{requestId}] - Failed. Could not update light {entityId} to {value} after all {totalAttempts} attempts", requestId, entityId, value, totalAttempts);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{requestId}] - Failed. Unexpected error in runner for {entityId} setting to {value} during attempt {attemptNumber}", requestId, entityId, value, attemptNumber);
            }
            finally
            {
                postRun(entityId);
                taskCompletionSource.SetResult(isSuccessful);
            }
            return isSuccessful;
        }

        private async Task<bool> PollWait(TimeSpan waitTime, CancellationToken cancellationToken)
        {
            if (IsLightStateCorrect()) return true;
            var remainingTime = waitTime;
            while (remainingTime > TimeSpan.Zero && !cancellationToken.IsCancellationRequested)
            {
                var curWait = remainingTime > PollingDelay ? PollingDelay : remainingTime;
                try
                {
                    await Task.Delay(curWait, cancellationToken);
                }
                catch (TaskCanceledException)
                {
                    return false;
                }
                remainingTime -= curWait;
                if (IsLightStateCorrect()) return true;
            }
            return false;
        }

        private void SetLightValue()
        {
            var serviceCall = string.Equals(value, ON, StringComparison.OrdinalIgnoreCase) ? "turn_on" : "turn_off";
            try
            {
                haContext.CallService("light", serviceCall, ServiceTarget.FromEntity(entityId));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[{requestId}] - Unable to set light {entityId} {serviceCall}", requestId, entityId, serviceCall);
            }
        }

        private bool IsLightStateCorrect()
        {
            var newState = haContext.Entity(entityId).State;
            return string.Equals(newState, value, StringComparison.OrdinalIgnoreCase);
        }
    }
}
