using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BunonBrain.Application.Monitoring;

namespace BunonBrain.Application.CameraAi;

// Camera AI: browser webcam frames analysed by the OpenAI vision API (no
// detection model is hosted). Everything in this file is pure: the JSON
// schema the model must answer with, the validation of what it answered,
// and the JPEG checks on what the browser sent. The model's answer is an
// estimate about one still frame; it never names a person, and it carries
// no numeric confidence (the model's own scores are not calibrated, so none
// is asked for and none is invented). Boxes are approximate by contract.

/// <summary>Helmet / safety-vest status of one person, or of the frame.</summary>
public static class PpeStatus
{
    /// <summary>Clearly worn.</summary>
    public const string Present = "present";
    /// <summary>The head (helmet) or torso (vest) is clearly visible and the item is clearly not worn.</summary>
    public const string Missing = "missing";
    /// <summary>Visible, but the model can't tell.</summary>
    public const string Unclear = "unclear";
    /// <summary>The relevant body part is not visible, or nobody is in the frame.</summary>
    public const string Unknown = "unknown";
    public static readonly string[] All = { Present, Missing, Unclear, Unknown };

    /// <summary>One value for the frame: any person missing it → missing; everyone wearing it → present; else unclear / unknown.</summary>
    public static string Aggregate(IEnumerable<string> perPerson)
    {
        var values = perPerson.ToList();
        if (values.Count == 0) return Unknown;
        if (values.Contains(Missing)) return Missing;
        if (values.All(v => v == Present)) return Present;
        return values.Contains(Unclear) || values.Contains(Present) ? Unclear : Unknown;
    }
}

public static class PresenceStatus
{
    public const string Present = "present";
    public const string Absent = "absent";
    public const string Unclear = "unclear";
    public static readonly string[] All = { Present, Absent, Unclear };
}

public static class IncidentStatus
{
    public const string None = "none";
    public const string Possible = "possible";
    public const string Unclear = "unclear";
    public static readonly string[] All = { None, Possible, Unclear };
}

public static class IncidentTypes
{
    public static readonly string[] All = { "FALL", "PERSON_ON_GROUND", "FIRE_OR_SMOKE", "BLOCKED_PATH", "UNSAFE_MACHINE_USE", "OTHER" };
}

public static class ImageQualities
{
    public const string Good = "good";
    public const string Poor = "poor";
    public const string Unusable = "unusable";
    public static readonly string[] All = { Good, Poor, Unusable };
}

/// <summary>PPE items a camera AI session can require (CAMERA_AI_PPE_REQUIRED).</summary>
public static class PpeItems
{
    public const string Helmet = "HELMET";
    public const string Vest = "SAFETY_VEST";
    public static readonly string[] All = { Helmet, Vest };

    public static string? Parse(string? v) => v?.Trim().ToUpperInvariant() switch
    {
        "HELMET" or "HARD_HAT" or "HARDHAT" => Helmet,
        "VEST" or "SAFETY_VEST" or "HI_VIS" or "HIVIS" => Vest,
        _ => null,
    };
}

/// <summary>An approximate box in fractions of the frame (origin top-left). Never precise: the model estimates it.</summary>
public sealed record AiBox(double X, double Y, double Width, double Height);

public sealed record AiPerson(string Activity, string Helmet, string SafetyVest, AiBox? Box);

public sealed record AiIncident(string Status, string? Type, string? Description);

/// <summary>A validated answer for one frame. <see cref="Adjustments"/> lists what validation changed, so nothing is silently rewritten.</summary>
public sealed record VisionAnalysis(
    string PersonPresence,
    int? PersonCount,
    IReadOnlyList<AiPerson> People,
    string SceneActivity,
    AiIncident Incident,
    string ImageQuality,
    string? Notes,
    IReadOnlyList<string> Adjustments)
{
    public string Helmet => PersonPresence == PresenceStatus.Present ? PpeStatus.Aggregate(People.Select(p => p.Helmet)) : PpeStatus.Unknown;
    public string SafetyVest => PersonPresence == PresenceStatus.Present ? PpeStatus.Aggregate(People.Select(p => p.SafetyVest)) : PpeStatus.Unknown;

    /// <summary>Presence the temporal rules may use: an unusable image proves nothing either way.</summary>
    public string EffectivePresence => ImageQuality == ImageQualities.Unusable ? PresenceStatus.Unclear : PersonPresence;

    public string PpeOf(string item) => item == PpeItems.Helmet ? Helmet : SafetyVest;
}

