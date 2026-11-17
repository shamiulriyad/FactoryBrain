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
    public const string AttendanceView    = "attendance.view";
    public const string AttendanceRecord  = "attendance.record";
    public const string AttendanceManage  = "attendance.manage";
    public const string AttendanceApprove = "attendance.approve";
    public const string ShiftsManage      = "shifts.manage";
    public const string ShiftsAssign      = "shifts.assign";
    public const string LeaveManage      = "leave.manage";
    public const string OvertimeView     = "overtime.view";
    public const string OvertimeManage   = "overtime.manage";
    public const string OvertimeApprove  = "overtime.approve";

    // Monitoring
    public const string MonitoringView           = "monitoring.view";
    public const string MonitoringAlertsReview   = "monitoring.alerts.review";
    public const string MonitoringAlertsEscalate = "monitoring.alerts.escalate";
    public const string MonitoringAlertsDismiss  = "monitoring.alerts.dismiss";
    public const string MonitoringEventsReview   = "monitoring.events.review";
    public const string MonitoringEventsIngest   = "monitoring.events.ingest";
    public const string MonitoringSimulate       = "monitoring.simulate";
    public const string CamerasView              = "cameras.view";
    public const string CamerasManage            = "cameras.manage";

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

    // Production planning & target management
    public const string PlanningView             = "planning.view";
    public const string PlanningOrdersManage     = "planning.orders.manage";
    public const string PlanningStylesManage     = "planning.styles.manage";
    public const string PlanningAllocationsManage = "planning.allocations.manage";
    public const string PlanningTargetsManage    = "planning.targets.manage";
    public const string PlanningPlansManage      = "planning.plans.manage";
    public const string PlanningPlansApprove     = "planning.plans.approve";
    public const string PlanningPlansPublish     = "planning.plans.publish";
    public const string PlanningExceptionsReview = "planning.exceptions.review";
    public const string PlanningSetup            = "planning.setup";
    public const string PlanningReportsExport    = "planning.reports.export";

    // Devices
    public const string DevicesView     = "devices.view";
    public const string DevicesManage   = "devices.manage";
    public const string DevicesLogsView = "devices.logs.view";
    public const string DevicesSimulate = "devices.simulate";

    // Factory documents (automation engine). An action permission works only
    // inside the document categories (departments) the caller also holds.
    public const string DocumentsView        = "documents.view";
    public const string DocumentsCreate      = "documents.create";
    public const string DocumentsReview      = "documents.review";
    public const string DocumentsApprove     = "documents.approve";
    public const string DocumentsApproveHigh = "documents.approve_high";
    public const string DocumentsControl     = "documents.control";
    public const string DocumentsMonitor     = "documents.monitor";
    public const string DocCatProduction    = "documents.cat.production";
    public const string DocCatHr            = "documents.cat.hr";
    public const string DocCatStore         = "documents.cat.store";
    public const string DocCatQuality       = "documents.cat.quality";
    public const string DocCatMerchandising = "documents.cat.merchandising";
    public const string DocCatCompliance    = "documents.cat.compliance";
    public const string DocCatCommercial    = "documents.cat.commercial";
    public const string DocCatDevices       = "documents.cat.devices";

    // Store / inventory
    public const string InventoryView   = "inventory.view";
    public const string InventoryManage = "inventory.manage";

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

    // Documentation roles (factory scope). A person may hold several roles.
    public const string DocumentController = "DocumentController";
    public const string Merchandiser       = "Merchandiser";
    public const string StoreOfficer       = "StoreOfficer";
    public const string QcOfficer          = "QcOfficer";
    public const string ComplianceOfficer  = "ComplianceOfficer";
    public const string CommercialOfficer  = "CommercialOfficer";
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

        P(Permissions.AttendanceView,    "Attendance", "View attendance", "Attendance sessions, breaks, shifts, events and reports in your scope (supervisors: their lines)."),
        P(Permissions.AttendanceRecord,  "Attendance", "Record punches and breaks", "Record check-ins, check-outs and breaks for workers in your scope."),
        P(Permissions.AttendanceManage,  "Attendance", "Administer attendance", "Request corrections, use the RFID simulator, set the attendance policy, confirm payable overtime and run the missing-punch review."),
        P(Permissions.AttendanceApprove, "Attendance", "Approve corrections", "Approve or reject attendance corrections requested by someone else."),
        P(Permissions.ShiftsManage,      "Attendance", "Manage shifts", "Create, edit, activate and deactivate shifts and their breaks."),
        P(Permissions.ShiftsAssign,      "Attendance", "Assign shifts", "Assign workers to shifts, one by one or by line or department, in your scope."),
        P(Permissions.LeaveManage,       "Attendance", "Manage leave", "Record and approve leave.", reserved: true),
        P(Permissions.OvertimeView,      "Attendance", "View overtime", "See overtime requests, approvals and actual overtime in your scope."),
        P(Permissions.OvertimeManage,    "Attendance", "Request overtime", "Submit and cancel overtime requests for workers in your scope."),
        P(Permissions.OvertimeApprove,   "Attendance", "Approve overtime", "Approve or reject overtime requested by someone else."),

        P(Permissions.MonitoringView,           "Monitoring", "View monitoring", "The central monitoring room, camera monitoring, the event timeline and alerts in your scope (supervisors: their lines), with live updates. Floor alerts, line board and live floor status."),
        P(Permissions.MonitoringAlertsReview,   "Monitoring", "Review monitoring alerts", "Acknowledge and resolve monitoring alerts in your scope, and floor alerts."),
        P(Permissions.MonitoringAlertsEscalate, "Monitoring", "Escalate alerts", "Escalate monitoring alerts (uncertain events, offline equipment) to a supervisor, manager or administrator."),
        P(Permissions.MonitoringAlertsDismiss,  "Monitoring", "Dismiss alerts", "Close a monitoring alert without action, with a written reason."),
        P(Permissions.MonitoringEventsReview,   "Monitoring", "Review monitoring events", "Record a review decision (confirmed, false positive, still uncertain) beside a monitoring event; the event itself never changes."),
        P(Permissions.MonitoringEventsIngest,   "Monitoring", "Send monitoring events", "Send REAL monitoring events through the ingestion API (integration accounts, later the AI service). Worker identities only with a verified RFID card."),
        P(Permissions.MonitoringSimulate,       "Monitoring", "Use the monitoring simulator", "Start and stop monitoring simulations and trigger demo events. Everything it produces is labelled SIMULATED."),
        P(Permissions.CamerasView,              "Monitoring", "View cameras", "The cameras in your scope (supervisors: cameras covering their lines): placement, coverage, zones, video source type, reference frame and recorded video. Never camera passwords."),
        P(Permissions.CamerasManage,            "Monitoring", "Manage cameras", "Register, edit, (de)activate and delete cameras, map their coverage, configure video sources (credentials stored encrypted), reference frames and zones."),

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

        P(Permissions.PlanningView,              "Production planning", "View production planning", "Orders, allocations, targets, capacity, exceptions and plan-versus-actual performance in your scope. Daily plans are visible once published."),
        P(Permissions.PlanningOrdersManage,      "Production planning", "Manage production orders", "Create and edit production orders and change their status."),
        P(Permissions.PlanningStylesManage,      "Production planning", "Manage garment styles", "Garment styles, size and colour ranges, operation sequences, required machine types and quality checkpoints."),
        P(Permissions.PlanningAllocationsManage, "Production planning", "Allocate orders to lines", "Split orders across production lines (supervisors: only their own lines)."),
        P(Permissions.PlanningTargetsManage,     "Production planning", "Manage production targets", "Create and revise daily, shift and hourly targets (supervisors: only their own lines)."),
        P(Permissions.PlanningPlansManage,       "Production planning", "Prepare daily plans", "Create, edit, submit and revise daily production plans."),
        P(Permissions.PlanningPlansApprove,      "Production planning", "Approve plans and targets", "Approve or return daily plans and factory-, department- or floor-level targets."),
        P(Permissions.PlanningPlansPublish,      "Production planning", "Publish daily plans", "Publish approved plans so supervisors and monitoring operators see them."),
        P(Permissions.PlanningExceptionsReview,  "Production planning", "Handle production exceptions", "Run the exception check, acknowledge and resolve delay and capacity exceptions (supervisors: only their own lines)."),
        P(Permissions.PlanningSetup,             "Production planning", "Configure planning inputs", "Which counters measure finished garments for a line, and each line's efficiency and operator assumptions."),
        P(Permissions.PlanningReportsExport,     "Production planning", "Export planning reports", "Download plan-versus-actual reports as CSV or PDF."),

        P(Permissions.DevicesView,     "Devices", "View devices", "Sensors, RFID readers, gateways, cameras' health devices and the device health overview."),
        P(Permissions.DevicesManage,   "Devices", "Manage devices", "Register and configure devices, change their status, and send device heartbeats, events and readings."),
        P(Permissions.DevicesLogsView, "Devices", "View device logs", "Device heartbeats and event history (connections, errors, status changes)."),
        P(Permissions.DevicesSimulate, "Devices", "Use the device simulator", "Make simulated devices come online, go offline, send heartbeats and report errors. Only simulated devices; always marked SIMULATED."),

        P(Permissions.DocumentsView,        "Documents", "View documents", "Factory documents of your document categories: list, search, open, download, versions and history."),
        P(Permissions.DocumentsCreate,      "Documents", "Prepare documents", "Create, generate from operational data, upload, edit drafts, resubmit rejected documents and submit them (your categories)."),
        P(Permissions.DocumentsReview,      "Documents", "Review documents", "Take submitted documents under review, verify them and return them with a reason (your categories)."),
        P(Permissions.DocumentsApprove,     "Documents", "Approve documents", "Approve or reject normal-risk documents of your categories. Never your own."),
        P(Permissions.DocumentsApproveHigh, "Documents", "Approve high-risk documents", "Approve or reject high-risk (financial, compliance, shipment) documents of your categories."),
        P(Permissions.DocumentsControl,     "Documents", "Control documents", "Central document control: every category, archiving, verification and the approval / schedule settings of each document type."),
        P(Permissions.DocumentsMonitor,     "Documents", "Monitor documents", "The document status board: missing, pending and overdue documents of every category (titles and status only, not content)."),
        P(Permissions.DocCatProduction,     "Documents", "Production documents", "Daily, line-wise, cutting, sewing, finishing, WIP and downtime reports."),
        P(Permissions.DocCatHr,             "Documents", "HR documents", "Attendance, late / absent, overtime, leave, employee information and payroll input reports."),
        P(Permissions.DocCatStore,          "Documents", "Store documents", "GRN, material requisition, material issue, stock register and stock balance."),
        P(Permissions.DocCatQuality,        "Documents", "Quality documents", "Inline, end-line and final inspections, defect, rework, AQL and CAPA reports."),
        P(Permissions.DocCatMerchandising,  "Documents", "Merchandising documents", "Purchase orders, order sheets, BOM, costing sheets and buyer instructions."),
        P(Permissions.DocCatCompliance,     "Documents", "Compliance documents", "Compliance audits, safety inspections and training records."),
        P(Permissions.DocCatCommercial,     "Documents", "Commercial documents", "Commercial invoices, packing lists and shipment documents."),
        P(Permissions.DocCatDevices,        "Documents", "Device documents", "Device log reports and maintenance records."),

        P(Permissions.InventoryView,   "Store", "View inventory", "Store items, stock balances and the stock ledger of your factories."),
        P(Permissions.InventoryManage, "Store", "Manage inventory items", "Create and edit store items (stock moves only through approved GRN and material issue documents)."),

        P(Permissions.ReportsView,   "Reports & AI", "View reports", "Morning brief and operational reports."),
        P(Permissions.KnowledgeView, "Reports & AI", "Use the assistant", "Ask questions, browse the knowledge base and see AI agents."),
        P(Permissions.AgentsRun,     "Reports & AI", "Run AI agents", "Start AI agent runs."),

        P(Permissions.SelfView,          "Self-service", "View own records", "Own worker profile, workstation, attendance and activity."),
        P(Permissions.NotificationsView, "Self-service", "View notifications", "Notifications addressed to you."),
    };

    /// <summary>Every document-category permission (a Document Controller and Factory Manager hold all of them).</summary>
    public static readonly string[] DocumentCategoryPermissions =
    {
        Permissions.DocCatProduction, Permissions.DocCatHr, Permissions.DocCatStore, Permissions.DocCatQuality,
        Permissions.DocCatMerchandising, Permissions.DocCatCompliance, Permissions.DocCatCommercial, Permissions.DocCatDevices,
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
                Permissions.AttendanceView, Permissions.AttendanceApprove,
                Permissions.OvertimeView, Permissions.OvertimeManage, Permissions.OvertimeApprove,
                Permissions.MonitoringView, Permissions.MonitoringAlertsReview, Permissions.MonitoringAlertsEscalate,
                Permissions.MonitoringAlertsDismiss, Permissions.MonitoringEventsReview,
                Permissions.ProductionView, Permissions.ProductionManage, Permissions.QcView,
                Permissions.MerchandisingView, Permissions.MerchandisingManage,
                Permissions.ApprovalsView, Permissions.ApprovalsDecide,
                Permissions.PlanningView, Permissions.PlanningOrdersManage, Permissions.PlanningStylesManage, Permissions.PlanningAllocationsManage,
                Permissions.PlanningTargetsManage, Permissions.PlanningPlansManage, Permissions.PlanningPlansApprove, Permissions.PlanningPlansPublish,
                Permissions.PlanningExceptionsReview, Permissions.PlanningSetup, Permissions.PlanningReportsExport,
                Permissions.DevicesView, Permissions.CamerasView, Permissions.ReportsView, Permissions.KnowledgeView, Permissions.AgentsRun,
                Permissions.NotificationsView,
                // Reviews and approves factory reports and important documents of every department.
                Permissions.DocumentsView, Permissions.DocumentsReview, Permissions.DocumentsApprove, Permissions.DocumentsApproveHigh, Permissions.DocumentsMonitor,
                Permissions.DocCatProduction, Permissions.DocCatHr, Permissions.DocCatStore, Permissions.DocCatQuality,
                Permissions.DocCatMerchandising, Permissions.DocCatCompliance, Permissions.DocCatCommercial, Permissions.DocCatDevices,
                Permissions.InventoryView,
            }),

        new RoleDefinition(SystemRoles.ProductionSupervisor, "Production Supervisor",
            "Runs assigned production lines: their workers and workstations, production and activity, line alerts, and the buyer orders they produce (read-only).",
            RoleScope.Line, 50, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.WorkersView, Permissions.WorkersAssign,
                Permissions.AttendanceView, Permissions.AttendanceRecord, Permissions.ShiftsAssign,
                Permissions.OvertimeView, Permissions.OvertimeManage, Permissions.OvertimeApprove,
                Permissions.MonitoringView, Permissions.MonitoringAlertsReview, Permissions.MonitoringAlertsEscalate, Permissions.MonitoringEventsReview,
                Permissions.ProductionView, Permissions.ProductionAlertsReview,
                Permissions.QcView, Permissions.QcManage, Permissions.ApprovalsView, Permissions.MerchandisingView,
                Permissions.PlanningView, Permissions.PlanningTargetsManage, Permissions.PlanningExceptionsReview, Permissions.PlanningReportsExport,
                Permissions.CamerasView, Permissions.KnowledgeView, Permissions.NotificationsView,
                // Daily production, line output, WIP and downtime reports of their lines.
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocCatProduction,
            }),

        new RoleDefinition(SystemRoles.MonitoringOperator, "Monitoring Operator",
            "Watches the central monitoring room, camera monitoring, the live event timeline and device health; reviews events, acknowledges and escalates alerts, and runs the monitoring simulator. Cannot change attendance, salary or worker records.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.WorkersView, Permissions.AttendanceView,
                Permissions.MonitoringView, Permissions.MonitoringAlertsReview, Permissions.MonitoringAlertsEscalate,
                Permissions.MonitoringEventsReview, Permissions.MonitoringSimulate,
                Permissions.PlanningView,
                Permissions.CamerasView, Permissions.DevicesView, Permissions.NotificationsView,
                // Watches for missing reports, pending documents and operational exceptions (status only).
                Permissions.DocumentsMonitor,
            }),

        new RoleDefinition(SystemRoles.HrOfficer, "HR / Attendance Officer",
            "Worker profiles and employment information, attendance and corrections, shifts, leave and overtime.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.WorkersView, Permissions.WorkersDetailsView, Permissions.WorkersManage,
                Permissions.AttendanceView, Permissions.AttendanceRecord, Permissions.AttendanceManage, Permissions.AttendanceApprove,
                Permissions.ShiftsManage, Permissions.ShiftsAssign,
                Permissions.LeaveManage, Permissions.OvertimeView, Permissions.OvertimeManage, Permissions.OvertimeApprove,
                Permissions.ReportsView, Permissions.NotificationsView,
                // Attendance, late / absent, overtime, leave, employee and payroll input documents.
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocumentsReview, Permissions.DocCatHr,
            }),

        new RoleDefinition(SystemRoles.DeviceAdmin, "IT / Device Administrator",
            "Cameras (registration, coverage, video sources, zones), RFID readers, machine sensors and gateways: health, connectivity, the device simulator, technical logs, camera / device / model status events and the monitoring ingestion API. Cannot change HR or salary information.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView,
                Permissions.DevicesView, Permissions.DevicesManage, Permissions.DevicesLogsView, Permissions.DevicesSimulate,
                Permissions.CamerasView, Permissions.CamerasManage, Permissions.MonitoringEventsIngest, Permissions.NotificationsView,
                // Device logs, maintenance records and system-generated logs.
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocCatDevices,
            }),

        new RoleDefinition(SystemRoles.Worker, "Worker",
            "Own profile, attendance and shift, assigned workstation, permitted activity and break records, and notifications.",
            RoleScope.Self, 10, AdminTier, new[]
            {
                Permissions.SelfView, Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.DocumentController, "Document Controller",
            "Central document control for every department: tracking, verification, versioning, archiving, missing-document follow-up and the approval settings of each document type. Approves nothing by default.",
            RoleScope.Factory, 45, AdminTier, new[]
            {
                Permissions.FactoriesView, Permissions.LinesView,
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocumentsReview, Permissions.DocumentsControl, Permissions.DocumentsMonitor,
                Permissions.DocCatProduction, Permissions.DocCatHr, Permissions.DocCatStore, Permissions.DocCatQuality,
                Permissions.DocCatMerchandising, Permissions.DocCatCompliance, Permissions.DocCatCommercial, Permissions.DocCatDevices,
                Permissions.InventoryView, Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.Merchandiser, "Merchandiser",
            "Buyer orders and their documents: purchase orders, order sheets, bills of materials, costing sheets and buyer instructions.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.MerchandisingView, Permissions.MerchandisingManage, Permissions.PlanningView,
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocCatMerchandising,
                Permissions.InventoryView, Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.StoreOfficer, "Store / Inventory Officer",
            "Store items and stock: goods received notes, material requisitions, material issue notes, the stock register and stock balance.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.InventoryView, Permissions.InventoryManage,
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocCatStore,
                Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.QcOfficer, "QC / QA Officer",
            "Quality inspections and their documents: inline, end-line and final inspections, defect, rework, AQL and CAPA reports.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.QcView, Permissions.QcManage, Permissions.ProductionView,
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocumentsReview, Permissions.DocCatQuality,
                Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.ComplianceOfficer, "Compliance Officer",
            "Compliance audits, safety inspections and training records.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.LinesView, Permissions.WorkersView,
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocumentsReview, Permissions.DocCatCompliance,
                Permissions.NotificationsView,
            }),

        new RoleDefinition(SystemRoles.CommercialOfficer, "Commercial / Shipping Officer",
            "Commercial invoices, packing lists and shipment documents, checked against the buyer's order.",
            RoleScope.Factory, 40, AdminTier, new[]
            {
                Permissions.MerchandisingView,
                Permissions.DocumentsView, Permissions.DocumentsCreate, Permissions.DocCatCommercial,
                Permissions.NotificationsView,
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
