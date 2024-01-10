using System.Text.Json;
using FluentAssertions;
using NetDaemon.HassModel;
using NSubstitute;
using Xunit.Abstractions;

namespace Tests.UnitTests;

public class RetrySetterAppTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void ShouldParseMultipleEventsCorrectly()
    {
        var ha = Substitute.For<IHaContext>();
        var logger = testOutputHelper.ToLogger<RetrySetterApp>();
        var loggerFactory = testOutputHelper.ToLoggerFactory();
        RetrySetterApp retrySetterApp = new(ha: ha, logger: logger, loggerFactory: loggerFactory);

        Event originalEvent = JsonSerializer.Deserialize<Event>(
            """
            {
                "event_type": "set_lights",
                "data": {
                    "entityId": [
                        "light.abc",
                        "light.xyz"
                    ],
                    "value": "off"
                },
                "time_fired": "2024-01-10T10:47:22.2696810Z"
            }
            """)!;
        
        var events = retrySetterApp.ToSetLightEvents(originalEvent);
        IEnumerable<RetrySetterApp.SetLightEvent> expectedEvents = [
            new RetrySetterApp.SetLightEvent(EntityId: "light.abc", Value: "off", TriggeredAt: DateTime.Parse("2024-01-10T10:47:22.2696810Z")),
            new RetrySetterApp.SetLightEvent(EntityId: "light.xyz", Value: "off", TriggeredAt: DateTime.Parse("2024-01-10T10:47:22.2696810Z"))
        ];
        events.Should().BeEquivalentTo(expectedEvents, o => o.WithStrictOrdering().RespectingRuntimeTypes());
    }

    [Fact]
    public void ShouldParseSingleEventCorrectly()
    {
        var ha = Substitute.For<IHaContext>();
        var logger = testOutputHelper.ToLogger<RetrySetterApp>();
        var loggerFactory = testOutputHelper.ToLoggerFactory();
        RetrySetterApp retrySetterApp = new(ha: ha, logger: logger, loggerFactory: loggerFactory);

        Event originalEvent = JsonSerializer.Deserialize<Event>(
            """
            {
                "event_type": "set_lights",
                "data": {
                    "entityId": "light.abc",
                    "value": "off"
                },
                "time_fired": "2024-01-10T10:47:22.2696810Z"
            }
            """)!;
        
        var events = retrySetterApp.ToSetLightEvents(originalEvent);
        IEnumerable<RetrySetterApp.SetLightEvent> expectedEvents = [
            new RetrySetterApp.SetLightEvent(EntityId: "light.abc", Value: "off", TriggeredAt: DateTime.Parse("2024-01-10T10:47:22.2696810Z"))
        ];
        events.Should().BeEquivalentTo(expectedEvents, o => o.WithStrictOrdering().RespectingRuntimeTypes());
    }
}