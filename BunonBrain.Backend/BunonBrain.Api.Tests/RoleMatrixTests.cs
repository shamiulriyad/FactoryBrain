using System.IdentityModel.Tokens.Jwt;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using Microsoft.Extensions.DependencyInjection;
using P = BunonBrain.Application.Access.Permissions;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// The role → permission matrix and the grant rules, checked against the
/// requirements for each of the eight predefined roles. Pure catalog logic
/// plus token round-trips; no database.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class RoleMatrixTests
{
    private readonly ApiFactory _factory;
    public RoleMatrixTests(ApiFactory factory) { _factory = factory; factory.Activate(); }

    private static IReadOnlyList<string> PermsOf(string role) => AccessCatalog.Role(role)!.Permissions;

    [Fact]
    public void There_are_exactly_the_eight_predefined_roles()
    {
        Assert.Equal(
            new[] { R.SuperAdmin, R.FactoryAdmin, R.FactoryManager, R.ProductionSupervisor, R.MonitoringOperator, R.HrOfficer, R.DeviceAdmin, R.Worker }.OrderBy(x => x),
            AccessCatalog.AllRoles.Select(r => r.Id).OrderBy(x => x));
        Assert.All(AccessCatalog.AllRoles, r => Assert.All(r.Permissions, p => Assert.NotNull(AccessCatalog.Permission(p))));
    }

    [Fact]
    public void Super_admin_holds_every_permission_and_platform_permissions_reach_no_one_else()
    {
        Assert.Equal(AccessCatalog.AllPermissions.Select(p => p.Code).OrderBy(x => x), PermsOf(R.SuperAdmin).OrderBy(x => x));
        var platformOnly = AccessCatalog.AllPermissions.Where(p => p.PlatformOnly).Select(p => p.Code).ToList();
        Assert.Contains(P.FactoriesManage, platformOnly);
        Assert.Contains(P.PlatformSettingsManage, platformOnly);
        foreach (var role in AccessCatalog.AllRoles.Where(r => r.Id != R.SuperAdmin))
            Assert.Empty(role.Permissions.Intersect(platformOnly));
    }

    [Fact]
    public void Factory_admin_holds_every_factory_level_permission()
    {
        var factoryLevel = AccessCatalog.AllPermissions.Where(p => !p.PlatformOnly).Select(p => p.Code).OrderBy(x => x);
        Assert.Equal(factoryLevel, PermsOf(R.FactoryAdmin).OrderBy(x => x));
        Assert.Equal(RoleScope.Factory, AccessCatalog.Role(R.FactoryAdmin)!.Scope);
    }

    [Fact]
    public void Factory_manager_oversees_operations_and_approvals_but_not_people_or_configuration()
    {
        var p = PermsOf(R.FactoryManager);
        Assert.Contains(P.ProductionView, p);
        Assert.Contains(P.WorkersView, p);
        Assert.Contains(P.AttendanceView, p);
        Assert.Contains(P.OvertimeView, p);
        Assert.Contains(P.ReportsView, p);
        Assert.Contains(P.ApprovalsDecide, p);
        Assert.DoesNotContain(P.ApprovalsDecideHigh, p);
        Assert.DoesNotContain(P.UsersView, p);
        Assert.DoesNotContain(P.RolesAssign, p);
        Assert.DoesNotContain(P.WorkersManage, p);
        Assert.DoesNotContain(P.SettingsManage, p);
    }

    [Fact]
    public void Production_supervisor_is_line_scoped_and_assigns_workstations()
    {
        var role = AccessCatalog.Role(R.ProductionSupervisor)!;
        Assert.Equal(RoleScope.Line, role.Scope);
        Assert.Contains(P.WorkersAssign, role.Permissions);
        Assert.Contains(P.WorkersView, role.Permissions);
        Assert.Contains(P.ProductionView, role.Permissions);
        Assert.Contains(P.ProductionAlertsReview, role.Permissions);
        Assert.DoesNotContain(P.WorkersManage, role.Permissions);
        Assert.DoesNotContain(P.AttendanceManage, role.Permissions);
        Assert.DoesNotContain(P.UsersView, role.Permissions);
    }

    [Fact]
    public void Monitoring_operator_watches_and_acknowledges_but_cannot_change_attendance_or_worker_records()
    {
        var p = PermsOf(R.MonitoringOperator);
        Assert.Contains(P.MonitoringView, p);
        Assert.Contains(P.MonitoringAlertsReview, p);
        Assert.Contains(P.MonitoringAlertsEscalate, p);
        Assert.Contains(P.CamerasView, p);
        Assert.Contains(P.WorkersView, p);
        foreach (var forbidden in new[] { P.AttendanceManage, P.WorkersManage, P.WorkersAssign, P.OvertimeManage, P.LeaveManage, P.UsersUpdate })
            Assert.DoesNotContain(forbidden, p);
    }

    [Fact]
    public void Hr_officer_manages_workers_attendance_shifts_leave_and_overtime()
    {
        var p = PermsOf(R.HrOfficer);
        foreach (var required in new[] { P.WorkersManage, P.AttendanceManage, P.ShiftsManage, P.LeaveManage, P.OvertimeManage, P.AttendanceView })
            Assert.Contains(required, p);
        Assert.DoesNotContain(P.DevicesManage, p);
        Assert.DoesNotContain(P.UsersCreate, p);
    }

    [Fact]
    public void Device_administrator_manages_devices_but_not_hr_or_salary()
    {
        var p = PermsOf(R.DeviceAdmin);
        foreach (var required in new[] { P.DevicesView, P.DevicesManage, P.DevicesLogsView, P.CamerasView })
            Assert.Contains(required, p);
        foreach (var forbidden in new[] { P.WorkersManage, P.AttendanceManage, P.OvertimeManage, P.LeaveManage, P.ShiftsManage, P.WorkersView })
            Assert.DoesNotContain(forbidden, p);
    }

    [Fact]
    public void Worker_sees_only_their_own_records()
    {
        Assert.Equal(new[] { P.NotificationsView, P.SelfView }.OrderBy(x => x), PermsOf(R.Worker).OrderBy(x => x));
        Assert.Equal(RoleScope.Self, AccessCatalog.Role(R.Worker)!.Scope);
    }

    [Fact]
    public void Super_admin_grants_every_role_factory_admin_grants_only_lower_roles_and_nobody_else_grants_anything()
    {
        string[] Grantable(string role) => AccessCatalog.GrantableBy(new[] { role }, PermsOf(role).ToArray()).OrderBy(x => x).ToArray();

        Assert.Equal(AccessCatalog.AllRoles.Select(r => r.Id).OrderBy(x => x), Grantable(R.SuperAdmin));
        Assert.Equal(new[] { R.FactoryManager, R.ProductionSupervisor, R.MonitoringOperator, R.HrOfficer, R.DeviceAdmin, R.Worker }.OrderBy(x => x),
            Grantable(R.FactoryAdmin));
        foreach (var role in new[] { R.FactoryManager, R.ProductionSupervisor, R.MonitoringOperator, R.HrOfficer, R.DeviceAdmin, R.Worker })
            Assert.Empty(Grantable(role));
    }

    [Fact]
    public void Nobody_can_grant_a_role_whose_permissions_they_lack()
    {
        // A Factory Admin token missing one permission of Factory Manager can't grant it.
        var perms = PermsOf(R.FactoryAdmin).Where(p => p != P.AgentsRun).ToArray();
        Assert.False(AccessCatalog.CanGrant(new[] { R.FactoryAdmin }, perms, R.FactoryManager));
        Assert.True(AccessCatalog.CanGrant(new[] { R.FactoryAdmin }, perms, R.Worker));
        Assert.False(AccessCatalog.CanGrant(new[] { R.FactoryAdmin }, PermsOf(R.FactoryAdmin).ToArray(), "Root"));
    }

    [Fact]
    public void Scope_is_the_widest_of_the_roles()
    {
        Assert.Equal(RoleScope.Self, AccessCatalog.ScopeFor(Array.Empty<string>()));
        Assert.Equal(RoleScope.Line, AccessCatalog.ScopeFor(new[] { R.Worker, R.ProductionSupervisor }));
        Assert.Equal(RoleScope.Factory, AccessCatalog.ScopeFor(new[] { R.ProductionSupervisor, R.HrOfficer }));
        Assert.Equal(RoleScope.Platform, AccessCatalog.ScopeFor(new[] { R.Worker, R.SuperAdmin }));
        Assert.Equal(R.FactoryManager, AccessCatalog.PrimaryRole(new[] { R.Worker, R.FactoryManager, R.ProductionSupervisor }));
    }

    [Fact]
    public void Access_context_scopes_factories_and_lines()
    {
        var f1 = Guid.NewGuid();
        var f2 = Guid.NewGuid();
        var supervisor = new AccessContext(Guid.NewGuid(), "s@x", new[] { R.ProductionSupervisor }, PermsOf(R.ProductionSupervisor), new[] { f1 }, new[] { "line-1" });
        Assert.True(supervisor.IsLineScoped);
        Assert.True(supervisor.CanAccessLine("line-1", f1));
        Assert.False(supervisor.CanAccessLine("line-2", f1));
        Assert.False(supervisor.CanAccessLine("line-1", f2));
        Assert.False(supervisor.CanAccessFactory(f2));

        var hr = new AccessContext(Guid.NewGuid(), "h@x", new[] { R.HrOfficer }, PermsOf(R.HrOfficer), new[] { f1 }, Array.Empty<string>());
        Assert.True(hr.CanAccessLine("line-9", f1));
        Assert.False(hr.CanAccessLine("line-9", f2));
        Assert.False(hr.CanAccessLine("line-9", null));

        var super = new AccessContext(Guid.NewGuid(), "a@x", new[] { R.SuperAdmin }, PermsOf(R.SuperAdmin), Array.Empty<Guid>(), Array.Empty<string>());
        Assert.True(super.CanAccessFactory(f2));
        Assert.True(super.IsPlatform);
    }

    [Fact]
    public void Issued_tokens_carry_roles_permissions_scope_and_stamp_and_round_trip_into_the_access_context()
    {
        var factory = new FactoryRef(Guid.NewGuid(), "DHK-1", "Dhaka 1");
        var snapshot = TestAccess.Snapshot(Guid.NewGuid(), "sup@tests.local", new[] { R.ProductionSupervisor },
            PermsOf(R.ProductionSupervisor), "stamp-123", new[] { factory }, new[] { "line-4" });
        var token = _factory.Services.GetRequiredService<ITokenService>().IssueAccessToken(snapshot, out var ttl);
        Assert.InRange(ttl, 60, 3600);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("stamp-123", jwt.Claims.Single(c => c.Type == AccessClaims.SecurityStamp).Value);
        Assert.Equal(PermsOf(R.ProductionSupervisor).OrderBy(x => x),
            jwt.Claims.Where(c => c.Type == AccessClaims.Permission).Select(c => c.Value).OrderBy(x => x));
        Assert.Equal(factory.Id.ToString(), jwt.Claims.Single(c => c.Type == AccessClaims.Factory).Value);
        Assert.Equal("line-4", jwt.Claims.Single(c => c.Type == AccessClaims.Line).Value);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == AccessClaims.MustChangePassword);
    }
}
