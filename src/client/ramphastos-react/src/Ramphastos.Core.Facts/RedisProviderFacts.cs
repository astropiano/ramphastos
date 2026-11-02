using System.Text.Json.Nodes;
using FluentAssertions;
using Ramphastos.Core;
using Ramphastos.TestSdk;
using StackExchange.Redis;
using Xunit;

namespace Ramphastos.Core.Facts;

/// <summary>
/// Redis provider facts — skipped (early return) when no Redis is reachable at
/// RAMPHASTOS_TEST_REDIS (default localhost:6399). For local verification:
/// docker run -d -p 6399:6379 --name rph-redis redis:7-alpine
/// </summary>
public class RedisProviderFacts
{
    private static string ConnectionString =>
        Environment.GetEnvironmentVariable("RAMPHASTOS_TEST_REDIS") ?? "localhost:6399";

    private static async Task<IConnectionMultiplexer?> TryConnect()
    {
        try
        {
            var mux = await ConnectionMultiplexer.ConnectAsync(
                $"{ConnectionString},connectTimeout=1500,syncTimeout=1500,abortConnect=false");
            return mux.IsConnected ? mux : null;
        }
        catch
        {
            return null;
        }
    }

    [Fact]
    public async Task Provider_saves_loads_and_deletes_records()
    {
        await using var mux = await TryConnect();
        if (mux is null) return; // no redis in this environment — skip

        var provider = new SessionProviderRedis(mux,
            keyPrefix: $"ramphastos:test:{Guid.NewGuid():N}:",
            recordTtl: TimeSpan.FromMinutes(5));

        var record = new SessionRecord
        {
            SessionId = "sess_redis",
            VmType = typeof(DemoVm).AssemblyQualifiedName!,
            Schema = "sha1:x",
            StateFormat = "stj",
            State = new JsonObject { ["count"] = 5 },
        };
        await provider.SaveAsync(record);

        var loaded = await provider.LoadAsync("sess_redis");
        loaded.Should().NotBeNull();
        loaded!.SessionId.Should().Be("sess_redis");
        loaded.State!["count"]!.GetValue<int>().Should().Be(5);

        await provider.DeleteAsync("sess_redis");
        (await provider.LoadAsync("sess_redis")).Should().BeNull();
    }

    [Fact]
    public async Task Full_suspend_resume_through_redis()
    {
        await using var mux = await TryConnect();
        if (mux is null) return;

        var prefix = $"ramphastos:test:{Guid.NewGuid():N}:";
        SessionOptions Opts() => new()
        {
            Provider = new SessionProviderRedis(mux, keyPrefix: prefix),
            DisconnectGrace = TimeSpan.FromMilliseconds(60),
        };

        string sessionId;
        {
            await using var host = new TestHost(() => new DemoVm(), Opts());
            var c = await host.ConnectAsync();
            sessionId = c.SessionId!;
            await host.CommandAsync(c, "increment", 33);
            host.Disconnect(c);
            await Task.Delay(300);
            host.Manager.Sessions.Should().BeEmpty();
        }

        await using var host2 = new TestHost(() => new DemoVm(), Opts());
        var c2 = await host2.ConnectAsync(sessionId: sessionId);
        c2.At("/count")!.GetValue<int>().Should().Be(33,
            "another node/host resumed the session straight from redis");
    }
}
