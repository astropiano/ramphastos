using System.Text.Json.Nodes;
using FluentAssertions;
using Astropiano.Ramphastos.Core;
using Astropiano.Ramphastos.TestSdk;
using Xunit;

namespace Astropiano.Ramphastos.Core.Facts;

/// <summary>VM with state the STJ projection can't see: private fields only.</summary>
public class WiredVm : ApplicationModel
{
    // No property — invisible to the member-model projection entirely.
    private readonly List<string> _auditLog = new();
    private int _count;

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    [Command]
    public Task Note(string entry)
    {
        _auditLog.Add(entry);
        return Task.CompletedTask;
    }

    public IReadOnlyList<string> AuditEntries => _auditLog;
}

/// <summary>Record codec: stj projection (default) vs. TotalSerializer whole-graph (§5.4).</summary>
public class SessionCodecFacts : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rph-codec-" + Guid.NewGuid().ToString("N"));

    private SessionOptions Opts(ISessionStateCodec? codec) => new()
    {
        Provider = new SessionProviderFiles(_dir),
        DisconnectGrace = TimeSpan.FromMilliseconds(60),
        Codec = codec,
    };

    private static async Task<string> SuspendSession<TVm>(Func<TVm> factory, SessionOptions options,
        Action<TVm> mutate) where TVm : ApplicationModel
    {
        await using var host = new TestHost(() => factory(), options);
        var c = await host.ConnectAsync();
        mutate((TVm)host.Manager.Sessions.Single().Root);
        await Task.Delay(80); // let mutations flush into the graph
        host.Disconnect(c);
        await Task.Delay(300);
        return c.SessionId!;
    }

    [Fact]
    public async Task Stj_record_format_is_tagged_and_resumes()
    {
        var id = await SuspendSession(() => new DemoVm(), Opts(codec: null),
            vm => vm.Count = 42);

        var record = await new SessionProviderFiles(_dir).LoadAsync(id);
        (record!.StateFormat ?? "stj").Should().Be("stj");
        record.State.Should().BeOfType<JsonObject>();

        await using var host2 = new TestHost(() => new DemoVm(), Opts(codec: null));
        var c2 = await host2.ConnectAsync(sessionId: id);
        c2.At("/count")!.GetValue<int>().Should().Be(42);
    }

    [Fact]
    public async Task Ts_codec_records_whole_graph_including_private_wiring()
    {
        var id = await SuspendSession(() => new WiredVm(), Opts(new SessionStateCodecTotalSerializer()),
            vm => { vm.Count = 7; vm.Note("a"); vm.Note("b"); });

        var record = await new SessionProviderFiles(_dir).LoadAsync(id);
        record!.StateFormat.Should().Be("ts");
        record.State.Should().BeAssignableTo<JsonValue>("ts state is an opaque string blob");

        await using var host2 = new TestHost(() => new WiredVm(), Opts(new SessionStateCodecTotalSerializer()));
        var c2 = await host2.ConnectAsync(sessionId: id);
        c2.At("/count")!.GetValue<int>().Should().Be(7);

        // The private audit log — unreachable for the stj projection — survived.
        var vm2 = (WiredVm)host2.Manager.Sessions.Single().Root;
        vm2.AuditEntries.Should().Equal("a", "b");
    }

    [Fact]
    public async Task Ts_codec_reads_legacy_stj_records_as_upgrade()
    {
        // Suspended under the default codec…
        var id = await SuspendSession(() => new DemoVm(), Opts(codec: null), vm => vm.Count = 11);

        // …resumed under the ts codec — hydrates via the projection path.
        await using var host = new TestHost(() => new DemoVm(), Opts(new SessionStateCodecTotalSerializer()));
        var c = await host.ConnectAsync(sessionId: id);
        c.At("/count")!.GetValue<int>().Should().Be(11);
    }

    [Fact]
    public async Task Stj_codec_rejects_ts_records()
    {
        var id = await SuspendSession(() => new WiredVm(), Opts(new SessionStateCodecTotalSerializer()),
            vm => vm.Count = 9);

        await using var host = new TestHost(() => new WiredVm(), Opts(codec: null));
        var c = await host.ConnectAsync(sessionId: id);
        c.SessionId.Should().NotBe(id, "unreadable format must mint a fresh session");
        c.At("/count")!.GetValue<int>().Should().Be(0);
    }

    [Fact]
    public async Task Migrator_maps_drifted_records_onto_the_new_schema()
    {
        var provider = new SessionProviderFiles(_dir);
        var sessionId = "mig-" + Guid.NewGuid().ToString("N")[..8];

        // Record written by an "older version" — different schema, old field name.
        var record = new SessionRecord
        {
            SessionId = sessionId,
            VmType = typeof(DemoVm).AssemblyQualifiedName!,
            Schema = "sha1:old",
            State = new JsonObject { ["count"] = 99 },
        };
        await provider.SaveAsync(record);

        var options = Opts(codec: null);
        options.Migrators.Add(new TestMigrator());
        await using var host = new TestHost(() => new DemoVm(), options);
        var c = await host.ConnectAsync(sessionId: sessionId);

        c.SessionId.Should().Be(sessionId, "migrator accepted the record → resume, not fresh");
        c.At("/count")!.GetValue<int>().Should().Be(199, "migrator transformed the state");
    }

    [Fact]
    public async Task Migrator_rejection_falls_back_to_fresh_session()
    {
        var provider = new SessionProviderFiles(_dir);
        var sessionId = "mig-" + Guid.NewGuid().ToString("N")[..8];
        await provider.SaveAsync(new SessionRecord
        {
            SessionId = sessionId,
            VmType = typeof(DemoVm).AssemblyQualifiedName!,
            Schema = "sha1:old",
            State = new JsonObject { ["count"] = 1 },
        });

        var options = Opts(codec: null);
        options.Migrators.Add(new RejectingMigrator());
        await using var host = new TestHost(() => new DemoVm(), options);
        var c = await host.ConnectAsync(sessionId: sessionId);
        c.SessionId.Should().NotBe(sessionId);
        c.At("/count")!.GetValue<int>().Should().Be(0);
    }

    private sealed class TestMigrator : ISessionMigrator
    {
        public bool CanMigrate(string fromSchema, string toSchema) => fromSchema == "sha1:old";

        public JsonNode? Migrate(JsonNode oldState, string fromSchema, string toSchema)
        {
            if (oldState is not JsonObject obj) return null;
            var migrated = (JsonObject)obj.DeepClone();
            migrated["count"] = migrated["count"]!.GetValue<int>() + 100;
            return migrated;
        }
    }

    private sealed class RejectingMigrator : ISessionMigrator
    {
        public bool CanMigrate(string fromSchema, string toSchema) => true;
        public JsonNode? Migrate(JsonNode oldState, string fromSchema, string toSchema) => null;
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }
}
