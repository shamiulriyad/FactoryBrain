namespace BunonBrain.Application.Access;

/// <summary>
/// How far a role's access reaches. A user's effective reach is the widest
/// scope among their roles (Platform &gt; Factory &gt; Line &gt; Self).
/// </summary>
public enum RoleScope
{
    /// <summary>Only the user's own records (their linked worker profile).</summary>
    Self = 0,
    /// <summary>Only the production lines assigned to the user, inside their factories.</summary>
    Line = 1,
    /// <summary>Everything inside the factories assigned to the user.</summary>
    Factory = 2,
    /// <summary>Every factory on the platform.</summary>
    Platform = 3,
}

/// <summary>One permission of the catalog.</summary>
/// <param name="Code">Stable dotted code, e.g. <c>users.create</c>. Stored in the JWT <c>perm</c> claim.</param>
/// <param name="Module">Module the permission belongs to (grouping in the UI and docs).</param>
/// <param name="PlatformOnly">Only platform roles may ever hold it; it can never reach a factory-level role.</param>
/// <param name="Reserved">Defined now so roles are stable, but no endpoint uses it until its module ships.</param>
public sealed record PermissionDefinition(
    string Code, string Module, string Name, string Description, bool PlatformOnly = false, bool Reserved = false);

/// <summary>One predefined system role.</summary>
/// <param name="Id">Stable identifier, used in the JWT role claim and the <c>roles</c> table.</param>
/// <param name="Rank">Higher rank = more authority. Used only for ordering and display.</param>
/// <param name="ManagedBy">Roles whose holders may grant or remove this role (and manage its holders).</param>
public sealed record RoleDefinition(
    string Id,
    string Name,
    string Description,
    RoleScope Scope,
    int Rank,
    IReadOnlyList<string> ManagedBy,
    IReadOnlyList<string> Permissions);

/// <summary>Permission codes. Use these constants, never string literals, in endpoint attributes.</summary>
public static class Permissions
{
    // Platform
    public const string FactoriesView          = "factories.view";
    public const string FactoriesManage        = "factories.manage";
    public const string PlatformSettingsManage = "platform.settings.manage";
    public const string KnowledgeManage        = "knowledge.manage";
    public const string InquiriesManage        = "inquiries.manage";

    // Users & access
    public const string UsersView          = "users.view";
    public const string UsersCreate        = "users.create";
    public const string UsersUpdate        = "users.update";
    public const string UsersDeactivate    = "users.deactivate";
    public const string UsersResetPassword = "users.reset_password";
    public const string RolesView          = "roles.view";
    public const string RolesAssign        = "roles.assign";
    public const string AuditView          = "audit.view";

    // Organization
    public const string LinesView         = "lines.view";
    public const string LinesManage       = "lines.manage";
    public const string DepartmentsManage = "departments.manage";
    public const string SettingsManage    = "settings.manage";

    // Workforce
    public const string WorkersView        = "workers.view";
    public const string WorkersDetailsView = "workers.details.view";
    public const string WorkersManage      = "workers.manage";
    public const string WorkersAssign      = "workers.assign";

    // Attendance & shifts
    public const string AttendanceView   = "attendance.view";
    public const string AttendanceManage = "attendance.manage";
    public const string ShiftsManage     = "shifts.manage";
    public const string LeaveManage      = "leave.manage";
    public const string OvertimeView     = "overtime.view";
    public const string OvertimeManage   = "overtime.manage";

    // Monitoring
    public const string MonitoringView           = "monitoring.view";
    public const string MonitoringAlertsReview   = "monitoring.alerts.review";
    public const string MonitoringAlertsEscalate = "monitoring.alerts.escalate";
    public const string CamerasView              = "cameras.view";

    // Production, quality, merchandising, approvals
    public const string ProductionView         = "production.view";
    public const string ProductionManage       = "production.manage";
    public const string ProductionSetup        = "production.setup";
    public const string ProductionAlertsReview = "production.alerts.review";
    public const string QcView                 = "qc.view";
    public const string QcManage               = "qc.manage";
    public const string MerchandisingView      = "merchandising.view";
    public const string MerchandisingManage    = "merchandising.manage";
    public const string ApprovalsView          = "approvals.view";
    public const string ApprovalsDecide        = "approvals.decide";
    public const string ApprovalsDecideHigh    = "approvals.decide_high";

    // Devices
    public const string DevicesView     = "devices.view";
    public const string DevicesManage   = "devices.manage";
    public const string DevicesLogsView = "devices.logs.view";

    // Reports, knowledge & AI
    public const string ReportsView   = "reports.view";
    public const string KnowledgeView = "knowledge.view";
    public const string AgentsRun     = "agents.run";

    // Self-service
    public const string SelfView          = "self.view";
    public const string NotificationsView = "notifications.view";
}

