// Permission codes and role ids, mirroring the API's AccessCatalog
// (BunonBrain.Application/Access/AccessCatalog.cs). Used only to decide
// what the UI shows; the API decides what is allowed. The full role →
// permission matrix is fetched from GET /api/roles and /api/permissions.

export const P = {
  FactoriesView: "factories.view",
  FactoriesManage: "factories.manage",
  PlatformSettingsManage: "platform.settings.manage",
  KnowledgeManage: "knowledge.manage",
  UsersView: "users.view",
  UsersCreate: "users.create",
  UsersUpdate: "users.update",
  UsersDeactivate: "users.deactivate",
  UsersResetPassword: "users.reset_password",
  RolesView: "roles.view",
  RolesAssign: "roles.assign",
  AuditView: "audit.view",
  LinesView: "lines.view",
  LinesManage: "lines.manage",
  SettingsManage: "settings.manage",
  WorkersView: "workers.view",
  WorkersManage: "workers.manage",
  WorkersAssign: "workers.assign",
  AttendanceView: "attendance.view",
  MonitoringView: "monitoring.view",
  MonitoringAlertsReview: "monitoring.alerts.review",
  ProductionView: "production.view",
  ProductionManage: "production.manage",
  ProductionSetup: "production.setup",
  ProductionAlertsReview: "production.alerts.review",
  QcView: "qc.view",
  QcManage: "qc.manage",
  MerchandisingView: "merchandising.view",
  MerchandisingManage: "merchandising.manage",
  ApprovalsView: "approvals.view",
  ApprovalsDecide: "approvals.decide",
  ApprovalsDecideHigh: "approvals.decide_high",
  DevicesView: "devices.view",
  DevicesManage: "devices.manage",
  ReportsView: "reports.view",
  KnowledgeView: "knowledge.view",
  AgentsRun: "agents.run",
  SelfView: "self.view",
  NotificationsView: "notifications.view"
} as const;

export type Permission = (typeof P)[keyof typeof P];

export const ROLE_IDS = [
  "SuperAdmin",
  "FactoryAdmin",
  "FactoryManager",
  "ProductionSupervisor",
  "MonitoringOperator",
  "HrOfficer",
  "DeviceAdmin",
  "Worker"
] as const;

export type RoleId = (typeof ROLE_IDS)[number];

export const ROLE_LABEL: Record<RoleId, string> = {
  SuperAdmin: "Super Admin",
  FactoryAdmin: "Factory Admin",
  FactoryManager: "Factory Manager",
  ProductionSupervisor: "Production Supervisor",
  MonitoringOperator: "Monitoring Operator",
  HrOfficer: "HR / Attendance Officer",
  DeviceAdmin: "IT / Device Administrator",
  Worker: "Worker"
};

export function roleLabel(id: string): string {
  return (ROLE_LABEL as Record<string, string>)[id] ?? id;
}

export const SCOPE_LABEL: Record<string, string> = {
  Platform: "All factories",
  Factory: "Assigned factories",
  Line: "Assigned lines",
  Self: "Own records"
};

// ─── API contracts (GET/PUT /api/users, /api/roles, /api/permissions, /api/factories, /api/workers) ───

export type PagedResult<T> = { items: T[]; page: number; pageSize: number; total: number };

export type LineRef = { id: string; name: string; factoryId: string | null };
export type FactoryRef = { id: string; code: string; name: string };
export type WorkerRef = { id: string; employeeCode: string; fullName: string };

export type UserSummary = {
  id: string;
  email: string;
  fullName?: string | null;
  isActive: boolean;
  mustChangePassword: boolean;
  roles: string[];
  factories: FactoryRef[];
  lineCount: number;
  createdAt: string;
  lastLoginAt?: string | null;
  canManage: boolean;
};

export type UserCapabilities = {
  edit: boolean;
  changeStatus: boolean;
  assignRoles: boolean;
  changeScope: boolean;
  resetPassword: boolean;
  grantableRoles: string[];
};

export type UserDetail = {
  id: string;
  email: string;
  fullName?: string | null;
  phone?: string | null;
  isActive: boolean;
  mustChangePassword: boolean;
  roles: string[];
  permissions: string[];
  scope: string;
  factories: FactoryRef[];
  lines: LineRef[];
  worker?: WorkerRef | null;
  createdAt: string;
  updatedAt?: string | null;
  lastLoginAt?: string | null;
  deactivatedAt?: string | null;
  can: UserCapabilities;
};

export type RoleDto = {
  id: string;
  name: string;
  description: string;
  scope: string;
  rank: number;
  isSystem: boolean;
  managedBy: string[];
  permissions: string[];
  userCount: number;
  canGrant: boolean;
};

export type PermissionDto = {
  code: string;
  module: string;
  name: string;
  description: string;
  platformOnly: boolean;
  reserved: boolean;
  roles: string[];
};

export type PermissionModule = { module: string; permissions: PermissionDto[] };

export type FactoryDto = {
  id: string;
  code: string;
  name: string;
  location?: string | null;
  isActive: boolean;
  userCount: number;
  lineCount: number;
  createdAt: string;
  updatedAt: string;
};

export type FactoryLine = { id: string; name: string; workstations: number; isActive: boolean; factoryId: string | null };

export type WorkerDto = {
  id: string;
  factoryId: string;
  employeeCode: string;
  fullName: string;
  designation?: string | null;
  lineId?: string | null;
  workstation?: string | null;
  isActive: boolean;
  hasAccount: boolean;
  userId?: string | null;
  phone?: string | null;
  joinedOn?: string | null;
  grade?: string | null;
  createdAt: string;
  updatedAt: string;
};

export type AccountProfile = {
  id: string;
  email: string;
  fullName?: string | null;
  phone?: string | null;
  roles: string[];
  permissions: string[];
  scope: string;
  factories: FactoryRef[];
  lines: LineRef[];
  worker?: WorkerRef | null;
  mustChangePassword: boolean;
  createdAt: string;
  lastLoginAt?: string | null;
};
