using System.Security.Cryptography;
using System.Text;

namespace FactoryBrain.Application.Merchandising;

/// <summary>One value of one field from one source: a current document version, or the order record (when a person typed it).</summary>
public sealed record ConflictSource(
    string FieldKey,
    /// <summary>Normalised value.</summary>
    string Value,
    /// <summary><c>document</c> or <c>order</c>.</summary>
    string Kind,
    Guid? DocumentId = null,
    string? DocumentTitle = null,
    string? DocumentType = null,
    Guid? VersionId = null,
    int? VersionNumber = null,
    string? VersionStatus = null,
    Guid? FieldId = null,
    int? Page = null,
    string? Snippet = null);

public sealed record DetectedConflict(string FieldKey, IReadOnlyList<ConflictSource> Values, string Fingerprint);

/// <summary>
/// Contradictions by explicit rules, never by a model's opinion: for every
/// compared field (<see cref="MerchFieldDefinition.Compared"/>), the values
/// of the order's current document versions — plus the order record's value
/// when a person typed it — are reduced to their comparison key
/// (<see cref="MerchFields.ComparisonKey"/>); two or more different keys are
/// a conflict. The fingerprint identifies the exact set of sources and
/// values, so a resolved conflict is not raised again for the same evidence.
/// </summary>
public static class MerchConflictDetector
{
    public static IReadOnlyList<DetectedConflict> Detect(IEnumerable<ConflictSource> sources)
    {
        var result = new List<DetectedConflict>();
        foreach (var group in sources.Where(s => MerchFields.IsKnown(s.FieldKey) && MerchFields.Get(s.FieldKey).Compared).GroupBy(s => s.FieldKey))
        {
            var keyed = group.Select(s => (Source: s, Key: MerchFields.ComparisonKey(s.FieldKey, s.Value))).ToList();
            if (keyed.Select(k => k.Key).Distinct(StringComparer.Ordinal).Count() < 2) continue;
            var ordered = keyed.OrderBy(k => k.Source.Kind == "order" ? 1 : 0).ThenBy(k => k.Source.DocumentTitle).ThenBy(k => k.Source.VersionNumber).ToList();
            result.Add(new DetectedConflict(group.Key, ordered.Select(k => k.Source).ToList(), Fingerprint(group.Key, keyed)));
        }
        return result.OrderBy(c => c.FieldKey, StringComparer.Ordinal).ToList();
    }

    private static string Fingerprint(string field, IEnumerable<(ConflictSource Source, string Key)> values)
    {
        var parts = values.Select(v => $"{v.Source.Kind}|{v.Source.VersionId}|{v.Key}").OrderBy(x => x, StringComparer.Ordinal);
        var text = field + "\n" + string.Join("\n", parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
