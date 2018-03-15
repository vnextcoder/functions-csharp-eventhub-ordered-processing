using Microsoft.Azure.EventHubs;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.Azure.WebJobs.ServiceBus;
using Polly;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using System.Net.Http;
using Microsoft.Azure.EventGrid.Models;
using Microsoft.Azure.EventGrid;

namespace OrderedEventHubs
{
    public static class EventHubTrigger
    {
        private static ConnectionMultiplexer redis = ConnectionMultiplexer.Connect(Environment.GetEnvironmentVariable("Redis"));
        private static IDatabase db = redis.GetDatabase();
        private static HttpClient client = new HttpClient();
        private static int FAILURE_SECONDS = int.Parse(Environment.GetEnvironmentVariable("FailureSeconds"));
        private static int FAILURE_THRESHOLD = int.Parse(Environment.GetEnvironmentVariable("FailureThreshold"));
        private static EventGridClient eventGridClient = new EventGridClient(new TopicCredentials(Environment.GetEnvironmentVariable("EventGrid")));
        private static string eventGridEndpoint = Environment.GetEnvironmentVariable("EventGridEndpoint");

        [FunctionName("EventHubTrigger")]
        public static async Task RunAsync(
            [EventHubTrigger(eventHubName: "events2", Connection = "EventHub")] EventData[] eventDataSet, 
            TraceWriter log,
            [Queue("deadletter")] IAsyncCollector<string> queue)
        {
            log.Info($"Triggered batch of size {eventDataSet.Length}");
            foreach (var eventData in eventDataSet) {
                var result = await Policy
                .Handle<Exception>()
                .RetryAsync(3, onRetryAsync: async (exception, retryCount, context) =>
                {
                    await db.ListRightPushAsync("events:" + context["partitionKey"], (string)context["counter"] + $"CAUGHT{retryCount}");
                })
                .ExecuteAndCaptureAsync(async () =>
                {
                    if (int.Parse((string)eventData.Properties["counter"]) % 100 == 0)
                    {
                        throw new SystemException("Some Exception");
                    }
                    await db.ListRightPushAsync("events:" + eventData.Properties["partitionKey"], (string)eventData.Properties["counter"]);
                },
                new Dictionary<string, object>() { { "partitionKey", eventData.Properties["partitionKey"] }, { "counter", eventData.Properties["counter"] } });

                if(result.Outcome == OutcomeType.Failure)
                {
                    await queue.AddAsync(Encoding.UTF8.GetString(eventData.Body.Array));
                    await queue.FlushAsync();
                    await LogFailure(eventData.SystemProperties.EnqueuedTimeUtc.Ticks);
                    await db.ListRightPushAsync("events:" + eventData.Properties["partitionKey"], (string)eventData.Properties["counter"] + "FAILED");
                }
            }
        }

        private static async Task LogFailure(long ticks)
        {
            var trans = db.CreateTransaction();
            trans.AddCondition(Condition.KeyNotExists("break"));
            trans.SortedSetRemoveRangeByScoreAsync("failures", double.NegativeInfinity, DateTime.Now.AddSeconds(FAILURE_SECONDS * -1).Ticks);
            trans.SortedSetAddAsync("failures", DateTime.Now.Ticks, DateTime.Now.Ticks);
            trans.KeyExpireAsync("failures", new TimeSpan(0, 0, FAILURE_SECONDS));
            var rolling_failures = trans.SortedSetLengthAsync("failures");
            if (await trans.ExecuteAsync())
            {
                var failures = await rolling_failures;
                if (failures >= FAILURE_THRESHOLD)
                {
                    trans = db.CreateTransaction();
                    trans.AddCondition(Condition.KeyNotExists("break"));
                    trans.ListRightPushAsync("break_log", "FAILURE TRIGGERED AT " + DateTime.Now + " WITH " + failures + " FAILURES");
                    trans.StringSetAsync("break", "true");
                    if(await trans.ExecuteAsync())
                    {
                        await EmitEvent();
                    }
                }
            }
        }

        private static async Task EmitEvent()
        {
            await eventGridClient.PublishEventsAsync(eventGridEndpoint, new List<EventGridEvent>() { new EventGridEvent()
                {
                    Id = Guid.NewGuid().ToString(),
                    Subject = "Alert/Break",
                    EventType = "CircuitBreaker",
                    EventTime = DateTime.Now,
                    Data = new { },
                    DataVersion = "1.0"
                }
            });
        }
    }
}
