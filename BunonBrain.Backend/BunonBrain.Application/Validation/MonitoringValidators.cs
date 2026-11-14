using BunonBrain.Application.Dtos.Monitoring;
using BunonBrain.Application.Monitoring;
using FluentValidation;

namespace BunonBrain.Application.Validation;

// Shape checks only. The services check the contract per event type, every
// reference against the database, identities, state transitions and reach.

public class IngestMonitoringEventRequestValidator : AbstractValidator<IngestMonitoringEventRequest>
{
    public IngestMonitoringEventRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.EventType).NotEmpty();
        RuleFor(x => x.IdempotencyKey).MaximumLength(MonitoringRules.IdempotencyKeyMax);
        RuleFor(x => x.TrackId).MaximumLength(MonitoringRules.TrackIdMax);
        RuleFor(x => x.LineId).MaximumLength(32);
        RuleFor(x => x.ModelName).MaximumLength(MonitoringRules.ModelNameMax);
    }
}

public class ReviewMonitoringEventRequestValidator : AbstractValidator<ReviewMonitoringEventRequest>
{
    public ReviewMonitoringEventRequestValidator()
    {
        RuleFor(x => x.Decision).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(MonitoringRules.NoteMax);
    }
}

public class EscalateAlertRequestValidator : AbstractValidator<EscalateAlertRequest>
{
    public EscalateAlertRequestValidator()
    {
        RuleFor(x => x.ToRole).NotEmpty();
        RuleFor(x => x.Reason).MaximumLength(MonitoringRules.NoteMax);
    }
}

public class ResolveAlertRequestValidator : AbstractValidator<ResolveAlertRequest>
{
    public ResolveAlertRequestValidator()
    {
        RuleFor(x => x.ResolutionCode).NotEmpty();
        RuleFor(x => x.Note).MaximumLength(MonitoringRules.NoteMax);
    }
}

public class StartSimulationRequestValidator : AbstractValidator<StartSimulationRequest>
{
    public StartSimulationRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.CameraIds).Must(l => l is null || l.Count <= MonitoringSimulation.MaxCameras)
            .WithMessage($"At most {MonitoringSimulation.MaxCameras} cameras per run.");
    }
}

public class TriggerSimulatedEventRequestValidator : AbstractValidator<TriggerSimulatedEventRequest>
{
    public TriggerSimulatedEventRequestValidator()
    {
        RuleFor(x => x.FactoryId).NotEmpty();
        RuleFor(x => x.EventType).NotEmpty();
    }
}
