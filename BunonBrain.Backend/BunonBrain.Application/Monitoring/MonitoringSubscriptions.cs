using BunonBrain.Application.Access;

namespace BunonBrain.Application.Monitoring;

/// <summary>The groups a monitoring connection joins, or why it can't subscribe.</summary>
public sealed record SubscriptionPlan(
    IReadOnlyList<string> Groups, IReadOnlyList<Guid> Factories, IReadOnlyList<string> Lines, IReadOnlyList<string> Categories,
    bool AllFactories, string? Error = null, string? Message = null)
{
    public static SubscriptionPlan Refused(string code, string message) =>
        new(Array.Empty<string>(), Array.Empty<Guid>(), Array.Empty<string>(), Array.Empty<string>(), false, code, message);
}

/// <summary>
/// Decides which SignalR groups a connection may join, from the caller's
/// validated token only (never from what the client asks for beyond picking
/// one of its own factories). A connection never receives another factory's
/// or another line's events, and VISION events only with <c>monitoring.view</c>.
/// </summary>
public static class MonitoringSubscriptions
{
    public const int MaxGroups = 200;

    public static SubscriptionPlan Plan(AccessContext caller, Guid? factoryId)
    {
        var categories = new List<string>();
        if (caller.Has(Permissions.MonitoringView)) categories.Add(MonitoringCategories.Vision);
        if (caller.HasAny(Permissions.MonitoringView, Permissions.DevicesView)) categories.Add(MonitoringCategories.System);
        if (categories.Count == 0) return SubscriptionPlan.Refused("monitoring_access_required", "You don't have access to monitoring updates.");
        if (factoryId is { } f && !caller.CanAccessFactory(f))
            return SubscriptionPlan.Refused("factory_out_of_scope", "You can only follow factories you have access to.");

        switch (caller.Scope)
        {
            case RoleScope.Platform when factoryId is null:
                return new SubscriptionPlan(categories.Select(MonitoringGroups.Platform).ToList(), Array.Empty<Guid>(), Array.Empty<string>(), categories, true);
            case RoleScope.Platform:
            case RoleScope.Factory:
            {
                var factories = factoryId is { } one ? new List<Guid> { one } : caller.FactoryIds.OrderBy(x => x).ToList();
                if (factories.Count == 0) return SubscriptionPlan.Refused("no_factory", "Your account isn't assigned to a factory.");
                var groups = factories.SelectMany(fid => categories.Select(c => MonitoringGroups.Factory(fid, c))).Take(MaxGroups).ToList();
                return new SubscriptionPlan(groups, factories, Array.Empty<string>(), categories, false);
            }
            case RoleScope.Line:
            {
                // Line groups carry every category, so line-scoped users need monitoring.view.
                if (!caller.Has(Permissions.MonitoringView))
                    return SubscriptionPlan.Refused("monitoring_access_required", "Line-level monitoring updates need the monitoring permission.");
                var lines = caller.LineIds.OrderBy(x => x, StringComparer.Ordinal).Take(MaxGroups).ToList();
                if (lines.Count == 0) return SubscriptionPlan.Refused("no_line", "You aren't assigned to a production line.");
                return new SubscriptionPlan(lines.Select(MonitoringGroups.Line).ToList(), caller.FactoryIds.ToList(), lines, categories, false);
            }
            default:
                return SubscriptionPlan.Refused("monitoring_access_required", "You don't have access to monitoring updates.");
        }
    }

    /// <summary>Every group an item goes to: its factory's group for its category, the platform group, and its line's group.</summary>
    public static IReadOnlyList<string> Targets(Guid factoryId, string? lineId, string category)
    {
        var groups = new List<string> { MonitoringGroups.Factory(factoryId, category), MonitoringGroups.Platform(category) };
        if (lineId is not null) groups.Add(MonitoringGroups.Line(lineId));
        return groups;
    }
}
