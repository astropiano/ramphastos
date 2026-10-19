using System.Text.Json;
using FluentAssertions;
using Astropiano.Ramphastos.TestSdk;
using Xunit;

namespace Astropiano.Ramphastos.Core.Facts;

/// <summary>
/// §5.4 round-trip check: the VM graph survives TotalSerializer end-to-end —
/// private fields, nested objects, collections, and server-only members
/// included — without capturing tracker subscriptions.
/// </summary>
public class TotalSerializerFacts
{
    [Fact]
    public void Vm_graph_roundtrips_through_TotalSerializer()
    {
        var vm = new DemoVm();
        vm.Title = "persisted";
        vm.Child.Name = "deep";
        vm.Child.Tags.Add("a");
        vm.Items.Add("x");
        vm.Doc = "some document";
        vm.InternalToken = "must-survive"; // server-only: persisted, never on the wire

        var restored = TotalSerializerRoundTrip.RoundTrip(vm);

        restored.Should().NotBeSameAs(vm);
        restored.Title.Should().Be("persisted");
        restored.Child.Name.Should().Be("deep");
        restored.Child.Tags.Should().Equal("a");
        restored.Items.Should().Equal("x");
        restored.Doc.Should().Be("some document");
        restored.InternalToken.Should().Be("must-survive");
    }

    [Fact]
    public void Restored_vm_projects_identical_sync_state()
    {
        var vm = new DemoVm();
        vm.Title = "persisted";
        vm.Items.Add("x");

        var restored = TotalSerializerRoundTrip.RoundTrip(vm);

        var before = JsonSerializer.Serialize(TotalSerializerRoundTrip.Project(vm));
        var after = JsonSerializer.Serialize(TotalSerializerRoundTrip.Project(restored));
        after.Should().Be(before, "the restored graph must replicate identically");
    }

    [Fact]
    public void Tracker_subscriptions_are_not_serialized()
    {
        var vm = new DemoVm();
        vm.Title = "x"; // fires PropertyChanged — handlers must not round-trip

        var restored = TotalSerializerRoundTrip.RoundTrip(vm);

        // The restored VM's event field must be clean: attaching the tracker
        // again on resume is the session's job, not the record's.
        var act = () => restored.Title = "mutated-after-restore";
        act.Should().NotThrow("a restored VM is standalone until a session re-attaches it");
        restored.Title.Should().Be("mutated-after-restore");
    }
}
