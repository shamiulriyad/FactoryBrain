namespace BunonBrain.Application.Planning;

// Rule-based production exceptions. Each rule takes the facts it needs,
// returns a detection (or nothing) with the evidence it used, a reason
// written for people, and what to look into next. A rule never names a
// worker as the cause: the evidence is output, counters, machines and
// capacity, and the suggestions are things to check, not conclusions.

public sealed record Fact(string Label, string Value);

public sealed record DetectedException(
    string Type, string Severity, string DedupeKey, string Title, string Reason, IReadOnlyList<Fact> Data, string SuggestedInvestigation,
    Guid? OrderId, Guid? AllocationId, Guid? PlanItemId, string? LineId, Guid? ShiftId, DateOnly? Date);

/// <summary>A published plan item with its progress, as the rules see it.</summary>
public sealed record ItemFacts(
    Guid ItemId, Guid OrderId, string OrderNumber, string LineId, string LineName, Guid ShiftId, string ShiftName, DateOnly Date,
    ItemProgress Progress, bool LineHasOutputPoint, int CounterEventsInShift, DateTimeOffset? LastCounterEventAt, bool OrderIsLive);

public sealed record OrderFacts(
    Guid OrderId, string OrderNumber, string? Buyer, DateOnly DeliveryDeadline, int Quantity, int Produced, string ForecastBasis, OrderForecast Forecast, DateOnly Today);

public sealed record LineFacts(
    string LineId, string LineName, bool LineActive, int MachinesTotal, int MachinesUnavailable, IReadOnlyList<string> UnavailableMachineCodes, bool HasPublishedWorkToday);

public sealed record StoppageFacts(
    string LineId, string LineName, Guid? ShiftId, string? ShiftName, DateOnly Date, IReadOnlyList<(string Code, string State, int Minutes)> Machines, int ThresholdMinutes);

public sealed record AllocationFacts(
    Guid AllocationId, Guid OrderId, string OrderNumber, string LineId, string LineName, int OrderQuantity, int AllocatedTotal, bool OverrideRecorded);

public static class ExceptionRules
{
    /// <summary>Productive minutes that must pass before a shift's pace is judged.</summary>
    public const double MinProductiveMinutes = 60;
    /// <summary>Minutes into a shift before silence from the line's counters is reported.</summary>
    public const double MinSilenceMinutes = 30;

    private const string NoBlame = "This is based on counters, machines and capacity only; it doesn't show why output is where it is or who is responsible.";

    private static IReadOnlyList<Fact> F(params (string, string)[] x) => x.Select(t => new Fact(t.Item1, t.Item2)).ToList();

    public static DetectedException? OutputBehind(ItemFacts f)
    {
        var p = f.Progress;
        if (!f.OrderIsLive || p.Basis == ActualBases.None || p.Actual is null) return null;
        if (p.State is not (ItemStates.Behind or ItemStates.Missed)) return null;
        if (p.ProductiveMinutesElapsed < MinProductiveMinutes && p.State != ItemStates.Missed) return null;
        var basisPct = p.State == ItemStates.Missed || p.ExpectedByNow is not > 0
            ? (p.AchievementPct ?? 0)
            : p.Actual.Value / (double)p.ExpectedByNow.Value * 100.0;
        var severity = PlanMath.SeverityForShortfall(basisPct);
        var verified = p.Basis == ActualBases.Counter ? "counter pieces" : "recorded pieces";
        var reason = p.State == ItemStates.Missed
            ? $"The shift ended with {p.Actual} of {p.Target} planned pieces ({p.AchievementPct}%)."
            : $"{p.Actual} {verified} so far against {p.ExpectedByNow} expected at this point of the shift (target {p.Target}).";
        return new DetectedException(ExceptionTypes.OutputBehindTarget, severity, $"{ExceptionTypes.OutputBehindTarget}:{f.ItemId}",
            $"{f.LineName}: output behind the published target for {f.OrderNumber}", reason,
            F(("Order", f.OrderNumber), ("Line", f.LineName), ("Shift", f.ShiftName), ("Date", f.Date.ToString("yyyy-MM-dd")), ("Target", p.Target.ToString()),
                ("Actual", p.Actual.Value.ToString()), ("Expected by now", (p.ExpectedByNow ?? 0).ToString()),
                ("Productive minutes elapsed", Math.Round(p.ProductiveMinutesElapsed).ToString()), ("Basis", p.Basis)),
            "Check the line's machine states and stoppages for this shift, material and bundle flow into the line, and whether the counter reflects completed garments. " + NoBlame,
            f.OrderId, null, f.ItemId, f.LineId, f.ShiftId, f.Date);
    }