public static class CameraAiContract
{
    public const string Purpose = "camera.vision";
    public const string SchemaName = "camera_frame_analysis";
    public const int MaxPeople = 10;
    public const int MaxCount = 100;
    public const int MaxTextLength = 300;

    /// <summary>Scene / person activity labels: the platform's activity vocabulary (ActivityLabels), UNKNOWN when one frame can't tell.</summary>
    public static IReadOnlyList<string> Activities => ActivityLabels.All;

    public const string SystemPrompt =
        "You analyse ONE still frame from a factory safety camera and answer only with the JSON object the schema describes. " +
        "Report only what is visible in this frame. Never identify anyone: no names, no identity or face descriptions, no guesses about who a person is. " +
        "person_presence: 'present' only if at least one person is clearly visible; 'absent' only if the image is clear and nobody is visible; otherwise 'unclear'. " +
        "person_count: approximate number of visible people, or null if you cannot estimate it. " +
        "people: one entry per clearly visible person (at most 10). activity: the closest label; UNKNOWN when a single frame does not show it. " +
        "helmet and safety_vest: 'present' = clearly worn; 'missing' = the head (helmet) or upper body (vest) is clearly visible and the item is clearly not worn; " +
        "'unclear' = visible but you cannot tell; 'unknown' = the relevant body part is not visible. " +
        "box: approximate bounding box of the person as fractions of the image width and height (x, y = top-left corner, all between 0 and 1), or null if unsure. " +
        "scene_activity: the main activity in the frame (UNKNOWN if nobody is visible or it is not clear). " +
        "safety_incident.status: 'possible' only for visible signs of a fall, a person lying on the ground, fire or smoke, a blocked path or unsafe machine use; " +
        "'none' if nothing suggests an incident; 'unclear' if the image does not allow a judgement. type is null when status is 'none'. " +
        "image_quality: 'unusable' if the frame is black, blurred or covered. notes: at most one short sentence, or null. Never invent details.";

    public const string UserPrompt = "Analyse this camera frame.";

    /// <summary>The strict JSON schema sent as response_format (OpenAI Structured Outputs).</summary>
    public static JsonObject Schema()
    {
        JsonArray Enum(IEnumerable<string> values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
        JsonObject Str(IEnumerable<string> values) => new() { ["type"] = "string", ["enum"] = Enum(values) };
        var ppe = Str(PpeStatus.All);
        var box = new JsonObject
        {
            ["anyOf"] = new JsonArray(
                new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JsonArray("x", "y", "width", "height"),
                    ["properties"] = new JsonObject
                    {
                        ["x"] = new JsonObject { ["type"] = "number" }, ["y"] = new JsonObject { ["type"] = "number" },
                        ["width"] = new JsonObject { ["type"] = "number" }, ["height"] = new JsonObject { ["type"] = "number" },
                    },
                },
                new JsonObject { ["type"] = "null" }),
        };
        var incidentTypes = Enum(IncidentTypes.All);
        incidentTypes.Add(null);
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("person_presence", "person_count", "people", "scene_activity", "safety_incident", "image_quality", "notes"),
            ["properties"] = new JsonObject
            {
                ["person_presence"] = Str(PresenceStatus.All),
                ["person_count"] = new JsonObject { ["type"] = new JsonArray("integer", "null") },
                ["people"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["required"] = new JsonArray("activity", "helmet", "safety_vest", "box"),
                        ["properties"] = new JsonObject
                        {
                            ["activity"] = Str(Activities), ["helmet"] = ppe.DeepClone(), ["safety_vest"] = ppe.DeepClone(), ["box"] = box,
                        },
                    },
                },
                ["scene_activity"] = Str(Activities),
                ["safety_incident"] = new JsonObject
                {
                    ["type"] = "object", ["additionalProperties"] = false,
                    ["required"] = new JsonArray("status", "type", "description"),
                    ["properties"] = new JsonObject
                    {
                        ["status"] = Str(IncidentStatus.All),
                        ["type"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["enum"] = incidentTypes },
                        ["description"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                    },
                },
                ["image_quality"] = Str(ImageQualities.All),
                ["notes"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
            },
        };
    }

