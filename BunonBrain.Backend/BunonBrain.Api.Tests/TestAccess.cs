using System.Net.Http.Headers;
using BunonBrain.Application.Abstractions.Interfaces;
using BunonBrain.Application.Access;
using BunonBrain.Domain.Entities;
using BunonBrain.Infrastructure.Access;
using BunonBrain.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Access tiers the module tests (QC, production, merchandising, RAG, …)
/// were written against, before the eight-role model. They name a fixed
/// permission set, so those tests keep checking "a reader can't write" and
/// "only an administrator configures" on top of permission enforcement.
/// The role matrix itself is covered by <see cref="RoleMatrixTests"/> and
/// <see cref="DbAccessControlTests"/>, which use the real roles.
/// </summary>
public static class Roles
{
    /// <summary>Reads every operational module; writes nothing.</summary>
    public const string Viewer = "Viewer";
    /// <summary>Viewer plus operational writes (production, QC, merchandising, approvals, agents, alerts, devices).</summary>
    public const string Operator = "Operator";
    /// <summary>Super Admin: every permission.</summary>
    public const string Admin = "Admin";
}

public static class TestAccess
{
    public static readonly string[] ViewerPermissions =
    {
        Permissions.ProductionView, Permissions.QcView, Permissions.MerchandisingView, Permissions.MonitoringView,
        Permissions.DevicesView, Permissions.ApprovalsView, Permissions.KnowledgeView, Permissions.ReportsView,
        Permissions.LinesView,
    };

    public static readonly string[] OperatorPermissions = ViewerPermissions.Concat(new[]
    {
        Permissions.ProductionManage, Permissions.QcManage, Permissions.MerchandisingManage, Permissions.ApprovalsDecide,
        Permissions.AgentsRun, Permissions.MonitoringAlertsReview, Permissions.ProductionAlertsReview, Permissions.DevicesManage,
    }).ToArray();

    /// <summary>Roles and permissions for a tier name or a real role id.</summary>
    public static (string[] Roles, string[] Permissions) Tier(string tier) => tier switch
    {
        Roles.Admin => (new[] { SystemRoles.SuperAdmin }, AccessCatalog.PermissionsFor(new[] { SystemRoles.SuperAdmin }).ToArray()),
        Roles.Operator => (new[] { SystemRoles.FactoryManager }, OperatorPermissions),
        Roles.Viewer => (new[] { SystemRoles.MonitoringOperator }, ViewerPermissions),
        _ when AccessCatalog.IsRole(tier) => (new[] { tier }, AccessCatalog.PermissionsFor(new[] { tier }).ToArray()),
        _ => throw new ArgumentException($"Unknown tier or role '{tier}'."),
    };

    /// <summary>A snapshot for a token with exactly <paramref name="permissions"/>.</summary>
    public static AccessSnapshot Snapshot(Guid userId, string email, IEnumerable<string> roles, IEnumerable<string> permissions,
        string stamp, IEnumerable<FactoryRef>? factories = null, IEnumerable<string>? lines = null, bool mustChangePassword = false) =>
        new(userId, email, null, roles.ToArray(), permissions.ToArray(), (factories ?? Array.Empty<FactoryRef>()).ToArray(),
            (lines ?? Array.Empty<string>()).ToArray(), stamp, mustChangePassword);

    public sealed record DbPerson(Guid Id, string Email, string Role, string Token, HttpClient Client);

    /// <summary>
    /// A real user row (so the per-request session check passes) holding the
    /// tier's roles in the default factory, and a client whose token carries
    /// the tier's permissions.
    /// </summary>
    public static DbPerson DbUser(DbApiFactory api, string tier, string? emailTag = null)
    {
        var (roles, permissions) = Tier(tier);
        using var scope = api.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BunonBrainDbContext>();
        var factory = AccessCatalog.ScopeFor(roles) == RoleScope.Platform ? null : MainFactory(db);
        var id = Guid.NewGuid();
        var email = $"{(emailTag ?? tier).ToLowerInvariant()}-{id:N}@tests.local";
        var user = new User { Id = id, Email = email, PasswordHash = "not-a-real-hash" };
        foreach (var r in roles) user.Roles.Add(new UserRole { UserId = id, RoleId = r });
        if (factory is not null) user.Factories.Add(new UserFactoryAssignment { UserId = id, FactoryId = factory.Id });
        db.Users.Add(user);
        db.SaveChanges();

        var token = scope.ServiceProvider.GetRequiredService<ITokenService>().IssueAccessToken(
            Snapshot(id, email, roles, permissions, user.SecurityStamp,
                factory is null ? null : new[] { new FactoryRef(factory.Id, factory.Code, factory.Name) }), out _);
        var client = api.RawClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new DbPerson(id, email, AccessCatalog.PrimaryRole(roles), token, client);
    }

    /// <summary>The default factory, created if missing (tolerates a concurrent create).</summary>
    public static Factory MainFactory(BunonBrainDbContext db)
    {
        var existing = db.Factories.AsNoTracking().FirstOrDefault(f => f.Code == AccessSeeder.DefaultFactoryCode);
        if (existing is not null) return existing;
        try
        {
            var created = new Factory { Code = AccessSeeder.DefaultFactoryCode, Name = AccessSeeder.DefaultFactoryName };
            db.Factories.Add(created);
            db.SaveChanges();
            db.Entry(created).State = EntityState.Detached;
            return created;
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return db.Factories.AsNoTracking().First(f => f.Code == AccessSeeder.DefaultFactoryCode);
        }
    }
}

/// <summary>Session validator for the database-free test host: every token is current.</summary>
public sealed class AlwaysCurrentSessions : ISessionValidator
{
    public Task<bool> IsCurrentAsync(Guid userId, string? securityStamp, CancellationToken ct) =>
        Task.FromResult(!string.IsNullOrEmpty(securityStamp));
    public void Invalidate(Guid userId) { }
}