/// <summary>Ids of the eight predefined system roles.</summary>
public static class SystemRoles
{
    public const string SuperAdmin           = "SuperAdmin";
    public const string FactoryAdmin         = "FactoryAdmin";
    public const string FactoryManager       = "FactoryManager";
    public const string ProductionSupervisor = "ProductionSupervisor";
    public const string MonitoringOperator   = "MonitoringOperator";
    public const string HrOfficer            = "HrOfficer";
    public const string DeviceAdmin          = "DeviceAdmin";
    public const string Worker               = "Worker";
}

/// <summary>
/// The access-control catalog: every permission, every system role and
/// which permissions each role grants. This is the single source of truth:
/// the startup seeder projects it into the <c>permissions</c>, <c>roles</c>
/// and <c>role_permissions</c> tables (for referential integrity and SQL
/// reporting), and tokens are issued from it. System roles are protected:
/// no API can edit them.
/// </summary>
public static class AccessCatalog
{
    private static PermissionDefinition P(string code, string module, string name, string description,
        bool platformOnly = false, bool reserved = false) => new(code, module, name, description, platformOnly, reserved);

    public static readonly IReadOnlyList<PermissionDefinition> AllPermissions = new[]
    {
        P(Permissions.FactoriesView,          "Platform", "View factories", "See the factories you belong to (Super Admin: every factory)."),
        P(Permissions.FactoriesManage,        "Platform", "Manage factories", "Create, rename, deactivate and reactivate factories.", platformOnly: true),
        P(Permissions.PlatformSettingsManage, "Platform", "Manage platform settings", "AI providers, API keys and embedding settings shared by every factory.", platformOnly: true),
        P(Permissions.KnowledgeManage,        "Platform", "Manage knowledge base", "Ingest documents and rebuild the shared knowledge-base index.", platformOnly: true),
        P(Permissions.InquiriesManage,        "Platform", "Manage website inquiries", "Read and triage demo requests, contact and support messages sent from the public website.", platformOnly: true),

        P(Permissions.UsersView,          "Users & access", "View users", "List and open user accounts in your scope."),
        P(Permissions.UsersCreate,        "Users & access", "Create users", "Create login accounts in your factory."),
        P(Permissions.UsersUpdate,        "Users & access", "Edit users", "Edit names and contact details, and factory / line scope."),
        P(Permissions.UsersDeactivate,    "Users & access", "Activate / deactivate users", "Block or restore sign-in. Deactivation ends every session at once."),
        P(Permissions.UsersResetPassword, "Users & access", "Reset passwords", "Set a temporary password that must be changed at next sign-in."),
        P(Permissions.RolesView,          "Users & access", "View roles & permissions", "See roles, what they allow and who holds them."),
        P(Permissions.RolesAssign,        "Users & access", "Assign roles", "Grant or remove roles you are allowed to manage."),
        P(Permissions.AuditView,          "Users & access", "View audit log", "Read the audit trail for your scope."),

        P(Permissions.LinesView,         "Organization", "View production lines", "See the lines of your factory (supervisors: assigned lines)."),
        P(Permissions.LinesManage,       "Organization", "Manage production lines", "Create lines, production floors, workstations (with their capacity) and machines, and attach lines to a factory."),
        P(Permissions.DepartmentsManage, "Organization", "Manage departments", "Create, rename and (de)activate the departments of a factory."),
        P(Permissions.SettingsManage,    "Organization", "Manage factory settings", "Factory-level configuration such as QC defect categories."),

        P(Permissions.WorkersView,        "Workforce", "View workers", "The worker directory in your scope: Worker ID, name, photo, placement, designation and status, and workstation assignments and their history. No personal or employment details."),
        P(Permissions.WorkersDetailsView, "Workforce", "View employment details", "Employee number, joining date, grade and status history of workers, and export of the worker list. No personal contact details."),
        P(Permissions.WorkersManage,      "Workforce", "Manage workers", "Register, edit and import workers, change their status, issue cards, and see their personal details."),
        P(Permissions.WorkersAssign,      "Workforce", "Assign workstations", "Assign workers to workstations, transfer them and end assignments, in your scope (supervisors: their lines)."),

        P(Permissions.AttendanceView,   "Attendance", "View attendance", "See attendance and shift records.", reserved: true),
        P(Permissions.AttendanceManage, "Attendance", "Manage attendance", "Record attendance and process corrections.", reserved: true),
        P(Permissions.ShiftsManage,     "Attendance", "Manage shift assignments", "Assign workers to shifts.", reserved: true),
        P(Permissions.LeaveManage,      "Attendance", "Manage leave", "Record and approve leave.", reserved: true),
        P(Permissions.OvertimeView,     "Attendance", "View overtime", "See overtime requests and records.", reserved: true),
        P(Permissions.OvertimeManage,   "Attendance", "Manage overtime", "Create and approve overtime requests.", reserved: true),

        P(Permissions.MonitoringView,           "Monitoring", "View monitoring", "Floor alerts, line board and live floor status."),
        P(Permissions.MonitoringAlertsReview,   "Monitoring", "Review monitoring alerts", "Acknowledge and resolve floor alerts."),
        P(Permissions.MonitoringAlertsEscalate, "Monitoring", "Escalate alerts", "Escalate uncertain events to supervisors.", reserved: true),
        P(Permissions.CamerasView,              "Monitoring", "View camera feeds", "Watch assigned camera feeds and AI results.", reserved: true),

        P(Permissions.ProductionView,         "Production", "View production", "Plans, orders, output, efficiency and line balancing."),
        P(Permissions.ProductionManage,       "Production", "Manage production", "Record output, edit orders and plans, run line balancing."),
        P(Permissions.ProductionSetup,        "Production", "Configure production", "Shifts, styles and operation bulletins."),
        P(Permissions.ProductionAlertsReview, "Production", "Handle production alerts", "Acknowledge production-related floor alerts."),
        P(Permissions.QcView,                 "Production", "View quality", "QC inspections, defects and quality summaries."),
        P(Permissions.QcManage,               "Production", "Manage quality", "Upload inspections, review findings and flag defects."),
        P(Permissions.MerchandisingView,      "Production", "View merchandising", "Buyer orders, milestones and documents."),
        P(Permissions.MerchandisingManage,    "Production", "Manage merchandising", "Edit buyer orders, milestones and documents."),
        P(Permissions.ApprovalsView,          "Production", "View approvals", "See the AI recommendation approval queue."),
        P(Permissions.ApprovalsDecide,        "Production", "Decide approvals", "Approve or reject low- and medium-risk recommendations."),
        P(Permissions.ApprovalsDecideHigh,    "Production", "Decide high-risk approvals", "Approve or reject high-risk recommendations."),

        P(Permissions.DevicesView,     "Devices", "View devices", "Sensors, RFID readers, gateways and their health."),
        P(Permissions.DevicesManage,   "Devices", "Manage devices", "Configure device integrations and send device readings."),
        P(Permissions.DevicesLogsView, "Devices", "View technical logs", "Device and integration logs.", reserved: true),

        P(Permissions.ReportsView,   "Reports & AI", "View reports", "Morning brief and operational reports."),
        P(Permissions.KnowledgeView, "Reports & AI", "Use the assistant", "Ask questions, browse the knowledge base and see AI agents."),
        P(Permissions.AgentsRun,     "Reports & AI", "Run AI agents", "Start AI agent runs."),

        P(Permissions.SelfView,          "Self-service", "View own records", "Own worker profile, workstation, attendance and activity."),
        P(Permissions.NotificationsView, "Self-service", "View notifications", "Notifications addressed to you.", reserved: true),
    };