    // ─── Output validation ─────────────────────────────────────────────────

    /// <summary>
    /// Checks the model's answer against the contract. Wrong types or labels
    /// outside the enums reject the whole answer (<c>invalid_output</c>);
    /// small inconsistencies are corrected and listed in Adjustments.
    /// </summary>
    public static (VisionAnalysis? Analysis, string? Error) Validate(JsonObject? o)
    {
        if (o is null) return (null, "The answer is not a JSON object.");
        var adjustments = new List<string>();

        var presence = Enum(o, "person_presence", PresenceStatus.All);
        if (presence is null) return (null, "person_presence must be present, absent or unclear.");

        int? count = null;
        if (o["person_count"] is JsonValue cv)
        {
            if (Number(cv) is not { } d || d < 0 || d != Math.Floor(d)) return (null, "person_count must be a whole number or null.");
            if (d > MaxCount) return (null, $"person_count is more than {MaxCount}.");
            count = (int)d;
        }
        else if (o["person_count"] is not null) return (null, "person_count must be a whole number or null.");

        if (o["people"] is not JsonArray peopleArr) return (null, "people must be an array.");
        var people = new List<AiPerson>();
        foreach (var node in peopleArr)
        {
            if (node is not JsonObject p) return (null, "Every person must be an object.");
            var activity = Enum(p, "activity", Activities);
            var helmet = Enum(p, "helmet", PpeStatus.All);
            var vest = Enum(p, "safety_vest", PpeStatus.All);
            if (activity is null || helmet is null || vest is null) return (null, "A person has an activity, helmet or safety_vest value outside the allowed labels.");
            AiBox? box = null;
            if (p["box"] is JsonObject b)
            {
                box = Box(b);
                if (box is null) adjustments.Add("A box outside the image was dropped.");
            }
            else if (p["box"] is not null) return (null, "box must be an object or null.");
            if (people.Count < MaxPeople) people.Add(new AiPerson(activity, helmet, vest, box));
        }
        if (peopleArr.Count > MaxPeople) adjustments.Add($"Only the first {MaxPeople} people were kept.");

        var scene = Enum(o, "scene_activity", Activities);
        if (scene is null) return (null, "scene_activity is outside the allowed labels.");

        if (o["safety_incident"] is not JsonObject inc) return (null, "safety_incident must be an object.");
        var incStatus = Enum(inc, "status", IncidentStatus.All);
        if (incStatus is null) return (null, "safety_incident.status must be none, possible or unclear.");
        string? incType = null;
        if (inc["type"] is not null)
        {
            incType = Enum(inc, "type", IncidentTypes.All);
            if (incType is null) return (null, "safety_incident.type is outside the allowed labels.");
        }
        var incDesc = Text(inc["description"]);
        if (incStatus == IncidentStatus.None && (incType is not null || incDesc is not null))
        {
            incType = null; incDesc = null;
            adjustments.Add("An incident type or description given with status none was dropped.");
        }

        var quality = Enum(o, "image_quality", ImageQualities.All);
        if (quality is null) return (null, "image_quality must be good, poor or unusable.");
        var notes = Text(o["notes"]);

        // ── Consistency ──
        if (presence == PresenceStatus.Absent && (people.Count > 0 || count > 0))
        {
            presence = PresenceStatus.Unclear;
            adjustments.Add("The answer said nobody is visible but listed people: presence treated as unclear.");
        }
        // Unclear presence keeps any listed people as possible sightings; the temporal rules confirm nothing from them.
        if (presence == PresenceStatus.Absent) count = 0;
        if (presence == PresenceStatus.Present && count == 0)
        {
            count = null;
            adjustments.Add("A count of 0 with people present was cleared.");
        }
        if (count is { } n && n < people.Count)
        {
            count = people.Count;
            adjustments.Add("The count was raised to the number of listed people.");
        }
        return (new VisionAnalysis(presence, count, people, scene, new AiIncident(incStatus, incType, incDesc), quality, notes, adjustments), null);
    }

