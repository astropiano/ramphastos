using FluentAssertions;
using Ramphastos.Core;
using Ramphastos.TestSdk;
using Xunit;

namespace Ramphastos.Core.Facts;

/// <summary>Session lifecycle: disconnect grace → suspend to provider → resume rehydrates.</summary>
public class LifecycleFacts : IDisposable
{
    private readonly string _storeDir = Path.Combine(Path.GetTempPath(), "rph-facts-" + Guid.NewGuid().ToString("N"));

    private SessionOptions FileOptions() => new()
    {
        Provider = new SessionProviderFiles(_storeDir),
        DisconnectGrace = TimeSpan.FromMilliseconds(60),
    };

    [Fact]
    public async Task Idle_session_is_suspended_and_resumed_with_state()
    {
        string? sessionId;
        {
            await using var host = new TestHost(() => new DemoVm(), FileOptions());
            var c = await host.ConnectAsync();
            sessionId = c.SessionId;
            await host.CommandAsync(c, "increment", 42);
            host.Disconnect(c);
            await Task.Delay(300); // let the grace timer fire → persist + evict
            host.Manager.Sessions.Should().BeEmpty();
        }

        // new host process (same provider dir) — session must come back intact
        await using var host2 = new TestHost(() => new DemoVm(), FileOptions());
        var c2 = await host2.ConnectAsync(sessionId: sessionId);
        c2.LastConnectReplayed.Should().BeFalse("resume restores via snapshot, not op replay");
        c2.At("/count")!.GetValue<int>().Should().Be(42, "suspended state survived eviction + reload");
    }

    [Fact]
    public async Task Schema_drift_rejects_resume_with_fresh_session()
    {
        var sessionId = "drift-" + Guid.NewGuid().ToString("N")[..8];
        var provider = new SessionProviderFiles(_storeDir);
        var record = new SessionRecord
        {
            SessionId = sessionId,
            VmType = typeof(DemoVm).AssemblyQualifiedName!,
            Schema = "bogus-fingerprint",
            State = new System.Text.Json.Nodes.JsonObject { ["count"] = 99 },
        };
        await provider.SaveAsync(record);

        await using var host = new TestHost(() => new DemoVm(),
            new SessionOptions { Provider = provider });
        var c = await host.ConnectAsync(sessionId: sessionId);

        c.SessionId.Should().Be(sessionId, "schema mismatch falls back to fresh state under the requested id");
        c.At("/count")!.GetValue<int>().Should().Be(0, "mismatched schema → fresh VM, no stale data");
    }

    [Fact]
    public async Task Hello_auth_hook_rejects_connection()
    {
        await using var host = new TestHost(() => new DemoVm(), new SessionOptions
        {
            AuthorizeHello = hello => hello.Role == "controller",
        });

        var denied = await host.ConnectAsync(role: "viewer");
        denied.FatalError.Should().NotBeNull();
        denied.FatalError!.Code.Should().Be("forbidden");

        var allowed = await host.ConnectAsync(role: "controller");
        allowed.FatalError.Should().BeNull();
        allowed.SessionId.Should().NotBeNull();
    }

    [Fact]
    public async Task No_provider_keeps_disconnecting_sessions_idle()
    {
        await using var host = new TestHost(() => new DemoVm(),
            new SessionOptions { DisconnectGrace = TimeSpan.FromMilliseconds(50) });
        var c = await host.ConnectAsync();
        await host.CommandAsync(c, "increment", 5);
        host.Disconnect(c);
        await Task.Delay(200);

        host.Manager.Sessions.Should().ContainSingle("no provider → in-memory idle, never evicted");
        await host.ReconnectAsync(c);
        c.At("/count")!.GetValue<int>().Should().Be(5);
    }

    public void Dispose()
    {
        try { Directory.Delete(_storeDir, recursive: true); } catch { /* best effort */ }
    }
}
