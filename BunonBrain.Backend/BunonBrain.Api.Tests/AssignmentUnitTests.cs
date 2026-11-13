using BunonBrain.Application.Access;
using BunonBrain.Application.Assignments;
using BunonBrain.Application.Dtos.Assignments;
using BunonBrain.Application.Validation;
using P = BunonBrain.Application.Access.Permissions;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>Workstation assignment rules that need no database: occupancy, times, codes, validation and role permissions.</summary>
public sealed class AssignmentUnitTests
{
    [Theory]
    [InlineData(true, 0, 1, "Available")]
    [InlineData(true, 1, 1, "CapacityReached")]
    [InlineData(true, 1, 3, "Occupied")]
    [InlineData(true, 3, 3, "CapacityReached")]
    [InlineData(true, 4, 3, "CapacityReached")]
    [InlineData(false, 0, 1, "Inactive")]
    [InlineData(false, 2, 3, "Inactive")]
    public void Occupancy_follows_assigned_workers_and_capacity(bool active, int assigned, int capacity, string expected) =>
        Assert.Equal(expected, WorkstationOccupancy.Of(active, assigned, capacity));

    [Fact]
    public void Effective_times_must_be_now_or_recent_and_after_the_current_assignment()
    {
        var now = new DateTimeOffset(2026, 11, 13, 11, 30, 0, TimeSpan.FromHours(6));
        Assert.Null(AssignmentRules.EffectiveTimeProblem(now, now));
        Assert.Null(AssignmentRules.EffectiveTimeProblem(now.AddMinutes(4), now));           // clock skew
        Assert.NotNull(AssignmentRules.EffectiveTimeProblem(now.AddMinutes(10), now));       // future
        Assert.Null(AssignmentRules.EffectiveTimeProblem(now.AddDays(-30), now));            // late paperwork
        Assert.NotNull(AssignmentRules.EffectiveTimeProblem(now.AddDays(-32), now));         // too old
        var started = now.AddHours(-3);
        Assert.Null(AssignmentRules.EffectiveTimeProblem(now.AddHours(-1), now, started));
        Assert.NotNull(AssignmentRules.EffectiveTimeProblem(started, now, started));          // no zero-length assignment
        Assert.NotNull(AssignmentRules.EffectiveTimeProblem(started.AddMinutes(-1), now, started));
    }

    [Fact]
    public void Bulk_codes_are_numbered_and_padded()
    {
        Assert.Equal(new[] { "T-001", "T-002", "T-003" }, AssignmentRules.BulkCodes("t-", 1, 3));
        Assert.Equal(new[] { "L3-T099", "L3-T100" }, AssignmentRules.BulkCodes("L3-T", 99, 2));
        Assert.All(AssignmentRules.BulkCodes("T-", 1, 200), c => Assert.True(AssignmentRules.IsCode(c)));
    }

    [Theory]
    [InlineData("T-025", true)]
    [InlineData("t-025", true)]
    [InlineData("L1/T.03", true)]
    [InlineData("", false)]
    [InlineData("-T1", false)]
    [InlineData("T 25", false)]
    [InlineData("T-0000000000000000000000025", false)]
    public void Workstation_and_machine_codes_are_checked(string code, bool ok) => Assert.Equal(ok, AssignmentRules.IsCode(code));

    [Fact]
    public void Requests_are_validated()
    {
        Assert.False(new CreateAssignmentRequestValidator().Validate(new CreateAssignmentRequest(Guid.Empty, Guid.Empty)).IsValid);
        Assert.True(new CreateAssignmentRequestValidator().Validate(new CreateAssignmentRequest(Guid.NewGuid(), Guid.NewGuid())).IsValid);
        Assert.False(new TransferAssignmentRequestValidator().Validate(new TransferAssignmentRequest(Guid.NewGuid(), " ")).IsValid);   // reason required
        Assert.True(new TransferAssignmentRequestValidator().Validate(new TransferAssignmentRequest(Guid.NewGuid(), "Line balancing")).IsValid);
        Assert.False(new EndAssignmentRequestValidator().Validate(new EndAssignmentRequest("")).IsValid);
        var ws = new UpdateWorkstationRequestValidator();
        Assert.False(ws.Validate(new UpdateWorkstationRequest("line-1", Guid.NewGuid(), null, null, 0, 1, true)).IsValid);
        Assert.False(ws.Validate(new UpdateWorkstationRequest("line-1", Guid.NewGuid(), null, null, 21, 1, true)).IsValid);
        Assert.True(ws.Validate(new UpdateWorkstationRequest("line-1", Guid.NewGuid(), null, null, 2, 1, true)).IsValid);
        Assert.False(new BulkCreateWorkstationsRequestValidator().Validate(new BulkCreateWorkstationsRequest(Guid.NewGuid(), "line-1", Guid.NewGuid(), "T-", 1, 201)).IsValid);
        Assert.False(new CreateWorkstationRequestValidator().Validate(new CreateWorkstationRequest(Guid.NewGuid(), "line-1", Guid.NewGuid(), "bad code")).IsValid);
    }

    private static IReadOnlyList<string> PermsOf(string role) => AccessCatalog.Role(role)!.Permissions;

    [Fact]
    public void Who_may_manage_and_who_may_only_view_assignments()
    {
        foreach (var role in new[] { R.SuperAdmin, R.FactoryAdmin, R.FactoryManager, R.ProductionSupervisor })
            Assert.Contains(P.WorkersAssign, PermsOf(role));
        foreach (var role in new[] { R.MonitoringOperator, R.HrOfficer })
        {
            Assert.Contains(P.WorkersView, PermsOf(role));      // read the assignments
            Assert.DoesNotContain(P.WorkersAssign, PermsOf(role));
        }
        // IT / device administration: no assignment access, workstation list without workers.
        Assert.DoesNotContain(P.WorkersView, PermsOf(R.DeviceAdmin));
        Assert.DoesNotContain(P.WorkersAssign, PermsOf(R.DeviceAdmin));
        // Worker: own records only.
        Assert.Equal(new[] { P.NotificationsView, P.SelfView }.OrderBy(x => x), PermsOf(R.Worker).OrderBy(x => x));
        // Configuring workstations is an administrator's job.
        foreach (var role in new[] { R.FactoryManager, R.ProductionSupervisor, R.HrOfficer, R.MonitoringOperator })
            Assert.DoesNotContain(P.LinesManage, PermsOf(role));
    }
}