    public static DetectedException? MissingCounter(ItemFacts f)
    {
        var p = f.Progress;
        if (!f.OrderIsLive || p.ProductiveMinutesElapsed < MinSilenceMinutes) return null;
        if (p.Basis != ActualBases.None && p.Basis != ActualBases.Recorded) return null;
        if (p.Basis == ActualBases.Recorded) return null;
        string reason;
        string suggestion;
        string severity;
        if (!f.LineHasOutputPoint)
        {
            reason = $"No counter is designated as the finished-garment output of {f.LineName}, so this shift's output can't be verified.";
            suggestion = "Designate the counter that counts finished garments for this line in the planning setup, or record output manually.";
            severity = Severities.Low;
        }
        else if (f.CounterEventsInShift == 0)
        {
            reason = $"The output counter of {f.LineName} has sent no count since the shift started ({Math.Round(p.ProductiveMinutesElapsed)} productive minutes). That is missing data, not zero production.";
            suggestion = "Check the counter device's connection and heartbeat, the gateway, and whether the line is actually running.";
            severity = Severities.Medium;
        }
        else return null;
        return new DetectedException(ExceptionTypes.MissingCounterData, severity, $"{ExceptionTypes.MissingCounterData}:{f.ItemId}",
            $"{f.LineName}: no production counter data for {f.OrderNumber}", reason,
            F(("Order", f.OrderNumber), ("Line", f.LineName), ("Shift", f.ShiftName), ("Date", f.Date.ToString("yyyy-MM-dd")),
                ("Output counter configured", f.LineHasOutputPoint ? "yes" : "no"), ("Counter messages in shift", f.CounterEventsInShift.ToString()),
                ("Last counter message", f.LastCounterEventAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "never")),
            suggestion, f.OrderId, null, f.ItemId, f.LineId, f.ShiftId, f.Date);
    }

    public static DetectedException? DeliveryAtRisk(OrderFacts o)
    {
        var fc = o.Forecast;
        if (fc.Status is not (OrderForecasts.AtRisk or OrderForecasts.Overdue)) return null;
        var severity = fc.Status == OrderForecasts.Overdue || fc.WorkingDaysLeft <= 2 ? Severities.High : Severities.Medium;
        return new DetectedException(ExceptionTypes.DeliveryAtRisk, severity, $"{ExceptionTypes.DeliveryAtRisk}:{o.OrderId}",
            $"Order {o.OrderNumber} may miss its delivery deadline", fc.Note,
            F(("Order", o.OrderNumber), ("Buyer", o.Buyer ?? "—"), ("Delivery deadline", o.DeliveryDeadline.ToString("yyyy-MM-dd")), ("Ordered", o.Quantity.ToString()),
                ("Counted", o.Produced.ToString()), ("Remaining", fc.Remaining.ToString()), ("Pace (pieces/day)", fc.DailyRate?.ToString() ?? "—"),
                ("Working days needed", fc.WorkingDaysNeeded?.ToString() ?? "—"), ("Working days left", fc.WorkingDaysLeft.ToString()), ("Basis", o.ForecastBasis)),
            "Review the allocations and daily targets for this order, whether more lines or shifts can take part of it, and talk to the buyer early if the date can't hold. " + NoBlame,
            o.OrderId, null, null, null, null, o.Today);
    }

    public static DetectedException? LineUnavailable(LineFacts l, DateOnly date)
    {
        if (!l.HasPublishedWorkToday) return null;
        string? reason = null;
        if (!l.LineActive) reason = $"{l.LineName} is deactivated but has published work today.";
        else if (l.MachinesTotal > 0 && l.MachinesUnavailable >= l.MachinesTotal)
            reason = $"All {l.MachinesTotal} monitored machine(s) of {l.LineName} are in error, maintenance or offline, and the line has published work today.";
        if (reason is null) return null;
        return new DetectedException(ExceptionTypes.LineUnavailable, Severities.High, $"{ExceptionTypes.LineUnavailable}:{l.LineId}:{date:yyyy-MM-dd}",
            $"{l.LineName} is unavailable", reason,
            F(("Line", l.LineName), ("Line active", l.LineActive ? "yes" : "no"), ("Monitored machines", l.MachinesTotal.ToString()),
                ("Unavailable machines", l.MachinesUnavailable.ToString()), ("Machines", l.UnavailableMachineCodes.Count == 0 ? "—" : string.Join(", ", l.UnavailableMachineCodes))),
            "Check the machine states and maintenance records, the devices' connection (offline can mean a lost sensor, not a stopped machine), and move the line's allocation if it can't run. " + NoBlame,
            null, null, null, l.LineId, null, date);
    }