    private static string? Enum(JsonObject o, string name, IReadOnlyList<string> allowed)
    {
        if (o[name] is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        var hit = allowed.FirstOrDefault(a => string.Equals(a, s.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit;
    }

    /// <summary>A finite JSON number, whether parsed from text or built in memory.</summary>
    private static double? Number(JsonValue v) =>
        v.GetValueKind() == JsonValueKind.Number && double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d)
            ? d : null;

    private static string? Text(JsonNode? n)
    {
        if (n is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
        s = s.Trim();
        if (s.Length == 0) return null;
        return s.Length > MaxTextLength ? s[..MaxTextLength] : s;
    }

    /// <summary>Fractions are clamped into the frame; values clearly in pixels (above 1.5) or an empty area mean the box can't be trusted.</summary>
    private static AiBox? Box(JsonObject b)
    {
        double? N(string k) => b[k] is JsonValue v ? Number(v) : null;
        if (N("x") is not { } x || N("y") is not { } y || N("width") is not { } w || N("height") is not { } h) return null;
        if (x > 1.5 || y > 1.5 || w > 1.5 || h > 1.5 || x < -0.5 || y < -0.5) return null;
        var x0 = Math.Clamp(x, 0, 1); var y0 = Math.Clamp(y, 0, 1);
        var x1 = Math.Clamp(x + w, 0, 1); var y1 = Math.Clamp(y + h, 0, 1);
        if (x1 - x0 < 0.01 || y1 - y0 < 0.01) return null;
        return new AiBox(Math.Round(x0, 4), Math.Round(y0, 4), Math.Round(x1 - x0, 4), Math.Round(y1 - y0, 4));
    }

    // ─── Frame checks ──────────────────────────────────────────────────────

    public const int MinDimension = 64;
    public const int MaxDimension = 1920;

    /// <summary>
    /// A frame must be a baseline or progressive JPEG of 64–1920 px per side
    /// and at most <paramref name="maxBytes"/>. The size is read from the SOF
    /// header, so nothing is decoded on the server.
    /// </summary>
    public static (int Width, int Height, string? Code, string? Message) CheckJpeg(ReadOnlySpan<byte> data, string? contentType, int maxBytes)
    {
        var ct = contentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (ct is not ("image/jpeg" or "image/jpg"))
            return (0, 0, "frame_type_invalid", "Send the frame as a JPEG image (Content-Type: image/jpeg).");
        if (data.Length == 0) return (0, 0, "frame_empty", "The frame is empty.");
        if (data.Length > maxBytes)
            return (0, 0, "frame_too_large", $"The frame is {data.Length / 1024} KB; the limit is {maxBytes / 1024} KB. Scale it down or lower the JPEG quality.");
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8)
            return (0, 0, "frame_not_jpeg", "The frame is not a JPEG image.");
        var i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF) { i++; continue; }
            var marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            var len = (data[i + 2] << 8) | data[i + 3];
            if (len < 2) break;
            // SOF0–SOF15 except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                var h = (data[i + 5] << 8) | data[i + 6];
                var w = (data[i + 7] << 8) | data[i + 8];
                if (w < MinDimension || h < MinDimension || w > MaxDimension || h > MaxDimension)
                    return (w, h, "frame_size_invalid", $"The frame is {w}×{h}; each side must be {MinDimension}–{MaxDimension} px.");
                return (w, h, null, null);
            }
            if (marker == 0xDA) break;   // start of scan before any frame header
            i += 2 + len;
        }
        return (0, 0, "frame_not_jpeg", "The JPEG has no readable frame header.");
    }
}
