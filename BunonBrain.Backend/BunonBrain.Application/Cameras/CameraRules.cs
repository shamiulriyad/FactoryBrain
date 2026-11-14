using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BunonBrain.Application.Cameras;

/// <summary>Camera types a camera can be registered as.</summary>
public static class CameraTypes
{
    public const string FixedCctv = "FixedCctv";
    public const string IpCamera = "IpCamera";
    public const string UsbCamera = "UsbCamera";
    public const string RtspCamera = "RtspCamera";
    public const string Webcam = "Webcam";
    /// <summary>A recorded / uploaded video standing in for a camera (prototype, demonstrations).</summary>
    public const string RecordedVideo = "RecordedVideo";

    public static readonly IReadOnlyList<string> All = new[] { FixedCctv, IpCamera, UsbCamera, RtspCamera, Webcam, RecordedVideo };

    public static string? Parse(string? value) => Lookup.Parse(All, value);
}

/// <summary>Administrative status of a camera.</summary>
public static class CameraStatuses
{
    public const string Active = "Active";
    public const string Inactive = "Inactive";
    public const string Maintenance = "Maintenance";

    public static readonly IReadOnlyList<string> All = new[] { Active, Inactive, Maintenance };

    public static string? Parse(string? value) => Lookup.Parse(All, value);
}

/// <summary>How a camera's video is reached.</summary>
public static class CameraSourceTypes
{
    /// <summary>Not configured yet.</summary>
    public const string None = "None";
    /// <summary>An RTSP / RTSPS stream URL (IP cameras, NVR channels). Not connected to by the server in this phase.</summary>
    public const string Rtsp = "Rtsp";
    /// <summary>A camera attached to the viewing computer, opened by the browser (webcam, USB camera).</summary>
    public const string LocalWebcam = "LocalWebcam";
    /// <summary>An uploaded video file (MP4 / WebM) used in place of a live camera.</summary>
    public const string RecordedVideo = "RecordedVideo";
    /// <summary>An HTTP(S) stream (MJPEG / HLS) of an IP camera, for future integration.</summary>
    public const string HttpStream = "HttpStream";

    public static readonly IReadOnlyList<string> All = new[] { None, Rtsp, LocalWebcam, RecordedVideo, HttpStream };

    public static string? Parse(string? value) => Lookup.Parse(All, value);

    /// <summary>Sources that carry a URL (and may carry credentials).</summary>
    public static bool HasUrl(string type) => type is Rtsp or HttpStream;
}

/// <summary>What a camera zone marks.</summary>
public static class ZoneTypes
{
    public const string Workstation = "Workstation";
    public const string ProductionLine = "ProductionLine";
    public const string Walkway = "Walkway";
    public const string Restricted = "Restricted";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = new[] { Workstation, ProductionLine, Walkway, Restricted, Other };

    public static string? Parse(string? value) => Lookup.Parse(All, value);
}

public static class CoverageKinds
{
    public const string Line = "Line";
    public const string Workstation = "Workstation";
}

public static class ReferenceFrameSources
{
    public const string Upload = "Upload";
    public const string VideoFrame = "VideoFrame";

    public static string? Parse(string? value) => Lookup.Parse(new[] { Upload, VideoFrame }, value);
}

