using System.Text;

namespace BunonBrain.Application.Workers;

/// <summary>
/// Minimal RFC 4180 reader and writer for the worker import / export.
/// Reading: quoted fields, doubled quotes, line breaks inside quotes, CRLF or
/// LF, a UTF-8 byte-order mark, and a comma, semicolon or tab delimiter
/// (picked from the header line, because spreadsheets in some locales export
/// semicolons). Writing: every field that needs it is quoted, and a value
/// that a spreadsheet would run as a formula is prefixed with an apostrophe.
/// </summary>
public static class Csv
{
    public static char DetectDelimiter(string text)
    {
        var end = text.IndexOfAny(new[] { '\r', '\n' });
        var header = end < 0 ? text : text[..end];
        var best = ',';
        var bestCount = header.Count(c => c == ',');
        foreach (var candidate in new[] { ';', '\t' })
        {
            var n = header.Count(c => c == candidate);
            if (n > bestCount) { best = candidate; bestCount = n; }
        }
        return best;
    }

    /// <summary>
    /// Rows of fields. Completely empty lines are skipped. Throws
    /// <see cref="FormatException"/> for an unterminated quoted field or more
    /// than <paramref name="maxRows"/> rows.
    /// </summary>
    public static List<(int Line, string[] Fields)> Read(string text, char delimiter, int maxRows)
    {
        if (text.Length > 0 && text[0] == '﻿') text = text[1..];
        var rows = new List<(int, string[])>();
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var fieldWasQuoted = false;
        var line = 1;
        var rowLine = 1;

        void EndRow()
        {
            fields.Add(field.ToString());
            field.Clear();
            var blank = fields.Count == 1 && fields[0].Length == 0 && !fieldWasQuoted;
            if (!blank && !fields.All(string.IsNullOrWhiteSpace))
            {
                if (rows.Count >= maxRows) throw new FormatException($"The file has more than {maxRows - 1} data rows.");
                rows.Add((rowLine, fields.ToArray()));
            }
            fields.Clear();
            fieldWasQuoted = false;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else inQuotes = false;
                }
                else
                {
                    if (c == '\n') line++;
                    field.Append(c);
                }
                continue;
            }
            if (c == '"' && field.Length == 0) { inQuotes = true; fieldWasQuoted = true; }
            else if (c == delimiter) { fields.Add(field.ToString()); field.Clear(); }
            else if (c == '\r') { /* handled by the following \n, or ignored */ if (i + 1 >= text.Length || text[i + 1] != '\n') { EndRow(); line++; rowLine = line; } }
            else if (c == '\n') { EndRow(); line++; rowLine = line; }
            else field.Append(c);
        }
        if (inQuotes) throw new FormatException($"A quoted value that starts on line {rowLine} is never closed.");
        if (field.Length > 0 || fields.Count > 0) EndRow();
        return rows;
    }

    /// <summary>One CSV line (without the line break).</summary>
    public static string Line(IEnumerable<string?> values) => string.Join(",", values.Select(Escape));

    public static string Escape(string? value)
    {
        var v = value ?? "";
        // A cell starting with = + - @ (or a tab / CR) is evaluated as a formula by spreadsheets.
        if (v.Length > 0 && (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')) v = "'" + v;
        return v.IndexOfAny(new[] { ',', '"', '\n', '\r', ';' }) >= 0 ? "\"" + v.Replace("\"", "\"\"") + "\"" : v;
    }
}

/// <summary>One problem with one imported row.</summary>
/// <param name="Field">The column the problem is in (camelCase API name), or <c>row</c>.</param>
public sealed record ImportIssue(string Field, string Message);

/// <summary>One data row of an import file after field-level validation (no database lookups yet).</summary>
public sealed class ImportedWorkerRow
{
    public int RowNumber { get; init; }
    public string? FullName { get; set; }
    public string? EmployeeCode { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Gender { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public DateOnly? JoinedOn { get; set; }
    public string? Department { get; set; }
    public string? Designation { get; set; }
    public string EmploymentType { get; set; } = EmploymentTypes.Permanent;
    public string? Floor { get; set; }
    public string? Line { get; set; }
    public string? Grade { get; set; }
    public string? PresentAddress { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? EmergencyContactRelation { get; set; }
    public List<ImportIssue> Errors { get; } = new();
    public List<ImportIssue> Warnings { get; } = new();
    public bool IsValid => Errors.Count == 0;
}

/// <summary>What the parser made of an import file.</summary>
/// <param name="FileError">Set when the file as a whole can't be used (then <paramref name="Rows"/> is empty).</param>
public sealed record WorkerImportParse(
    string? FileError,
    IReadOnlyList<string> RecognisedColumns,
    IReadOnlyList<string> IgnoredColumns,
    IReadOnlyList<ImportedWorkerRow> Rows);

/// <summary>
/// Turns worker import CSV text into rows and checks every field that can be
/// checked without the database (required values, formats, lengths, dates,
/// known employment type / gender, duplicate employee numbers inside the
/// file). The service then resolves department, floor and line names and
/// checks employee numbers against existing workers.
/// </summary>
public static class WorkerImportParser
{
    public const int MaxRows = 2000;
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Column keys in the order of the downloadable template.</summary>
    public static readonly IReadOnlyList<string> TemplateColumns = new[]
    {
        "full_name", "phone", "joining_date", "department", "designation", "employment_type", "employee_number",
        "gender", "date_of_birth", "email", "present_address", "floor", "line", "grade",
        "emergency_contact_name", "emergency_contact_phone", "emergency_contact_relation",
    };

    public static readonly IReadOnlyList<string> RequiredColumns = new[] { "full_name", "phone", "joining_date", "department", "designation" };

    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["fullname"] = "full_name", ["name"] = "full_name", ["workername"] = "full_name",
        ["phone"] = "phone", ["phonenumber"] = "phone", ["mobile"] = "phone", ["mobilenumber"] = "phone",
        ["joiningdate"] = "joining_date", ["joindate"] = "joining_date", ["dateofjoining"] = "joining_date", ["joinedon"] = "joining_date",
        ["department"] = "department", ["dept"] = "department",
        ["designation"] = "designation", ["jobtitle"] = "designation", ["title"] = "designation",
        ["employmenttype"] = "employment_type", ["type"] = "employment_type",
        ["employeenumber"] = "employee_number", ["employeeno"] = "employee_number", ["employeecode"] = "employee_number",
        ["staffnumber"] = "employee_number", ["staffno"] = "employee_number", ["employeeid"] = "employee_number",
        ["gender"] = "gender", ["sex"] = "gender",
        ["dateofbirth"] = "date_of_birth", ["dob"] = "date_of_birth", ["birthdate"] = "date_of_birth",
        ["email"] = "email", ["emailaddress"] = "email",
        ["presentaddress"] = "present_address", ["address"] = "present_address",
        ["floor"] = "floor", ["productionfloor"] = "floor",
        ["line"] = "line", ["productionline"] = "line", ["lineid"] = "line",
        ["grade"] = "grade",
        ["emergencycontactname"] = "emergency_contact_name", ["emergencycontact"] = "emergency_contact_name",
        ["emergencycontactphone"] = "emergency_contact_phone", ["emergencyphone"] = "emergency_contact_phone",
        ["emergencycontactrelation"] = "emergency_contact_relation", ["emergencyrelation"] = "emergency_contact_relation", ["relation"] = "emergency_contact_relation",
    };

    private static string Key(string header) =>
        new string(header.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The header line plus one example row, for the "download template" button.</summary>
    public static string Template() =>
        Csv.Line(TemplateColumns) + "\r\n" +
        Csv.Line(new[]
        {
            "Example Name", "+8801700000000", "2025-01-15", "Sewing", "Sewing Operator", "Permanent", "EMP-1001",
            "Female", "1998-04-20", "", "House 1, Road 2, Gazipur", "", "", "G3", "Contact Name", "+8801800000000", "Brother",
        }) + "\r\n";

    public static WorkerImportParse Parse(string text, DateOnly today)
    {
        static WorkerImportParse Fail(string error) =>
            new(error, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<ImportedWorkerRow>());

        if (string.IsNullOrWhiteSpace(text)) return Fail("The file is empty.");
        if (text.Contains('\0')) return Fail("This is not a text CSV file. Save the sheet as CSV (UTF-8) and try again.");

        List<(int Line, string[] Fields)> raw;
        try { raw = Csv.Read(text, Csv.DetectDelimiter(text), MaxRows + 1); }
        catch (FormatException ex) { return Fail(ex.Message); }
        if (raw.Count == 0) return Fail("The file is empty.");

        var header = raw[0].Fields;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var ignored = new List<string>();
        for (var i = 0; i < header.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(header[i])) continue;
            if (!Aliases.TryGetValue(Key(header[i]), out var column)) { ignored.Add(header[i].Trim()); continue; }
            if (map.ContainsKey(column)) return Fail($"The column \"{header[i].Trim()}\" appears more than once.");
            map[column] = i;
        }
        var missing = RequiredColumns.Where(c => !map.ContainsKey(c)).ToList();
        if (missing.Count > 0)
            return Fail($"Missing required column{(missing.Count == 1 ? "" : "s")}: {string.Join(", ", missing)}. Download the template to see the expected header.");
        if (raw.Count == 1) return Fail("The file has a header but no worker rows.");

        var rows = new List<ImportedWorkerRow>(raw.Count - 1);
        var seenCodes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (line, fields) in raw.Skip(1))
        {
            string? Cell(string column)
            {
                if (!map.TryGetValue(column, out var i) || i >= fields.Length) return null;
                var v = fields[i].Trim();
                // Undo the apostrophe Csv.Escape puts before = + - @, so an exported file can be imported again.
                if (v.Length > 1 && v[0] == '\'' && v[1] is '=' or '+' or '-' or '@') v = v[1..];
                return v.Length == 0 ? null : v;
            }

            var row = new ImportedWorkerRow { RowNumber = line };
            void Error(string field, string message) => row.Errors.Add(new ImportIssue(field, message));

            row.FullName = Cell("full_name");
            if (row.FullName is null) Error("fullName", "Full name is required.");
            else if (row.FullName.Length > WorkerRules.NameMax) Error("fullName", $"Full name is longer than {WorkerRules.NameMax} characters.");

            row.Phone = Cell("phone");
            if (row.Phone is null) Error("phone", "Phone number is required.");
            else if (!WorkerRules.IsPhone(row.Phone)) Error("phone", "Phone may contain digits, spaces, +, - and parentheses (at least 6 digits).");

            var joined = Cell("joining_date");
            if (joined is null) Error("joinedOn", "Joining date is required.");
            else if (!WorkerRules.TryParseDate(joined, out var j)) Error("joinedOn", $"Joining date \"{joined}\" is not a date. Use YYYY-MM-DD or DD/MM/YYYY.");
            else if (WorkerRules.JoiningDateProblem(j, today) is { } problem) Error("joinedOn", problem);
            else row.JoinedOn = j;

            row.Department = Cell("department");
            if (row.Department is null) Error("department", "Department is required.");

            row.Designation = Cell("designation");
            if (row.Designation is null) Error("designation", "Designation is required.");
            else if (row.Designation.Length > WorkerRules.DesignationMax) Error("designation", $"Designation is longer than {WorkerRules.DesignationMax} characters.");

            var type = Cell("employment_type");
            if (type is not null)
            {
                if (EmploymentTypes.Parse(type) is { } parsed) row.EmploymentType = parsed;
                else Error("employmentType", $"Employment type \"{type}\" is not one of: {string.Join(", ", EmploymentTypes.All)}.");
            }

            var code = WorkerRules.NormaliseEmployeeCode(Cell("employee_number"));
            if (code is not null)
            {
                if (!WorkerRules.IsEmployeeCode(code)) Error("employeeCode", "Employee number: letters, digits, '-', '_' and '/', up to 32 characters.");
                else if (seenCodes.TryGetValue(code, out var first)) Error("employeeCode", $"Employee number {code} is also used on line {first} of this file.");
                else { seenCodes[code] = line; row.EmployeeCode = code; }
            }

            var gender = Cell("gender");
            if (gender is not null)
            {
                if (Genders.Parse(gender) is { } parsed) row.Gender = parsed;
                else Error("gender", $"Gender \"{gender}\" is not one of: {string.Join(", ", Genders.All)} (or leave it empty).");
            }

            var born = Cell("date_of_birth");
            if (born is not null)
            {
                if (!WorkerRules.TryParseDate(born, out var b)) Error("dateOfBirth", $"Date of birth \"{born}\" is not a date. Use YYYY-MM-DD or DD/MM/YYYY.");
                else if (WorkerRules.DateOfBirthProblem(b, today, row.JoinedOn) is { } problem) Error("dateOfBirth", problem);
                else row.DateOfBirth = b;
            }

            row.Email = Cell("email")?.ToLowerInvariant();
            if (row.Email is not null && !WorkerRules.IsEmail(row.Email)) Error("email", "Email is not a valid address.");

            row.PresentAddress = Cell("present_address");
            if (row.PresentAddress?.Length > WorkerRules.AddressMax) Error("presentAddress", $"Address is longer than {WorkerRules.AddressMax} characters.");

            row.Floor = Cell("floor");
            row.Line = Cell("line");

            row.Grade = Cell("grade");
            if (row.Grade?.Length > WorkerRules.GradeMax) Error("grade", $"Grade is longer than {WorkerRules.GradeMax} characters.");

            row.EmergencyContactName = Cell("emergency_contact_name");
            if (row.EmergencyContactName?.Length > WorkerRules.NameMax) Error("emergencyContactName", $"Emergency contact name is longer than {WorkerRules.NameMax} characters.");
            row.EmergencyContactPhone = Cell("emergency_contact_phone");
            if (row.EmergencyContactPhone is not null && !WorkerRules.IsPhone(row.EmergencyContactPhone))
                Error("emergencyContactPhone", "Emergency contact phone may contain digits, spaces, +, - and parentheses (at least 6 digits).");
            row.EmergencyContactRelation = Cell("emergency_contact_relation");
            if (row.EmergencyContactRelation?.Length > WorkerRules.RelationMax) Error("emergencyContactRelation", $"Relation is longer than {WorkerRules.RelationMax} characters.");

            rows.Add(row);
        }

        var recognised = TemplateColumns.Where(map.ContainsKey).ToList();
        return new WorkerImportParse(null, recognised, ignored, rows);
    }
}
