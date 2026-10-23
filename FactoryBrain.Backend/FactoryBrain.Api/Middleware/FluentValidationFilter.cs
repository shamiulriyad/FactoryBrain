using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FactoryBrain.Api.Middleware;

/// <summary>
/// Action-stage filter that runs every <see cref="IValidator{T}"/> registered
/// for an action's bound arguments before the action method runs. On
/// failure it short-circuits with a <see cref="BadRequestObjectResult"/>
/// wrapping a <see cref="ValidationProblemDetails"/> — RFC 7807's
/// <c>application/problem+json</c> shape with an <c>errors</c> map keyed by
/// the camel-cased property name.
///
/// <para>
/// All shape decisions (status, type, title, detail, instance, traceId,
/// content type, no-store header) live in
/// <see cref="ValidationProblemFactory"/>. This filter is the FluentValidation
/// funnel; the model-binding funnel is
/// <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> in
/// <c>Program.cs</c>. Both feed the same factory so a client gets the same
/// 400 envelope whether the failure was caught by FluentValidation
/// (validator rule) or by model binding (missing required field, wrong
/// type, unparseable JSON).
/// </para>
///
/// <para>
/// <b>Keys are camelCased per path segment, not whole-request-collapsed.</b>
/// Step 51-fix-3 stopped calling
/// <see cref="ValidationProblemFactory.NormaliseModelStateKey"/> from this
/// path. Validator <c>PropertyName</c> arrives as the C# property name
/// (e.g. <c>Model</c>, <c>Items[0].Name</c>, <c>Filter.Department</c>) and
/// the filter lower-cases the first letter of each path segment before
/// handing the result to <see cref="ValidationProblemFactory.BuildRawResult"/>
/// verbatim. No key ever collapses onto <c>"body"</c> from this path — a
/// real <c>Model</c> property lands under <c>errors.model</c>, never
/// <c>errors.body</c>.
/// </para>
///
/// <para>
/// <b>Messages are passed through verbatim.</b> Step 51-fix-2 changed the
/// contract so validator-authored messages (default FluentValidation
/// templates, application codes like <c>unknown_sample_file</c>,
/// <c>unknown_operation</c>, <c>invalid_provider</c>, the
/// <c>source must be one of: …</c> hint, and the
/// <c>content exceeds N bytes (max 200 KB)</c> message) flow straight to
/// the response without scrubbing. Only the model-state funnel calls
/// <see cref="ValidationProblemFactory.ScrubModelStateMessage"/>, where the
/// three canonical shapes (required / wrong type / unparseable JSON) are
/// useful because framework-generated messages can leak .NET type names.
/// </para>
///
/// <para>
/// <b>What it does NOT do:</b> it does not replace explicit envelopes. The
/// <c>AdminToken</c> filter still returns <c>{error:"AdminTokenMissing"}</c>
/// / <c>{error:"AdminTokenNotConfigured"}</c>, the 409 degraded reindex
/// still returns <c>{error:"EmbeddingProviderUnavailable"}</c>, and the
/// per-route <c>BadRequest(new{...})</c> calls in controllers that have
/// already passed validation are still emitted directly. This filter only
/// touches the pre-action validation step.
/// </para>
/// </summary>
public sealed class FluentValidationFilter : IAsyncActionFilter
{
    private readonly IServiceProvider _sp;
    private readonly ILogger<FluentValidationFilter> _log;

    public FluentValidationFilter(IServiceProvider sp, ILogger<FluentValidationFilter> log)
    {
        _sp = sp; _log = log;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext ctx, ActionExecutionDelegate next)
    {
        foreach (var (name, value) in ctx.ActionArguments)
        {
            if (value is null) continue;

            var validatorType = typeof(IValidator<>).MakeGenericType(value.GetType());
            var validator = _sp.GetService(validatorType) as IValidator;
            if (validator is null) continue;

            var validationContext = new ValidationContext<object>(value);
            var fluentResults = await validator.ValidateAsync(validationContext, ctx.HttpContext.RequestAborted);

            if (!fluentResults.IsValid)
            {
                // Step 51-fix-2: pass FluentValidation messages through
                // verbatim — no scrubbing, no canonicalisation. The
                // validator author has chosen the message text (or used a
                // default FluentValidation template) and we must preserve
                // codes like unknown_sample_file, unknown_operation,
                // invalid_provider, as well as the default
                // NotEmpty / MaximumLength / InclusiveBetween templates.
                //
                // Step 51-fix-3: do NOT route keys through
                // NormaliseModelStateKey. The validator's PropertyName
                // is the C# property path (e.g. "Model",
                // "Items[0].Name", "Filter.Department"); we lower-case
                // the first letter of each segment and pass the result
                // straight to the factory's BuildRaw. No "body" collapse
                // ever happens from this path, so a real Model field
                // stays errors.model.
                var raw = fluentResults.Errors
                    .Select(e => new ValidationProblemError(
                        CamelCaseValidatorPath(e.PropertyName),
                        e.ErrorMessage,
                        null));

                _log.LogDebug(
                    "Validation failed for {Controller}.{Action} arg {Arg}: {ErrorCount} error(s).",
                    ctx.Controller.GetType().Name,
                    ctx.ActionDescriptor.DisplayName,
                    name,
                    fluentResults.Errors.Count);

                ctx.Result = ValidationProblemFactory.BuildRawResult(ctx.HttpContext, raw);
                return;
            }
        }

        await next();
    }

    /// <summary>
    /// CamelCase every path segment in a FluentValidation
    /// <c>PropertyName</c>. Splits on the JSON-path separators
    /// <c>.</c>, <c>[</c>, <c>]</c>, and <c>"</c> so a single failure on a
    /// nested member lands under a path-stable key (e.g.
    /// <c>Items[0].Name</c> → <c>items[0].name</c>,
    /// <c>Filter.Department</c> → <c>filter.department</c>,
    /// <c>Model</c> → <c>model</c>).
    /// </summary>
    private static string CamelCaseValidatorPath(string? propertyName)
    {
        if (string.IsNullOrWhiteSpace(propertyName)) return string.Empty;

        var chars = propertyName.ToCharArray();
        bool atSegmentStart = true;
        bool inQuotes = false;
        for (int i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (inQuotes) continue;
            if (c == '.' || c == '[' || c == ']')
            {
                atSegmentStart = true;
                continue;
            }
            if (atSegmentStart && char.IsUpper(c))
            {
                chars[i] = char.ToLowerInvariant(c);
            }
            // Anything after the first char of a segment is left as the
            // validator produced it (so acronyms like "ID" stay
            // recognisable in the errors map).
            atSegmentStart = false;
        }
        return new string(chars);
    }
}