internal static class Lookup
{
    public static string? Parse(IReadOnlyList<string> all, string? value) =>
        all.FirstOrDefault(s => string.Equals(s, value?.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Limits and formats for cameras.</summary>
public static partial class CameraRules
{
    public const int CodeMax = 24;
    public const int NameMax = 120;
    public const int LocationMax = 200;
    public const int DescriptionMax = 1000;
    public const int ReasonMax = 500;
    public const int MaxCoverageLines = 20;
    public const int MaxCoverageWorkstations = 200;
    public const int MaxZonesPerCamera = 100;
    public const int ZoneNameMax = 80;
    public const int UrlMax = 500;
    public const int CredentialMax = 128;
    public const int WebcamLabelMax = 120;
    public const int NotesMax = 500;
    public const int FileNameMax = 200;

    /// <summary>Associated data for camera credentials; the source id is appended so a ciphertext can't be moved to another row.</summary>
    public const string CredentialsPurpose = "bunonbrain/camera_sources/v1/credentials";

    public static string CredentialsPurposeFor(Guid sourceId) => CredentialsPurpose + ":" + sourceId.ToString("N");

    [GeneratedRegex(@"^[A-Z0-9][A-Z0-9\-_/.]{0,23}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex ColorPattern();

    /// <summary>Camera codes: letters, digits, '-', '_', '/', '.'; stored in upper case (e.g. CAM-001).</summary>
    public static bool IsCode(string? value) => value is not null && CodePattern().IsMatch(value.Trim().ToUpperInvariant());

    public static string NormaliseCode(string value) => value.Trim().ToUpperInvariant();

    public static bool IsColor(string? value) => value is null || ColorPattern().IsMatch(value);

    /// <summary>The next free code in the CAM-001 series, given the codes a factory already has.</summary>
    public static string NextCode(IEnumerable<string> existing)
    {
        var max = 0;
        foreach (var code in existing)
        {
            if (!code.StartsWith("CAM-", StringComparison.Ordinal)) continue;
            if (int.TryParse(code.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > max) max = n;
        }
        return $"CAM-{max + 1:000}";
    }

    /// <summary>Which source types fit a camera type (the UI offers these; the API accepts any valid one).</summary>
    public static IReadOnlyList<string> SuggestedSources(string cameraType) => cameraType switch
    {
        CameraTypes.RecordedVideo => new[] { CameraSourceTypes.RecordedVideo },
        CameraTypes.Webcam or CameraTypes.UsbCamera => new[] { CameraSourceTypes.LocalWebcam, CameraSourceTypes.RecordedVideo },
        CameraTypes.RtspCamera => new[] { CameraSourceTypes.Rtsp, CameraSourceTypes.RecordedVideo },
        _ => new[] { CameraSourceTypes.Rtsp, CameraSourceTypes.HttpStream, CameraSourceTypes.RecordedVideo },
    };
}

/// <summary>A stream URL split into what may be shown and what must be encrypted.</summary>
/// <param name="Url">The URL without user name / password.</param>
public sealed record ParsedStreamUrl(string Url, string Host, string? UserName, string? Password);

/// <summary>
/// Validation of user-supplied stream URLs. The server never connects to
/// these in this phase; validation keeps obviously unsafe targets out
/// (loopback, link-local / cloud metadata, unspecified and multicast
/// addresses) so a later integration can't be pointed at the server itself,
/// and keeps secrets out of the stored URL: credentials in the user-info part
/// are split off for encryption, and credential-looking query parameters
/// are refused.
/// </summary>
public static class StreamUrls
{
    public const string ErrInvalid = "stream_url_invalid";
    public const string ErrScheme = "stream_url_scheme";
    public const string ErrHost = "stream_url_host_not_allowed";
    public const string ErrSecretInQuery = "stream_url_secret_in_query";

    private static readonly string[] SecretQueryNames = { "password", "passwd", "pwd", "pass", "token", "key", "apikey", "api_key", "secret", "auth", "credentials" };

    public static IReadOnlyList<string> SchemesFor(string sourceType) => sourceType switch
    {
        CameraSourceTypes.Rtsp => new[] { "rtsp", "rtsps" },
        CameraSourceTypes.HttpStream => new[] { "http", "https" },
        _ => Array.Empty<string>(),
    };

    public static bool TryParse(string? raw, string sourceType, out ParsedStreamUrl? parsed, out string? error, out string? message)
    {
        parsed = null;
        error = ErrInvalid;
        message = "Enter a full stream URL, e.g. rtsp://10.0.4.21:554/stream1.";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        if (text.Length > CameraRules.UrlMax || text.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return false;

        var schemes = SchemesFor(sourceType);
        if (!schemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
        {
            error = ErrScheme;
            message = $"A {sourceType} source needs a {string.Join(" or ", schemes.Select(s => s + "://"))} URL.";
            return false;
        }
        if (string.IsNullOrEmpty(uri.Host)) return false;
        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            message = "The stream URL can't contain a '#' fragment.";
            return false;
        }
        if (!IsAllowedHost(uri))
        {
            error = ErrHost;
            message = "That address can't be used for a camera (loopback, link-local, multicast and unspecified addresses are refused).";
            return false;
        }
        if (HasSecretQuery(uri.Query))
        {
            error = ErrSecretInQuery;
            message = "Don't put a password or token in the URL. Enter it in the password field; it is stored encrypted.";
            return false;
        }

        string? user = null, password = null;
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            var info = uri.UserInfo;
            var colon = info.IndexOf(':');
            user = Uri.UnescapeDataString(colon < 0 ? info : info[..colon]);
            password = colon < 0 ? null : Uri.UnescapeDataString(info[(colon + 1)..]);
            if (string.IsNullOrEmpty(user)) user = null;
            if (string.IsNullOrEmpty(password)) password = null;
        }
        var builder = new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty };
        var clean = builder.Uri.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.UserInfo, UriFormat.UriEscaped);
        parsed = new ParsedStreamUrl(clean, uri.IdnHost.ToLowerInvariant(), user, password);
        error = null;
        message = null;
        return true;
    }

    private static bool HasSecretQuery(string query)
    {
        if (string.IsNullOrEmpty(query)) return false;
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = Uri.UnescapeDataString(pair.Split('=', 2)[0]).Trim().ToLowerInvariant();
            if (SecretQueryNames.Contains(name)) return true;
        }
        return false;
    }

    private static bool IsAllowedHost(Uri uri)
    {
        var host = uri.IdnHost.Trim('[', ']').ToLowerInvariant();
        if (host.Length is 0 or > 253) return false;
        if (host == "localhost" || host.EndsWith(".localhost", StringComparison.Ordinal)) return false;
        if (host is "metadata.google.internal" or "metadata") return false;
        if (!IPAddress.TryParse(host, out var ip)) return true;
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.Equals(IPAddress.IPv6None)) return false;
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] == 0) return false;                       // 0.0.0.0/8
            if (b[0] == 169 && b[1] == 254) return false;      // link-local, cloud metadata
            if (b[0] >= 224) return false;                     // multicast and reserved
            return true;
        }
        return !(ip.IsIPv6LinkLocal || ip.IsIPv6Multicast || ip.IsIPv6SiteLocal);
    }

    /// <summary>The credentials as the JSON stored (encrypted) in <c>CredentialsCiphertext</c>.</summary>
    public static string CredentialsJson(string? userName, string? password) =>
        JsonSerializer.Serialize(new Dictionary<string, string?> { ["u"] = userName, ["p"] = password });
}

