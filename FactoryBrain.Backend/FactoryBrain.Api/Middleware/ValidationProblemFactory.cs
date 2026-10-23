using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace FactoryBrain.Api.Middleware;

/// <summary>
/// One raw error entry to feed into <see cref="ValidationProblemFactory"/>.
/// The model-state factory in <c>Program.cs</c> emits one of these per
/// <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.ModelError"/>, and the
/// FluentValidation filter emits one per
/// <see cref="FluentValidation.Results.ValidationFailure"/>. Carrying the
/// optional <see cref="Exception"/> lets the model-state caller preserve the
/// underlying <see cref="JsonException"/> even when the framework's
/// <c>AllowInputFormatterExceptionMessages</c> flag is off and the textual
/// <see cref="ErrorMessage"/> is suppressed to an empty string.
/// </summary>
public readonly record struct ValidationProblemError(
    string? RawKey,
    string? ErrorMessage,
    Exception? Exception);

/// <summary>
/// Single source of truth for the 400 <see cref="ValidationProblemDetails"/>
/// the API returns when a request body fails model binding, JSON parsing, or
/// FluentValidation. Used by <see cref="FluentValidationFilter"/> (validator
/// errors, after model binding succeeds) and by the
/// <c>ApiBehaviorOptions.InvalidModelStateResponseFactory</c> wired in
/// <c>Program.cs</c> (model-binding failures — missing required fields,
/// wrong JSON types, unparseable JSON, empty body).
///
/// <para>
/// <b>Shape contract.</b> Every response carries
/// <c>status=400</c>, the same <c>type</c>, <c>title</c> and <c>detail</c>,
/// <c>instance</c> set to <c>HttpContext.Request.Path</c>, and a
/// <c>traceId</c> extension copied from
/// <see cref="Activity.Current"/>?.Id or <see cref="HttpContext.TraceIdentifier"/>
/// when no ambient activity exists. The Content-Type stamped on the response
/// is always <c>application/problem+json</c> so clients can sniff the body
/// shape without depending on a custom field.
/// </para>
///
/// <para>
/// <b>Error keys.</b> The <c>errors</c> map is keyed by the camelCase JSON
/// property name. Two callers feed the factory:
/// <list type="number">
///   <item><b>FluentValidation filter</b> (post-model-binding): pre-camelCases
///   the validator's <c>PropertyName</c> per path segment and passes the
///   result through <see cref="BuildRaw"/> verbatim. Validator-authored
///   codes like <c>unknown_sample_file</c> land under
///   <c>errors.sampleFile</c>.</item>
///   <item><b>Model-state factory</b> (pre-action): runs
///   <see cref="NormaliseModelStateKey"/> on each raw key to strip the
///   synthetic body spellings (<c>""</c>, <c>"$"</c>, <c>"body"</c>,
///   <c>"request"</c>, or the bound action parameter's name when that name
///   is not also a real property on the parameter's type), then camelCases
///   the last path segment so <c>"$.query"</c> becomes <c>"query"</c>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Message handling.</b> This factory does NOT scrub messages — it
/// groups and de-duplicates, but the <c>ErrorMessage</c> on each
/// <see cref="ValidationProblemError"/> is emitted verbatim. The
/// model-state caller runs
/// <see cref="ScrubModelStateMessage"/> first to collapse the framework's
/// free-text messages onto one of the three canonical shapes (required /
/// wrong type / unparseable JSON). The FluentValidation path skips
/// scrubbing so codes like <c>unknown_sample_file</c>,
/// <c>unknown_operation</c> and <c>invalid_provider</c> survive intact.
/// </para>
/// </summary>
public static class ValidationProblemFactory
{
    public const string ProblemType = "https://datatracker.ietf.org/doc/html/rfc7807";
    public const string ProblemTitle = "One or more validation errors occurred.";
    public const string ProblemDetail = "See the errors property for details.";
    public const string ProblemStatus = "400";
    public const string ContentType  = "application/problem+json";

    /// <summary>
    /// Three canonical message strings emitted by the model-state funnel.
    /// Available publicly so the message-scrubbing step (separate from
    /// <see cref="Build"/>) can use the same constants.
    /// </summary>
    public const string MsgRequired     = "This field is required.";
    public const string MsgInvalidType  = "The value is not valid for this field.";
    public const string MsgInvalidJson  = "The request body is not valid JSON.";

