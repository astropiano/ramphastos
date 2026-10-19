using System.Collections.ObjectModel;
using FluentAssertions;
using Astropiano.Ramphastos.Core;
using Astropiano.Ramphastos.TestSdk;
using Xunit;

namespace Astropiano.Ramphastos.Core.Facts;

/// <summary>A fleet-wide VM shared across sessions (§6.3 signage case).</summary>
public class FleetVm : SyncedObject
{
    private string _banner = "boot";
    public string Banner
    {
        get => _banner;
        set => Set(ref _banner, value);
    }

    public ObservableCollection<string> Screens { get; } = new();
}

/// <summary>Per-sign session VM carrying a [Shared] member.</summary>
public class SignVm : ApplicationModel
{
    [Shared] public FleetVm Fleet { get; set; } = null!;

    private string _site = "hall-a";
    public string Site
    {
        get => _site;
        set => Set(ref _site, value);
    }
}

/// <summary>SharedObject: one instance, many session mirrors at member paths.</summary>
public class SharedObjectFacts
{
    private static async Task FlushAsync() => await Task.Delay(80);

    private static SessionOptions SharedOptions() => new()
    {
        SharedObjects = { [typeof(FleetVm).FullName!] = () => new FleetVm() },
    };

    [Fact]
    public async Task Shared_member_resolves_into_snapshot()
    {
        await using var host = new TestHost(() => new SignVm(), SharedOptions());
        var c = await host.ConnectAsync();

        c.At("/fleet/banner")!.GetValue<string>().Should().Be("boot");
        c.At("/site")!.GetValue<string>().Should().Be("hall-a");
    }

    [Fact]
    public async Task Shared_mutation_fans_out_to_all_sessions()
    {
        await using var host = new TestHost(() => new SignVm(), SharedOptions());
        var a = await host.ConnectAsync();
        var b = await host.ConnectAsync(); // separate session, same shared object

        host.Manager.Sessions.Should().HaveCount(2);
        host.Manager.SharedObjects.Post<FleetVm>(f => f.Banner = "live");
        await FlushAsync();

        a.At("/fleet/banner")!.GetValue<string>().Should().Be("live");
        b.At("/fleet/banner")!.GetValue<string>().Should().Be("live");
    }

    [Fact]
    public async Task Shared_ops_are_path_rebased_and_not_double_emitted()
    {
        await using var host = new TestHost(() => new SignVm(), SharedOptions());
        var c = await host.ConnectAsync();
        c.AppliedOps.Clear();

        host.Manager.SharedObjects.Post<FleetVm>(f =>
        {
            f.Banner = "once";
            f.Screens.Add("north");
        });
        await FlushAsync();

        c.AppliedOps.Count(o => o.Path == "/fleet/banner").Should().Be(1,
            "the session's own tracker must not also emit for the shared subtree");
        c.AppliedOps.Should().Contain(o => o.Op == "add" && o.Path == "/fleet/screens/0",
            "collection ops inside the shared object rebase under the member path");
        c.At("/fleet/screens/0")!.GetValue<string>().Should().Be("north");
    }

    [Fact]
    public async Task Write_intent_on_shared_member_is_forbidden()
    {
        await using var host = new TestHost(() => new SignVm(), SharedOptions());
        var c = await host.ConnectAsync();

        var act = () => host.WriteAsync(c, "fleet", new { banner = "hijack" });
        await act.Should().ThrowAsync<RamphastosTestException>()
            .Where(e => e.Error.Code == "forbidden");
    }

    [Fact]
    public async Task Unregistered_shared_member_stays_null()
    {
        await using var host = new TestHost(() => new SignVm()); // no factories
        var c = await host.ConnectAsync();
        c.At("/fleet").Should().BeNull("no registry entry → member unresolved");
    }

    [Fact]
    public async Task Shared_state_is_live_not_persisted()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rph-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var provider = new SessionProviderFiles(dir);
            var options = SharedOptions();
            string sessionId;
            {
                var opt = new SessionOptions
                {
                    Provider = provider,
                    DisconnectGrace = TimeSpan.FromMilliseconds(60),
                    SharedObjects = { [typeof(FleetVm).FullName!] = () => new FleetVm() },
                };
                await using var host = new TestHost(() => new SignVm(), opt);
                var c = await host.ConnectAsync();
                sessionId = c.SessionId!;
                host.Manager.SharedObjects.Post<FleetVm>(f => f.Banner = "before-suspend");
                await FlushAsync();
                host.Disconnect(c);
                await Task.Delay(300);
                host.Manager.Sessions.Should().BeEmpty();

                var record = await provider.LoadAsync(sessionId);
                record!.State!["fleet"].Should().BeNull(
                    "shared state is live cross-session state — it never enters records");
            }

            // New host, same provider: resumed session re-subscribes to the
            // NEW host's live shared object (hydration skips shared members).
            var opt2 = new SessionOptions
            {
                Provider = provider,
                SharedObjects = { [typeof(FleetVm).FullName!] = () => new FleetVm() },
            };
            await using var host2 = new TestHost(() => new SignVm(), opt2);
            host2.Manager.SharedObjects.Post<FleetVm>(f => f.Banner = "current-live");
            await Task.Delay(50);
            var c2 = await host2.ConnectAsync(sessionId: sessionId);
            c2.At("/fleet/banner")!.GetValue<string>().Should().Be("current-live",
                "the mirror shows the live shared object, not stale persisted state");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public async Task Session_disposal_unsubscribes_from_shared_object()
    {
        await using var host = new TestHost(() => new SignVm(), SharedOptions());
        var c = await host.ConnectAsync();
        var shared = host.Manager.SharedObjects.Get<FleetVm>()!;
        shared.SubscriberCount.Should().Be(1);

        await host.Manager.Sessions.Single().DisposeAsync();
        shared.SubscriberCount.Should().Be(0);
    }
}