/// <summary>Identifies an uploaded video by its bytes (never by its name or declared type).</summary>
public sealed record VideoFileInfo(string ContentType, string Extension);

public static class VideoFileInspector
{
    public const string ErrEmpty = "video_empty";
    public const string ErrTooLarge = "video_too_large";
    public const string ErrUnsupported = "video_unsupported_format";

    public static readonly IReadOnlyList<string> AcceptedContentTypes = new[] { "video/mp4", "video/webm" };

    /// <summary>MP4 (ISO base media, "ftyp" box first) or WebM / Matroska (EBML header).</summary>
    public static bool TryInspect(ReadOnlySpan<byte> header, out VideoFileInfo? info, out string? error)
    {
        info = null;
        if (header.Length == 0) { error = ErrEmpty; return false; }
        if (header.Length >= 12 && header.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(header.Slice(8, 4));
            // QuickTime ("qt  ") plays poorly in browsers; everything else in the MP4 family is accepted.
            if (brand == "qt  ") { error = ErrUnsupported; return false; }
            info = new VideoFileInfo("video/mp4", "mp4");
            error = null;
            return true;
        }
        if (header.Length >= 4 && header[0] == 0x1A && header[1] == 0x45 && header[2] == 0xDF && header[3] == 0xA3)
        {
            info = new VideoFileInfo("video/webm", "webm");
            error = null;
            return true;
        }
        error = ErrUnsupported;
        return false;
    }

    public static string Describe(string error) => error switch
    {
        ErrEmpty => "The video file is empty.",
        ErrTooLarge => "The video file is too large.",
        _ => "Upload an MP4 (H.264) or WebM video.",
    };
}

/// <summary>A point of a zone polygon, normalised to the reference frame (0–1, origin top-left).</summary>
public sealed record ZonePoint(double X, double Y);

