namespace BunonBrain.Domain.Entities;

/// <summary>
/// A message sent from a public website form: a demo or consultation
/// request, a contact or support message, a problem report, a feature
/// request, feedback or a partnership enquiry. Anyone can create one; only
/// platform staff (permission <c>inquiries.manage</c>) can read them.
///
/// No IP address or browser fingerprint is stored. The client IP is used only
/// in memory, by the rate limiter.
/// </summary>
public class Inquiry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>One of <see cref="InquiryKinds.All"/>.</summary>
    public string Kind { get; set; } = default!;

    public string Name { get; set; } = default!;
    public string? Email { get; set; }
    public string? Phone { get; set; }
    public string? Company { get; set; }
    public string? JobTitle { get; set; }
    public string? Subject { get; set; }
    public string Message { get; set; } = default!;

    /// <summary>Extra form-specific answers (e.g. number of sewing lines), as a JSON object of strings.</summary>
    public string DetailsJson { get; set; } = "{}";

    /// <summary>Website path the form was sent from, e.g. <c>/demo</c>.</summary>
    public string? SourcePath { get; set; }

    /// <summary><c>new</c>, <c>in_progress</c> or <c>closed</c>.</summary>
    public string Status { get; set; } = InquiryStatuses.New;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

public static class InquiryKinds
{
    public const string Demo = "demo";
    public const string Consultation = "consultation";
    public const string Contact = "contact";
    public const string Support = "support";
    public const string Problem = "problem";
    public const string Feature = "feature";
    public const string Feedback = "feedback";
    public const string Partnership = "partnership";

    public static readonly string[] All = { Demo, Consultation, Contact, Support, Problem, Feature, Feedback, Partnership };
}

public static class InquiryStatuses
{
    public const string New = "new";
    public const string InProgress = "in_progress";
    public const string Closed = "closed";

    public static readonly string[] All = { New, InProgress, Closed };
}