    public static DetectedException? Interruption(StoppageFacts s)
    {
        var stopped = s.Machines.Where(m => m.Minutes >= s.ThresholdMinutes).ToList();
        if (stopped.Count == 0) return null;
        var severity = stopped.Count >= 3 || stopped.Max(m => m.Minutes) >= s.ThresholdMinutes * 3 ? Severities.High : Severities.Medium;
        return new DetectedException(ExceptionTypes.ProductionInterruption, severity, $"{ExceptionTypes.ProductionInterruption}:{s.LineId}:{s.Date:yyyy-MM-dd}",
            $"{s.LineName}: unplanned interruption during {s.ShiftName ?? "the shift"}",
            $"{stopped.Count} machine(s) have been stopped or in error for {s.ThresholdMinutes} minutes or more during a scheduled shift, outside scheduled breaks.",
            F(("Line", s.LineName), ("Shift", s.ShiftName ?? "—"), ("Date", s.Date.ToString("yyyy-MM-dd")),
                ("Machines", string.Join("; ", stopped.Select(m => $"{m.Code} {m.State} {m.Minutes} min")))),
            "Check why these machines stopped (breakdown, material wait, power, changeover) with the line supervisor and the machine alerts. " + NoBlame,
            null, null, null, s.LineId, s.ShiftId, s.Date);
    }

    public static DetectedException? ExcessAllocation(AllocationFacts a)
    {
        if (a.AllocatedTotal <= a.OrderQuantity) return null;
        var excess = a.AllocatedTotal - a.OrderQuantity;
        return new DetectedException(ExceptionTypes.ExcessAllocation, a.OverrideRecorded ? Severities.Low : Severities.Medium, $"{ExceptionTypes.ExcessAllocation}:{a.OrderId}",
            $"Order {a.OrderNumber} is allocated beyond its quantity",
            $"Allocations total {a.AllocatedTotal} pieces for an order of {a.OrderQuantity} ({excess} over){(a.OverrideRecorded ? "; an authorised override is recorded" : "")}.",
            F(("Order", a.OrderNumber), ("Order quantity", a.OrderQuantity.ToString()), ("Allocated", a.AllocatedTotal.ToString()), ("Excess", excess.ToString()),
                ("Override recorded", a.OverrideRecorded ? "yes" : "no")),
            "Confirm whether the extra quantity is intentional (e.g. a cutting allowance for rejects) or reduce an allocation.",
            a.OrderId, a.AllocationId, null, a.LineId, null, null);
    }

    public static DetectedException? CapacityShortage(Guid orderId, string orderNumber, Guid allocationId, string lineId, string lineName, CapacityResult c)
    {
        if (c.Status != CapacityEstimator.Complete || c.ShortagePieces is not > 0 || c.CapacityPieces is not { } cap) return null;
        var severity = (c.UtilizationPct ?? 0) >= 130 ? Severities.High : Severities.Medium;
        return new DetectedException(ExceptionTypes.CapacityShortage, severity, $"{ExceptionTypes.CapacityShortage}:{allocationId}",
            $"{lineName}: estimated capacity is short for {orderNumber}",
            $"The configured inputs give an estimated capacity of {cap} pieces for the allocation's period; planned plus other commitments need {Math.Round(cap + c.ShortagePieces.Value, 1)} ({c.UtilizationPct}% utilisation).",
            F(("Order", orderNumber), ("Line", lineName), ("Estimated capacity", cap.ToString()), ("Planned", c.PlannedQuantity.ToString()), ("Other commitments", c.CommittedElsewhere.ToString()),
                ("Utilisation %", c.UtilizationPct?.ToString() ?? "—"), ("Shortage", c.ShortagePieces.Value.ToString()), ("Basis", "estimate from configured inputs")),
            "Review the assumptions (efficiency, operators, SMV) with the line supervisor, then consider moving part of the allocation to another line, adding operators, or extending the period. This is an estimate, not measured output.",
            orderId, allocationId, null, lineId, null, null);
    }
}