    private static readonly char[] BodyKeyTokens = { '.', '$', '[', ']', '"' };

    /// <summary>
    /// Build a 400 <see cref="ValidationProblemDetails"/> from a flat list of
    /// raw errors. Each <see cref="ValidationProblemError.RawKey"/> is taken
    /// verbatim — no normalisation. Used by the FluentValidation filter
    /// after it has pre-camelCased the validator's <c>PropertyName</c>.
    /// </summary>
    public static ValidationProblemDetails BuildRaw(
        HttpContext httpContext,
        IEnumerable<ValidationProblemError> rawErrors)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(rawErrors);

        var grouped = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var err in rawErrors)
        {
            var key = err.RawKey ?? string.Empty;
            var msg = err.ErrorMessage;
            if (string.IsNullOrEmpty(msg)) continue;

            if (!grouped.TryGetValue(key, out var list))
            {
                list = new List<string>();
                grouped[key] = list;
            }
            if (!list.Contains(msg, StringComparer.Ordinal))
                list.Add(msg);
        }

        var errors = grouped.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToArray(),
            StringComparer.Ordinal);

        var problem = new ValidationProblemDetails(errors)
        {
            Type     = ProblemType,
            Title    = ProblemTitle,
            Status   = StatusCodes.Status400BadRequest,
            Detail   = ProblemDetail,
            Instance = httpContext.Request.Path.HasValue
                ? httpContext.Request.Path.Value!
                : string.Empty,
        };

        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;
        problem.Extensions["traceId"] = traceId;

        return problem;
    }

    /// <summary>
    /// Same as <see cref="BuildRaw"/> but returns an <see cref="ObjectResult"/>
    /// stamped with the canonical <c>application/problem+json</c> content
    /// type and a <c>Cache-Control: no-store</c> header so the response is
    /// never cached by intermediaries. <c>FluentValidationFilter</c> uses
    /// this overload directly.
    /// </summary>
    public static ObjectResult BuildRawResult(
        HttpContext httpContext,
        IEnumerable<ValidationProblemError> rawErrors)
    {
        var problem = BuildRaw(httpContext, rawErrors);
        var result = new BadRequestObjectResult(problem)
        {
            ContentTypes = { ContentType }
        };
        if (!httpContext.Response.Headers.ContainsKey("Cache-Control"))
            httpContext.Response.Headers["Cache-Control"] = "no-store";
        return result;
    }

    /// <summary>
    /// Build a 400 <see cref="ValidationProblemDetails"/> from a flat list
    /// of raw model-state errors. Each <see cref="ValidationProblemError"/>
    /// is expected to have its <c>RawKey</c> already canonicalised by
    /// <see cref="NormaliseModelStateKey"/> — the factory groups and
    /// de-duplicates without further key massaging.
    /// </summary>
    public static ValidationProblemDetails Build(
        HttpContext httpContext,
        IEnumerable<ValidationProblemError> rawErrors)
        => BuildRaw(httpContext, rawErrors);

    /// <summary>
    /// Same as <see cref="Build"/> but returns an <see cref="ObjectResult"/>
    /// stamped with the canonical <c>application/problem+json</c> content
    /// type and a <c>Cache-Control: no-store</c> header. Used by the
    /// model-state factory in <c>Program.cs</c>.
    /// </summary>
    public static ObjectResult BuildResult(
        HttpContext httpContext,
        IEnumerable<ValidationProblemError> rawErrors)
        => BuildRawResult(httpContext, rawErrors);

    /// <summary>
    /// Model-state key canonicaliser. The model-state funnel uses this to
    /// collapse the framework's key spellings onto a single camelCase field
    /// name (or <c>"body"</c>). Behaviour:
    /// <list type="number">
    ///   <item>Empty input, <c>"$"</c>, <c>"body"</c>, <c>"request"</c>, or
    ///   any path that is entirely the JSON root → <c>"body"</c>.</item>
    ///   <item>The bound action parameter's C# name (e.g. <c>"body"</c> on
    ///   every route in this API) → <c>"body"</c> only when that name is
    ///   not also a real property on the parameter's CLR type. This is the
    ///   carve-out for <c>LlmSettingsRequest.Model</c>: when the model-state
    ///   synthesises a whole-request key, the parameter is <c>body</c>, but
    ///   the actual <c>model</c> field is a real property, so the
    ///   synthetic <c>"model"</c> entry is treated as a real field error
    ///   (and lands under <c>errors.model</c>) rather than collapsed onto
    ///   <c>"body"</c>.</item>
    ///   <item>Otherwise, strip a leading <c>"$."</c> or <c>"$"</c> if
    ///   present, take the last path segment, and camelCase it so
    ///   <c>"$.query"</c> → <c>"query"</c>, <c>"Filter.Department"</c> →
    ///   <c>"department"</c>, <c>"Query"</c> → <c>"query"</c>.</item>
    /// </list>
    /// </summary>
    public static string NormaliseModelStateKey(
        string? rawKey,
        string? actionParamName = null,
        Type?   actionParamType = null)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return "body";

        // Strip the JSON-pointer prefix the model binder adds. Keys arrive
        // as "$.model", "$.query", "$.items[0].name", or "$.$" for the
        // synthetic body error. After stripping, a key like "$" or
        // "$request" still describes the whole request.
        var trimmed = rawKey.StartsWith("$.", StringComparison.Ordinal)
            ? rawKey[2..]
            : rawKey.StartsWith("$", StringComparison.Ordinal)
                ? rawKey[1..]
                : rawKey;

        if (string.IsNullOrWhiteSpace(trimmed)) return "body";

        // Split on any of the body-path separator tokens. The first
        // non-empty segment wins; if it's a pure-path segment (only "$" or
        // empty), we fall through to the next.
        var segments = trimmed
            .Split(BodyKeyTokens, StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0) return "body";

        var last = segments[^1].Trim();
        if (string.IsNullOrEmpty(last)) return "body";

        // Synthetic body error keys: "body", "request", and the JSON-root
        // "$" all collapse onto "body". Note: "model" is intentionally NOT
        // in this list — a real LlmSettingsRequest has a Model property
        // and the model binder may produce a synthetic "$.model" key on
        // model-binding failure, which must be treated as a real field
        // error so the response is errors.model.
        if (segments.Length == 1 &&
            last.Equals("body", StringComparison.OrdinalIgnoreCase))
            return "body";
        if (segments.Length == 1 &&
            last.Equals("request", StringComparison.OrdinalIgnoreCase))
            return "body";

        // Bound action parameter name (e.g. the C# parameter named "body"
        // on every route in this API) — only collapse when that name is
        // NOT also a real property on the bound parameter's CLR type. If
        // a future route binds a parameter literally named "model" or
        // "request", the carve-out is what makes sure a real field by the
        // same name is not accidentally folded into "body".
        if (!string.IsNullOrEmpty(actionParamName) &&
            last.Equals(actionParamName, StringComparison.OrdinalIgnoreCase) &&
            !TypeHasProperty(actionParamType, last))
        {
            return "body";
        }

        return ToCamel(last);
    }

    /// <summary>
    /// True for the synthetic body / request keys that always describe the
    /// whole request rather than a specific field, and for the bound
    /// action parameter's name when it is not also a real property on
    /// the parameter's CLR type. Used by the model-state factory to
    /// decide which "required" entries are redundant in the presence of
    /// a more specific field error.
    /// </summary>
    public static bool IsWholeRequestModelStateKey(
        string? rawKey,
        string? actionParamName = null,
        Type?   actionParamType = null)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return true;

        var trimmed = rawKey.StartsWith("$.", StringComparison.Ordinal)
            ? rawKey[2..]
            : rawKey.StartsWith("$", StringComparison.Ordinal)
                ? rawKey[1..]
                : rawKey;

        if (string.IsNullOrWhiteSpace(trimmed)) return true;

        var segments = trimmed
            .Split(BodyKeyTokens, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return true;

        var last = segments[^1].Trim();
        if (string.IsNullOrEmpty(last)) return true;

        if (last.Equals("body", StringComparison.OrdinalIgnoreCase)) return true;
        if (last.Equals("request", StringComparison.OrdinalIgnoreCase)) return true;
        if (last.Equals("$", StringComparison.Ordinal)) return true;
        if (!string.IsNullOrEmpty(actionParamName) &&
            last.Equals(actionParamName, StringComparison.OrdinalIgnoreCase) &&
            !TypeHasProperty(actionParamType, last))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="type"/> exposes a public instance property
    /// whose name matches <paramref name="name"/> (case-insensitive). Used
    /// to decide whether a candidate whole-request key is actually a real
    /// field on the bound DTO.
    /// </summary>
    private static bool TypeHasProperty(Type? type, string name)
    {
        if (type is null) return false;
        return type.GetProperty(name,
            System.Reflection.BindingFlags.Public |
            System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.IgnoreCase) is not null;
    }

    private static string ToCamel(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        if (name.Length == 1) return name.ToLowerInvariant();
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>
    /// Reduce a raw <see cref="Microsoft.AspNetCore.Mvc.ModelBinding.ModelError"/>
    /// to one of the three canonical messages. Used by
    /// <c>InvalidModelStateResponseFactory</c> before calling
    /// <see cref="Build"/>. FluentValidation calls skip this — they pass
    /// validator-authored messages through unchanged so codes like
    /// <c>unknown_sample_file</c>, <c>unknown_operation</c> and
    /// <c>invalid_provider</c> survive intact.
    /// </summary>
    public static string ScrubModelStateMessage(ValidationProblemError err)
    {
        var msg = err.ErrorMessage;

        // 1. JsonException (parse or type-conversion failure) is the
        //    strongest signal. System.Text.Json throws it both for
        //    unparseable JSON literals and for "could not convert" type
        //    mismatches. Distinguish by message text:
        //    * "is an invalid JSON literal" / "could not convert ... to
        //      JsonTypeInfo" → unparseable / wrong root type → MsgInvalidJson.
        //    * "could not convert ... to <.NET type>" → wrong-type for an
        //      individual field → MsgInvalidType.
        //    * The exception's Message when AllowInputFormatterExceptionMessages
        //      is off is empty; in that case fall through to the textual
        //      heuristics.
        if (err.Exception is JsonException jex)
        {
            var jmsg = jex.Message ?? string.Empty;
            if (jmsg.Contains("is an invalid JSON literal", StringComparison.OrdinalIgnoreCase) ||
                jmsg.Contains("is not a valid value", StringComparison.OrdinalIgnoreCase) ||
                jmsg.Contains("could not be parsed", StringComparison.OrdinalIgnoreCase) ||
                jmsg.Contains("is not valid JSON", StringComparison.OrdinalIgnoreCase))
            {
                return MsgInvalidJson;
            }
            // The path on a JsonException is the JSON pointer that failed;
            // if it points at a non-root location, the body parsed but a
            // field type was wrong.
            var path = jex.Path;
            if (!string.IsNullOrEmpty(path) && path != "$")
                return MsgInvalidType;
            // Path == "$" or empty → the whole body is unparseable.
            return MsgInvalidJson;
        }

        if (string.IsNullOrWhiteSpace(msg)) return MsgInvalidType;

        // 2. Heuristics on the textual ErrorMessage (the framework may have
        //    rewritten it even when an exception is present).
        if (LooksLikeParseError(msg))
            return MsgInvalidJson;

        if (LooksLikeRequired(msg))
            return MsgRequired;

        // 3. Anything else: wrong type, range, format, etc.
        return MsgInvalidType;
    }

    /// <summary>
    /// True when the canonical message is the "required" variant. Used by
    /// the model-state factory to decide which whole-request entries are
    /// redundant in the presence of a more specific field error.
    /// </summary>
    public static bool IsRequiredCanonicalMessage(string canonical)
        => string.Equals(canonical, MsgRequired, StringComparison.Ordinal);

    private static bool LooksLikeParseError(string msg)
    {
        if (msg.Contains("invalid JSON", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("is an invalid JSON", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("could not convert", StringComparison.OrdinalIgnoreCase) &&
            msg.Contains("JSON", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("LineNumber", StringComparison.Ordinal)) return true;
        if (msg.Contains("BytePositionInLine", StringComparison.Ordinal)) return true;
        if (msg.Contains("Path:", StringComparison.Ordinal) &&
            msg.Contains("LineNumber", StringComparison.Ordinal)) return true;
        if (msg.StartsWith("'", StringComparison.Ordinal) &&
            msg.Contains("is an invalid JSON literal", StringComparison.Ordinal)) return true;
        return false;
    }

    private static bool LooksLikeRequired(string msg)
    {
        if (msg.Contains("field is required", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("property is required", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("non-empty request body is required", StringComparison.OrdinalIgnoreCase)) return true;
        if (msg.Contains("body field is required", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
