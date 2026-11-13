using BunonBrain.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BunonBrain.Infrastructure.Persistence.Configurations;

// Shifts, attendance, breaks and overtime. Nothing cascades a delete onto
// attendance history. Rules the database itself guarantees:
//   * one open attendance session and one active break per worker (partial unique indexes);
//   * an idempotency key is processed once per factory (unique index);
//   * a worker's shift assignments never overlap in dates (exclusion constraint, added in the migration);
//   * raw attendance events are append-only (trigger, added in the migration).

public class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> b)
    {
        b.ToTable("shifts", t =>
        {
            t.HasCheckConstraint("CK_shifts_times", "\"StartLocal\" <> \"EndLocal\"");
            t.HasCheckConstraint("CK_shifts_days", "\"WorkingDays\" BETWEEN 1 AND 127");
            t.HasCheckConstraint("CK_shifts_grace", "\"LateGraceMinutes\" BETWEEN 0 AND 120 AND \"EarlyLeaveGraceMinutes\" BETWEEN 0 AND 120");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.Property(x => x.Description).HasMaxLength(300);
        b.Property(x => x.ProductionShiftCode).HasMaxLength(16);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasIndex(x => new { x.FactoryId, x.Name }).IsUnique();
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ProductionShift>().WithMany().HasForeignKey(x => x.ProductionShiftCode).OnDelete(DeleteBehavior.SetNull);
        b.HasMany(x => x.Breaks).WithOne().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class ShiftBreakConfiguration : IEntityTypeConfiguration<ShiftBreak>
{
    public void Configure(EntityTypeBuilder<ShiftBreak> b)
    {
        b.ToTable("shift_breaks", t =>
        {
            t.HasCheckConstraint("CK_shift_breaks_duration", "\"DurationMinutes\" BETWEEN 1 AND 240");
            t.HasCheckConstraint("CK_shift_breaks_type", "\"BreakType\" IN ('Lunch','Tea','Prayer','Personal','Other')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.BreakType).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(60).IsRequired();
        b.HasIndex(x => x.ShiftId);
    }
}

public class WorkerShiftAssignmentConfiguration : IEntityTypeConfiguration<WorkerShiftAssignment>
{
    public const string ExclusionConstraintName = "EX_worker_shift_assignments_no_overlap";

    public void Configure(EntityTypeBuilder<WorkerShiftAssignment> b)
    {
        b.ToTable("worker_shift_assignments", t =>
        {
            t.HasCheckConstraint("CK_worker_shift_assignments_dates", "\"EffectiveTo\" IS NULL OR \"EffectiveTo\" >= \"EffectiveFrom\"");
            t.HasCheckConstraint("CK_worker_shift_assignments_status", "\"Status\" IN ('Active','Ended','Superseded')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.Reason).HasMaxLength(500);
        b.Property(x => x.EndReason).HasMaxLength(500);
        b.Property(x => x.AssignedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.EndedByEmail).HasMaxLength(256);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.WorkerId, x.EffectiveFrom });
        b.HasIndex(x => new { x.ShiftId, x.Status });
        b.HasIndex(x => new { x.FactoryId, x.Status });
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkerShiftAssignment>().WithMany().HasForeignKey(x => x.PreviousAssignmentId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AttendanceReaderConfiguration : IEntityTypeConfiguration<AttendanceReader>
{
    public void Configure(EntityTypeBuilder<AttendanceReader> b)
    {
        b.ToTable("attendance_readers", t =>
        {
            t.HasCheckConstraint("CK_attendance_readers_direction", "\"Direction\" IN ('Entry','Exit','Both')");
            t.HasCheckConstraint("CK_attendance_readers_kind", "\"Kind\" IN ('Simulated','Rfid','Nfc','Gateway')");
            t.HasCheckConstraint("CK_attendance_readers_simulated", "(\"Kind\" = 'Simulated') = \"IsSimulated\"");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Code).HasMaxLength(16).IsRequired();
        b.Property(x => x.Name).HasMaxLength(80).IsRequired();
        b.Property(x => x.Direction).HasMaxLength(8).IsRequired();
        b.Property(x => x.Kind).HasMaxLength(16).IsRequired();
        b.Property(x => x.Location).HasMaxLength(120);
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Code }).IsUnique();
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AttendanceEventConfiguration : IEntityTypeConfiguration<AttendanceEvent>
{
    public void Configure(EntityTypeBuilder<AttendanceEvent> b)
    {
        b.ToTable("attendance_events", t =>
        {
            t.HasCheckConstraint("CK_attendance_events_type", "\"EventType\" IN ('CheckIn','CheckOut','BreakStart','BreakEnd')");
            t.HasCheckConstraint("CK_attendance_events_outcome", "\"Outcome\" IN ('Accepted','Duplicate','Rejected')");
            t.HasCheckConstraint("CK_attendance_events_source", "\"Source\" IN ('Simulator','Device','Manual','Correction','System')");
            // A simulator event is always marked simulated, and only simulator events are.
            t.HasCheckConstraint("CK_attendance_events_simulated", "(\"Source\" = 'Simulator') = \"IsSimulated\"");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.EventType).HasMaxLength(16).IsRequired();
        b.Property(x => x.Source).HasMaxLength(16).IsRequired();
        b.Property(x => x.CardUid).HasMaxLength(96);
        b.Property(x => x.IdempotencyKey).HasMaxLength(160).IsRequired();
        b.Property(x => x.Outcome).HasMaxLength(16).IsRequired();
        b.Property(x => x.OutcomeCode).HasMaxLength(48);
        b.Property(x => x.OutcomeMessage).HasMaxLength(300);
        b.Property(x => x.RecordedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.Property(x => x.ReceivedAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.IdempotencyKey }).IsUnique();
        b.HasIndex(x => new { x.WorkerId, x.OccurredAt });
        b.HasIndex(x => new { x.FactoryId, x.OccurredAt });
        b.HasIndex(x => x.SessionId);
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AttendanceReader>().WithMany().HasForeignKey(x => x.ReaderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AttendanceSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<BreakSession>().WithMany().HasForeignKey(x => x.BreakSessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AttendanceSessionConfiguration : IEntityTypeConfiguration<AttendanceSession>
{
    public const string OneOpenIndex = "IX_attendance_sessions_one_open";

    public void Configure(EntityTypeBuilder<AttendanceSession> b)
    {
        b.ToTable("attendance_sessions", t =>
        {
            t.HasCheckConstraint("CK_attendance_sessions_status", "\"Status\" IN ('Open','Completed','MissingCheckOut','MissingCheckIn')");
            t.HasCheckConstraint("CK_attendance_sessions_order", "\"CheckInAt\" IS NULL OR \"CheckOutAt\" IS NULL OR \"CheckOutAt\" > \"CheckInAt\"");
            t.HasCheckConstraint("CK_attendance_sessions_open", "\"Status\" <> 'Open' OR (\"CheckInAt\" IS NOT NULL AND \"CheckOutAt\" IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.CheckInSource).HasMaxLength(16);
        b.Property(x => x.CheckOutSource).HasMaxLength(16);
        b.Property(x => x.ReviewReason).HasMaxLength(300);
        foreach (var p in new[] { nameof(AttendanceSession.ScheduledStart), nameof(AttendanceSession.ScheduledEnd), nameof(AttendanceSession.CheckInAt),
                     nameof(AttendanceSession.CheckOutAt), nameof(AttendanceSession.CreatedAt), nameof(AttendanceSession.UpdatedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.HasIndex(x => x.WorkerId).IsUnique().HasDatabaseName(OneOpenIndex).HasFilter("\"Status\" = 'Open'");
        b.HasIndex(x => new { x.FactoryId, x.WorkDate });
        b.HasIndex(x => new { x.WorkerId, x.WorkDate });
        b.HasIndex(x => new { x.LineId, x.WorkDate });
        b.HasIndex(x => new { x.FactoryId, x.NeedsReview });
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<WorkerShiftAssignment>().WithMany().HasForeignKey(x => x.ShiftAssignmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shift>().WithMany().HasForeignKey(x => x.ShiftId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class BreakSessionConfiguration : IEntityTypeConfiguration<BreakSession>
{
    public void Configure(EntityTypeBuilder<BreakSession> b)
    {
        b.ToTable("break_sessions", t =>
        {
            t.HasCheckConstraint("CK_break_sessions_status", "\"Status\" IN ('Active','Completed','AutoClosed')");
            t.HasCheckConstraint("CK_break_sessions_order", "\"EndAt\" IS NULL OR \"EndAt\" >= \"StartAt\"");
            t.HasCheckConstraint("CK_break_sessions_type", "\"BreakType\" IN ('Lunch','Tea','Prayer','Personal','Other')");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.BreakType).HasMaxLength(16).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.StartSource).HasMaxLength(16).IsRequired();
        b.Property(x => x.EndSource).HasMaxLength(16);
        b.Property(x => x.StartAt).HasColumnType("timestamptz");
        b.Property(x => x.EndAt).HasColumnType("timestamptz");
        b.Property(x => x.CreatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.HasIndex(x => x.WorkerId).IsUnique().HasDatabaseName("IX_break_sessions_one_active").HasFilter("\"Status\" = 'Active'");
        b.HasIndex(x => x.AttendanceSessionId);
        b.HasIndex(x => new { x.FactoryId, x.StartAt });
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AttendanceSession>().WithMany().HasForeignKey(x => x.AttendanceSessionId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<ShiftBreak>().WithMany().HasForeignKey(x => x.ShiftBreakId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class OvertimeRequestConfiguration : IEntityTypeConfiguration<OvertimeRequest>
{
    public void Configure(EntityTypeBuilder<OvertimeRequest> b)
    {
        b.ToTable("overtime_requests", t =>
        {
            t.HasCheckConstraint("CK_overtime_requests_status",
                "\"Status\" IN ('Requested','PendingApproval','Approved','Rejected','InProgress','Completed','Cancelled')");
            t.HasCheckConstraint("CK_overtime_requests_period", "\"PlannedEnd\" > \"PlannedStart\"");
            t.HasCheckConstraint("CK_overtime_requests_minutes", "\"RequestedMinutes\" > 0 AND (\"ApprovedMinutes\" IS NULL OR \"ApprovedMinutes\" <= \"RequestedMinutes\")");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.LineId).HasMaxLength(32);
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.PayableNote).HasMaxLength(500);
        b.Property(x => x.PayableConfirmedByEmail).HasMaxLength(256);
        b.Property(x => x.RequestedByEmail).HasMaxLength(256).IsRequired();
        foreach (var p in new[] { nameof(OvertimeRequest.PlannedStart), nameof(OvertimeRequest.PlannedEnd), nameof(OvertimeRequest.ApprovedStart),
                     nameof(OvertimeRequest.ApprovedEnd), nameof(OvertimeRequest.PayableConfirmedAt), nameof(OvertimeRequest.CreatedAt), nameof(OvertimeRequest.UpdatedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Status });
        b.HasIndex(x => new { x.WorkerId, x.WorkDate });
        b.HasIndex(x => new { x.LineId, x.Status });
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class OvertimeApprovalConfiguration : IEntityTypeConfiguration<OvertimeApproval>
{
    public void Configure(EntityTypeBuilder<OvertimeApproval> b)
    {
        b.ToTable("overtime_approvals");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).UseIdentityAlwaysColumn();
        b.Property(x => x.Action).HasMaxLength(24).IsRequired();
        b.Property(x => x.FromStatus).HasMaxLength(16);
        b.Property(x => x.ToStatus).HasMaxLength(16).IsRequired();
        b.Property(x => x.Note).HasMaxLength(500);
        b.Property(x => x.ActorEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.OccurredAt).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.RequestId, x.Id });
        b.HasOne<OvertimeRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AttendanceCorrectionConfiguration : IEntityTypeConfiguration<AttendanceCorrection>
{
    public void Configure(EntityTypeBuilder<AttendanceCorrection> b)
    {
        b.ToTable("attendance_corrections", t =>
        {
            t.HasCheckConstraint("CK_attendance_corrections_status", "\"Status\" IN ('Pending','Approved','Rejected')");
            t.HasCheckConstraint("CK_attendance_corrections_values", "\"ProposedCheckIn\" IS NOT NULL OR \"ProposedCheckOut\" IS NOT NULL");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Reason).HasMaxLength(500).IsRequired();
        b.Property(x => x.Status).HasMaxLength(16).IsRequired();
        b.Property(x => x.RequestedByEmail).HasMaxLength(256).IsRequired();
        b.Property(x => x.DecidedByEmail).HasMaxLength(256);
        b.Property(x => x.DecisionNote).HasMaxLength(500);
        foreach (var p in new[] { nameof(AttendanceCorrection.ProposedCheckIn), nameof(AttendanceCorrection.ProposedCheckOut), nameof(AttendanceCorrection.OriginalCheckIn),
                     nameof(AttendanceCorrection.OriginalCheckOut), nameof(AttendanceCorrection.RequestedAt), nameof(AttendanceCorrection.DecidedAt) })
            b.Property(p).HasColumnType("timestamptz");
        b.HasIndex(x => new { x.FactoryId, x.Status });
        b.HasIndex(x => x.SessionId);
        b.HasIndex(x => x.WorkerId);
        b.HasOne<WorkerProfile>().WithMany().HasForeignKey(x => x.WorkerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AttendanceSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class AttendancePolicyConfiguration : IEntityTypeConfiguration<AttendancePolicy>
{
    public void Configure(EntityTypeBuilder<AttendancePolicy> b)
    {
        b.ToTable("attendance_policies");
        b.HasKey(x => x.FactoryId);
        b.Property(x => x.TimeZoneId).HasMaxLength(64).IsRequired();
        b.Property(x => x.UpdatedAt).HasColumnType("timestamptz");
        b.Property(x => x.UpdatedByEmail).HasMaxLength(256);
        b.HasOne<Factory>().WithMany().HasForeignKey(x => x.FactoryId).OnDelete(DeleteBehavior.Restrict);
    }
}