/// <summary>
/// Zone polygon validation and storage format. A valid zone has 3–64
/// points, every coordinate in [0, 1], no repeated neighbouring points, a
/// non-trivial area and no crossing edges (a simple polygon), so the vision
/// service can rely on point-in-polygon tests.
/// </summary>
public static class ZoneGeometry
{
    public const int MinPoints = 3;
    public const int MaxPoints = 64;
    /// <summary>Smallest accepted area, as a fraction of the frame (0.01 %).</summary>
    public const double MinArea = 0.0001;
    private const int Decimals = 6;

    public const string ErrCount = "zone_point_count";
    public const string ErrRange = "zone_point_out_of_range";
    public const string ErrDuplicate = "zone_duplicate_point";
    public const string ErrArea = "zone_too_small";
    public const string ErrSelfIntersecting = "zone_self_intersecting";

    /// <summary>
    /// Validate and normalise (round to 6 decimals, drop a closing point equal
    /// to the first). Returns the cleaned points, or null with an error code and message.
    /// </summary>
    public static IReadOnlyList<ZonePoint>? Normalise(IReadOnlyList<ZonePoint>? points, out string? error, out string? message)
    {
        error = ErrCount;
        message = $"A zone needs between {MinPoints} and {MaxPoints} points.";
        if (points is null || points.Count < MinPoints) return null;
        var list = new List<ZonePoint>(points.Count);
        foreach (var p in points)
        {
            if (p is null || !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1)
            {
                error = ErrRange;
                message = "Every point must have x and y between 0 and 1 (a fraction of the frame's width and height).";
                return null;
            }
            list.Add(new ZonePoint(Math.Round(p.X, Decimals), Math.Round(p.Y, Decimals)));
        }
        if (list.Count > MinPoints && list[0] == list[^1]) list.RemoveAt(list.Count - 1);
        if (list.Count < MinPoints || list.Count > MaxPoints) return null;

        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == list[(i + 1) % list.Count])
            {
                error = ErrDuplicate;
                message = $"Point {i + 1} repeats the next point.";
                return null;
            }
        }
        // Crossing first: a bow tie's two halves cancel out to zero signed area.
        if (SelfIntersects(list))
        {
            error = ErrSelfIntersecting;
            message = "The zone's edges cross each other. Draw the points in order around the area.";
            return null;
        }
        if (Math.Abs(SignedArea(list)) < MinArea)
        {
            error = ErrArea;
            message = "The zone is too small or its points are in a straight line.";
            return null;
        }
        error = null;
        message = null;
        return list;
    }

    /// <summary>Shoelace formula (positive when the points run counter-clockwise in a y-up frame).</summary>
    public static double SignedArea(IReadOnlyList<ZonePoint> pts)
    {
        double sum = 0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            sum += a.X * b.Y - b.X * a.Y;
        }
        return sum / 2;
    }

    public static bool SelfIntersects(IReadOnlyList<ZonePoint> pts)
    {
        var n = pts.Count;
        for (var i = 0; i < n; i++)
        {
            var a1 = pts[i];
            var a2 = pts[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                // Neighbouring edges share a point; that's not a crossing.
                if (j == i || (j + 1) % n == i || (i + 1) % n == j) continue;
                if (SegmentsIntersect(a1, a2, pts[j], pts[(j + 1) % n])) return true;
            }
        }
        return false;
    }

    private static bool SegmentsIntersect(ZonePoint p1, ZonePoint p2, ZonePoint q1, ZonePoint q2)
    {
        double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2), d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
        if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
        return (d1 == 0 && OnSegment(q1, q2, p1)) || (d2 == 0 && OnSegment(q1, q2, p2))
            || (d3 == 0 && OnSegment(p1, p2, q1)) || (d4 == 0 && OnSegment(p1, p2, q2));
    }

    private static double Cross(ZonePoint a, ZonePoint b, ZonePoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    private static bool OnSegment(ZonePoint a, ZonePoint b, ZonePoint p) =>
        Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static string Serialize(IReadOnlyList<ZonePoint> pts) => JsonSerializer.Serialize(pts, Json);

    public static IReadOnlyList<ZonePoint> Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<ZonePoint>();
        try { return JsonSerializer.Deserialize<List<ZonePoint>>(json, Json) ?? new List<ZonePoint>(); }
        catch (JsonException) { return Array.Empty<ZonePoint>(); }
    }
}