    private static readonly string[] SuperOnly = { SystemRoles.SuperAdmin };
    private static readonly string[] AdminTier = { SystemRoles.SuperAdmin, SystemRoles.FactoryAdmin };

    public static readonly IReadOnlyList<RoleDefinition> AllRoles = new[]
    {
        new RoleDefinition(SystemRoles.SuperAdmin, "Super Admin",
            "Global platform access: every factory, Factory Admin accounts, platform settings, global reports and the system audit log.",
            RoleScope.Platform, 100, SuperOnly,
            AllPermissions.Select(p => p.Code).ToArray()),

        new RoleDefinition(SystemRoles.FactoryAdmin, "Factory Admin",
            "Full administration inside the assigned factory: users and their roles, lines, departments and factory settings.",
            RoleScope.Factory, 80, SuperOnly,
            AllPermissions.Where(p => !p.PlatformOnly).Select(p => p.Code).ToArray()),

        new RoleDefinition(SystemRoles.FactoryManager, "Factory Manager",
            "Oversees factory operations: workforce and production performance, workstation assignments, attendance, overtime and reports, and operational approvals.",
            RoleScope.Factory, 60, AdminTier, new[]
            {
                Permissions.FactoriesView, Permissions.LinesView, Permissions.WorkersView, Permissions.WorkersDetailsView,
                Permissions.WorkersAssign,
                Permissions.AttendanceView, Permissions.OvertimeView,
                Permissions.MonitoringView,
                Permissions.ProductionView, Permissions.ProductionManage, Permissions.QcView,
                Permissions.MerchandisingView, Permissions.MerchandisingManage,
                Permissions.ApprovalsView, Permissions.ApprovalsDecide,
                Permissions.DevicesView, Permissions.ReportsView, Permissions.KnowledgeView, Permissions.AgentsRun,
                Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.ProductionSupervisor, "Production Supervisor",
            "Runs assigned production lines: their workers and workstations, production and activity, line alerts, and the buyer orders they produce (read-only).",
            RoleScope.Line, 50, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.WorkersView, Permissions.WorkersAssign,
                Permissions.AttendanceView,
                Permissions.MonitoringView, Permissions.ProductionView, Permissions.ProductionAlertsReview,
                Permissions.QcView, Permissions.QcManage, Permissions.ApprovalsView, Permissions.MerchandisingView,
                Permissions.KnowledgeView, Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.MonitoringOperator, "Monitoring Operator",
            "Watches the central monitoring dashboard, camera feeds and AI results; acknowledges and escalates alerts. Cannot change attendance, salary or worker records.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.WorkersView,
                Permissions.MonitoringView, Permissions.MonitoringAlertsReview, Permissions.MonitoringAlertsEscalate,
                Permissions.CamerasView, Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.HrOfficer, "HR / Attendance Officer",
            "Worker profiles and employment information, attendance and corrections, shifts, leave and overtime.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.WorkersView, Permissions.WorkersDetailsView, Permissions.WorkersManage,
                Permissions.AttendanceView, Permissions.AttendanceManage, Permissions.ShiftsManage,
                Permissions.LeaveManage, Permissions.OvertimeView, Permissions.OvertimeManage,
                Permissions.ReportsView, Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.DeviceAdmin, "IT / Device Administrator",
            "Cameras, RFID readers, machine sensors and gateways: health, connectivity, integrations and technical logs. Cannot change HR or salary information.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView,
                Permissions.DevicesView, Permissions.DevicesManage, Permissions.DevicesLogsView,
                Permissions.CamerasView, Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.Worker, "Worker",
            "Own profile, attendance and shift, assigned workstation, permitted activity and break records, and notifications.",
            RoleScope.Self, 10, AdminTier, new[]
            {
                Permissions.SelfView, Permissions.NotificationsView,
            }),
    };

    private static readonly Dictionary<string, RoleDefinition> RolesById =
        AllRoles.ToDictionary(r => r.Id, StringComparer.Ordinal);

    private static readonly Dictionary<string, PermissionDefinition> PermissionsByCode =
        AllPermissions.ToDictionary(p => p.Code, StringComparer.Ordinal);

    public static RoleDefinition? Role(string id) => RolesById.GetValueOrDefault(id);

    public static PermissionDefinition? Permission(string code) => PermissionsByCode.GetValueOrDefault(code);

    public static bool IsRole(string id) => RolesById.ContainsKey(id);

    /// <summary>Union of the permissions of <paramref name="roleIds"/>, sorted. Unknown ids are ignored.</summary>
    public static IReadOnlyList<string> PermissionsFor(IEnumerable<string> roleIds) =>
        roleIds.Select(Role).Where(r => r is not null)
            .SelectMany(r => r!.Permissions).Distinct(StringComparer.Ordinal)
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();

    /// <summary>The widest scope among <paramref name="roleIds"/>; <see cref="RoleScope.Self"/> when none.</summary>
    public static RoleScope ScopeFor(IEnumerable<string> roleIds)
    {
        var scopes = roleIds.Select(Role).Where(r => r is not null).Select(r => r!.Scope).ToList();
        return scopes.Count == 0 ? RoleScope.Self : scopes.Max();
    }

    /// <summary>The highest-ranked role (for display and the audit log's single actor-role column).</summary>
    public static string PrimaryRole(IEnumerable<string> roleIds) =>
        roleIds.Select(Role).Where(r => r is not null).OrderByDescending(r => r!.Rank).FirstOrDefault()?.Id ?? "None";

    /// <summary>
    /// True when a holder of <paramref name="actorRoles"/> (with
    /// <paramref name="actorPermissions"/>) may grant or remove
    /// <paramref name="roleId"/>: one of the actor's roles is in the role's
    /// <see cref="RoleDefinition.ManagedBy"/> list, AND every permission of the
    /// role is one the actor already holds (nobody can hand out more than they have).
    /// </summary>
    public static bool CanGrant(IReadOnlyCollection<string> actorRoles, IReadOnlyCollection<string> actorPermissions, string roleId)
    {
        var role = Role(roleId);
        if (role is null) return false;
        if (!role.ManagedBy.Any(actorRoles.Contains)) return false;
        var held = actorPermissions as ISet<string> ?? actorPermissions.ToHashSet(StringComparer.Ordinal);
        return role.Permissions.All(held.Contains);
    }

    /// <summary>Roles the actor may grant, in rank order.</summary>
    public static IReadOnlyList<string> GrantableBy(IReadOnlyCollection<string> actorRoles, IReadOnlyCollection<string> actorPermissions) =>
        AllRoles.Where(r => CanGrant(actorRoles, actorPermissions, r.Id))
            .OrderByDescending(r => r.Rank).Select(r => r.Id).ToArray();
}
