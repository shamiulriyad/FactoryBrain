using BunonBrain.Application.Access;
using BunonBrain.Application.Dtos.Workers;
using BunonBrain.Application.Validation;
using BunonBrain.Application.Workers;
using P = BunonBrain.Application.Access.Permissions;
using R = BunonBrain.Application.Access.SystemRoles;

namespace BunonBrain.Api.Tests;

/// <summary>
/// Worker management rules that need no database: Worker ID format, the
/// status transition table, request validation, CSV reading / writing, import
/// row validation and which role may see or change what.
/// </summary>
public sealed class WorkerUnitTests
{
    private static readonly DateOnly Today = new(2026, 11, 13);

    // ─── Worker ID ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1, "W-001")]
    [InlineData(42, "W-042")]
    [InlineData(999, "W-999")]
    [InlineData(1000, "W-1000")]
    [InlineData(123456, "W-123456")]
    public void Worker_ids_have_at_least_three_digits_and_simply_grow(long number, string expected)
    {
        Assert.Equal(expected, WorkerRules.FormatWorkerCode(number));
        Assert.True(WorkerRules.LooksLikeWorkerCode(expected));
        Assert.True(WorkerRules.LooksLikeWorkerCode(expected.ToLowerInvariant()));
    }

    [Fact]
    public void Worker_ids_are_distinct_for_distinct_numbers_and_never_made_from_zero_or_less()
    {
        var codes = Enumerable.Range(1, 5000).Select(n => WorkerRules.FormatWorkerCode(n)).ToList();
        Assert.Equal(codes.Count, codes.Distinct().Count());
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerRules.FormatWorkerCode(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkerRules.FormatWorkerCode(-3));
    }

    [Theory]
    [InlineData("W-1")]
    [InlineData("W001")]
    [InlineData("X-001")]
    [InlineData("W-00A")]
    [InlineData("")]
    [InlineData("5b1f0c9e-0000-4000-8000-000000000001")]
    public void Things_that_are_not_worker_ids_are_not_mistaken_for_one(string value) =>
        Assert.False(WorkerRules.LooksLikeWorkerCode(value));

    // ─── Status transitions ────────────────────────────────────────────────

    public static IEnumerable<object[]> AllPairs() =>
        from a in WorkerStatuses.All from b in WorkerStatuses.All select new object[] { a, b };

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Only_the_documented_status_changes_are_allowed(string from, string to)
    {
        var allowed = new HashSet<(string, string)>
        {
            ("Active", "Inactive"), ("Active", "Suspended"), ("Active", "Resigned"), ("Active", "Terminated"),
            ("Inactive", "Active"), ("Inactive", "Resigned"), ("Inactive", "Terminated"),
            ("Suspended", "Active"), ("Suspended", "Resigned"), ("Suspended", "Terminated"),
            ("Resigned", "Active"), ("Terminated", "Active"),
        };
        var check = WorkerStatusTransitions.Check(from, to, "a documented reason");
        Assert.Equal(allowed.Contains((from, to)), check.Allowed);
        if (from == to) Assert.Equal("status_unchanged", check.Code);
        else if (!check.Allowed) Assert.Equal("status_transition_not_allowed", check.Code);
    }

    [Theory]
    [InlineData("Active", "Suspended", true)]
    [InlineData("Active", "Resigned", true)]
    [InlineData("Active", "Terminated", true)]
    [InlineData("Resigned", "Active", true)]
    [InlineData("Terminated", "Active", true)]
    [InlineData("Active", "Inactive", false)]
    [InlineData("Inactive", "Active", false)]
    [InlineData("Suspended", "Active", false)]
    public void A_reason_is_required_to_suspend_separate_or_bring_someone_back(string from, string to, bool required)
    {
        Assert.Equal(required, WorkerStatusTransitions.ReasonRequired(from, to));
        var withoutReason = WorkerStatusTransitions.Check(from, to, "  ");
        Assert.Equal(!required, withoutReason.Allowed);
        if (required) Assert.Equal("status_reason_required", withoutReason.Code);
        Assert.True(WorkerStatusTransitions.Check(from, to, "because").Allowed);
    }

    [Fact]
    public void Unknown_statuses_are_refused_and_spelling_is_normalised()
    {
        Assert.Equal("unknown_status", WorkerStatusTransitions.Check("Active", "Deleted", "x").Code);
        Assert.Equal("Suspended", WorkerStatuses.Parse(" suspended "));
        Assert.Null(WorkerStatuses.Parse("gone"));
        Assert.True(WorkerStatuses.IsSeparated("Resigned") && WorkerStatuses.IsSeparated("Terminated"));
        Assert.False(WorkerStatuses.IsSeparated("Suspended"));
        Assert.Empty(WorkerStatusTransitions.AllowedFrom("Deleted"));
    }

    // ─── Request validation ────────────────────────────────────────────────

    private static CreateWorkerRequest ValidRequest() => new(
        Guid.NewGuid(), "Rahima Begum", "+880 1711-000001", new DateOnly(2024, 1, 15), Guid.NewGuid(), "Sewing Operator");

    [Fact]
    public void A_complete_registration_is_valid() =>
        Assert.True(new CreateWorkerRequestValidator().Validate(ValidRequest()).IsValid);

    [Fact]
    public void Registration_requires_name_phone_joining_date_department_and_designation()
    {
        var result = new CreateWorkerRequestValidator().Validate(new CreateWorkerRequest(Guid.Empty, "", null, null, null, null));
        var fields = result.Errors.Select(e => e.PropertyName).ToHashSet();
        foreach (var field in new[] { "FactoryId", "FullName", "Phone", "JoinedOn", "DepartmentId", "Designation" })
            Assert.Contains(field, fields);
    }

    [Fact]
    public void Registration_rejects_bad_formats_and_accepts_the_optional_fields_when_empty()
    {
        var v = new CreateWorkerRequestValidator();
        string[] Bad(CreateWorkerRequest r) => v.Validate(r).Errors.Select(e => e.PropertyName).ToArray();

        Assert.Contains("Phone", Bad(ValidRequest() with { Phone = "call me" }));
        Assert.Contains("Phone", Bad(ValidRequest() with { Phone = "123" }));
        Assert.Contains("Email", Bad(ValidRequest() with { Email = "not-an-email" }));
        Assert.Contains("Gender", Bad(ValidRequest() with { Gender = "Robot" }));
        Assert.Contains("EmploymentType", Bad(ValidRequest() with { EmploymentType = "Forever" }));
        Assert.Contains("EmployeeCode", Bad(ValidRequest() with { EmployeeCode = "has space" }));
        Assert.Contains("JoinedOn", Bad(ValidRequest() with { JoinedOn = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(2)) }));
        Assert.Contains("DateOfBirth", Bad(ValidRequest() with { DateOfBirth = new DateOnly(2020, 1, 1) })); // 4 years old at joining
        Assert.Contains("DateOfBirth", Bad(ValidRequest() with { DateOfBirth = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)) }));
        Assert.Contains("EmergencyContactPhone", Bad(ValidRequest() with { EmergencyContactPhone = "abc" }));
        Assert.Contains("PresentAddress", Bad(ValidRequest() with { PresentAddress = new string('x', 501) }));

        Assert.Empty(Bad(ValidRequest() with
        {
            Email = "", EmployeeCode = " ", DateOfBirth = new DateOnly(1995, 6, 1), EmploymentType = "contract",
            EmergencyContactPhone = "01800000000", Gender = "female",
        }));
    }

    [Fact]
    public void A_status_change_request_must_name_a_known_status()
    {
        var v = new ChangeWorkerStatusRequestValidator();
        Assert.False(v.Validate(new ChangeWorkerStatusRequest("Deleted")).IsValid);
        Assert.False(v.Validate(new ChangeWorkerStatusRequest("")).IsValid);
        Assert.True(v.Validate(new ChangeWorkerStatusRequest("terminated", "misconduct")).IsValid);
        Assert.False(v.Validate(new ChangeWorkerStatusRequest("Suspended", new string('x', 501))).IsValid);
    }

    [Fact]
    public void Card_ids_are_normalised_and_checked()
    {
        Assert.Equal("04A1B2C3", WorkerRules.NormaliseCardUid("04:a1-b2 c3"));
        var v = new IssueWorkerCardRequestValidator();
        Assert.True(v.Validate(new IssueWorkerCardRequest("04:A1:B2:C3", "nfc")).IsValid);
        Assert.False(v.Validate(new IssueWorkerCardRequest("!!")).IsValid);
        Assert.False(v.Validate(new IssueWorkerCardRequest("04A1B2C3", "Barcode")).IsValid);
        Assert.False(new DeactivateWorkerCardRequestValidator().Validate(new DeactivateWorkerCardRequest("WorkerLeft")).IsValid); // system-only reason
        Assert.True(new DeactivateWorkerCardRequestValidator().Validate(new DeactivateWorkerCardRequest("lost")).IsValid);
    }

    // ─── CSV ───────────────────────────────────────────────────────────────

    [Fact]
    public void Csv_reader_handles_quotes_embedded_delimiters_line_breaks_and_a_byte_order_mark()
    {
        var text = "﻿name,note\r\n\"Begum, Rahima\",\"said \"\"hi\"\"\"\r\n\r\nKarim,\"line one\nline two\"\n";
        var rows = Csv.Read(text, ',', 100);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { "name", "note" }, rows[0].Fields);
        Assert.Equal(new[] { "Begum, Rahima", "said \"hi\"" }, rows[1].Fields);
        Assert.Equal(new[] { "Karim", "line one\nline two" }, rows[2].Fields);
        Assert.Equal(2, rows[1].Line);
        Assert.Equal(4, rows[2].Line); // the blank line 3 is skipped but still counted
    }

    [Fact]
    public void Csv_reader_detects_semicolon_and_tab_files_and_refuses_broken_or_oversized_ones()
    {
        Assert.Equal(';', Csv.DetectDelimiter("full_name;phone;department\nA;1;B"));
        Assert.Equal('\t', Csv.DetectDelimiter("full_name\tphone\n"));
        Assert.Equal(',', Csv.DetectDelimiter("full_name,phone"));
        Assert.Throws<FormatException>(() => Csv.Read("a,b\n\"never closed,1\n", ',', 100));
        Assert.Throws<FormatException>(() => Csv.Read("a\n1\n2\n3\n", ',', 3));
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+8801711", "'+8801711")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData(null, "")]
    public void Csv_writer_quotes_what_needs_it_and_defuses_spreadsheet_formulas(string? value, string expected) =>
        Assert.Equal(expected, Csv.Escape(value));

    // ─── Import parsing ────────────────────────────────────────────────────

    private const string Header = "full_name,phone,joining_date,department,designation,employment_type,employee_number,gender,date_of_birth,email";

    [Fact]
    public void A_well_formed_import_file_parses_into_valid_rows()
    {
        var parse = WorkerImportParser.Parse(
            Header + "\n" +
            "Rahima Begum,+8801711000001,2024-01-15,Sewing,Operator,Permanent,emp-1,Female,1995-06-01,Rahima@Example.com\n" +
            "Karim Uddin,01711000002,15/02/2024,Cutting,Cutter,,,,,\n", Today);

        Assert.Null(parse.FileError);
        Assert.Equal(2, parse.Rows.Count);
        Assert.All(parse.Rows, r => Assert.True(r.IsValid, string.Join("; ", r.Errors.Select(e => e.Message))));
        var first = parse.Rows[0];
        Assert.Equal((2, "Rahima Begum", "EMP-1", "Female", "rahima@example.com"), (first.RowNumber, first.FullName, first.EmployeeCode, first.Gender, first.Email));
        Assert.Equal(new DateOnly(2024, 1, 15), first.JoinedOn);
        Assert.Equal(new DateOnly(1995, 6, 1), first.DateOfBirth);
        var second = parse.Rows[1];
        Assert.Equal(new DateOnly(2024, 2, 15), second.JoinedOn); // day-first dates are accepted
        Assert.Equal("Permanent", second.EmploymentType);         // the default
        Assert.Null(second.EmployeeCode);
    }

    [Fact]
    public void The_downloadable_template_is_itself_a_valid_import_file()
    {
        var parse = WorkerImportParser.Parse(WorkerImportParser.Template(), Today);
        Assert.Null(parse.FileError);
        Assert.Equal(WorkerImportParser.TemplateColumns, parse.RecognisedColumns);
        var row = Assert.Single(parse.Rows);
        Assert.True(row.IsValid, string.Join("; ", row.Errors.Select(e => e.Message)));
        Assert.Equal("+8801700000000", row.Phone); // the apostrophe that defuses "+" on export is removed again
    }

    [Fact]
    public void Header_names_are_matched_loosely_and_unknown_columns_are_reported_not_fatal()
    {
        var parse = WorkerImportParser.Parse(
            "Name;Mobile Number;Date of Joining;Dept;Job Title;Shoe Size\nRahima;01711000001;2024-01-15;Sewing;Operator;38\n", Today);
        Assert.Null(parse.FileError);
        Assert.Equal(new[] { "Shoe Size" }, parse.IgnoredColumns);
        Assert.True(Assert.Single(parse.Rows).IsValid);
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("full_name,phone\nA,1\n", "Missing required column")]
    [InlineData(Header + "\n", "no worker rows")]
    [InlineData("full_name,name,phone,joining_date,department,designation\nA,B,1,2,3,4\n", "more than once")]
    [InlineData("PK\u0003\u0004\0\0binary", "not a text CSV")]
    public void Unusable_files_are_refused_with_a_reason(string text, string expected)
    {
        var parse = WorkerImportParser.Parse(text, Today);
        Assert.NotNull(parse.FileError);
        Assert.Contains(expected, parse.FileError, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(parse.Rows);
    }

    [Fact]
    public void A_file_with_too_many_rows_is_refused()
    {
        var text = Header + "\n" + string.Concat(Enumerable.Range(0, WorkerImportParser.MaxRows + 1)
            .Select(i => $"Worker {i},01711{i:D6},2024-01-15,Sewing,Operator,,,,,\n"));
        Assert.Contains($"{WorkerImportParser.MaxRows}", WorkerImportParser.Parse(text, Today).FileError);
    }

    [Fact]
    public void Each_bad_value_is_reported_on_its_own_row_and_field()
    {
        var parse = WorkerImportParser.Parse(
            Header + "\n" +
            ",01711000001,2024-01-15,Sewing,Operator,,,,,\n" +                         // 2: no name
            "No Phone,,2024-01-15,Sewing,Operator,,,,,\n" +                            // 3
            "Bad Phone,phone,2024-01-15,Sewing,Operator,,,,,\n" +                      // 4
            "Bad Date,01711000004,31/02/2024,Sewing,Operator,,,,,\n" +                 // 5
            "Future,01711000005,2099-01-01,Sewing,Operator,,,,,\n" +                   // 6
            "No Dept,01711000006,2024-01-15,,Operator,,,,,\n" +                        // 7
            "No Title,01711000007,2024-01-15,Sewing,,,,,,\n" +                         // 8
            "Bad Type,01711000008,2024-01-15,Sewing,Operator,Forever,,,,\n" +          // 9
            "Bad Gender,01711000009,2024-01-15,Sewing,Operator,,,Robot,,\n" +          // 10
            "Child,01711000010,2024-01-15,Sewing,Operator,,,,2015-01-01,\n" +          // 11
            "Bad Email,01711000011,2024-01-15,Sewing,Operator,,,,,nope\n" +            // 12
            "Dup One,01711000012,2024-01-15,Sewing,Operator,,E-9,,,\n" +               // 13
            "Dup Two,01711000013,2024-01-15,Sewing,Operator,,e-9,,,\n" +               // 14: same employee number, other case
            "Fine,01711000014,2024-01-15,Sewing,Operator,,,,,\n", Today);              // 15

        Assert.Null(parse.FileError);
        string FieldOf(int line) => Assert.Single(parse.Rows.Single(r => r.RowNumber == line).Errors).Field;
        Assert.Equal("fullName", FieldOf(2));
        Assert.Equal("phone", FieldOf(3));
        Assert.Equal("phone", FieldOf(4));
        Assert.Equal("joinedOn", FieldOf(5));
        Assert.Equal("joinedOn", FieldOf(6));
        Assert.Equal("department", FieldOf(7));
        Assert.Equal("designation", FieldOf(8));
        Assert.Equal("employmentType", FieldOf(9));
        Assert.Equal("gender", FieldOf(10));
        Assert.Equal("dateOfBirth", FieldOf(11));
        Assert.Equal("email", FieldOf(12));
        Assert.True(parse.Rows.Single(r => r.RowNumber == 13).IsValid);
        Assert.Equal("employeeCode", FieldOf(14));
        Assert.Contains("line 13", parse.Rows.Single(r => r.RowNumber == 14).Errors[0].Message);
        Assert.True(parse.Rows.Single(r => r.RowNumber == 15).IsValid);
        Assert.Equal(2, parse.Rows.Count(r => r.IsValid));
    }

    // ─── Who may do what ───────────────────────────────────────────────────

    private static IReadOnlyList<string> PermsOf(string role) => AccessCatalog.Role(role)!.Permissions;

    [Fact]
    public void Super_admin_and_factory_admin_manage_workers_departments_and_accounts()
    {
        foreach (var role in new[] { R.SuperAdmin, R.FactoryAdmin })
            foreach (var p in new[] { P.WorkersView, P.WorkersDetailsView, P.WorkersManage, P.WorkersAssign, P.DepartmentsManage, P.UsersCreate, P.UsersUpdate })
                Assert.Contains(p, PermsOf(role));
        Assert.Equal(RoleScope.Platform, AccessCatalog.Role(R.SuperAdmin)!.Scope);
        Assert.Equal(RoleScope.Factory, AccessCatalog.Role(R.FactoryAdmin)!.Scope);
    }

    [Fact]
    public void Hr_manages_worker_records_but_not_login_accounts_or_departments()
    {
        var p = PermsOf(R.HrOfficer);
        foreach (var required in new[] { P.WorkersView, P.WorkersDetailsView, P.WorkersManage }) Assert.Contains(required, p);
        foreach (var forbidden in new[] { P.UsersCreate, P.UsersUpdate, P.RolesAssign, P.DepartmentsManage }) Assert.DoesNotContain(forbidden, p);
    }

    [Fact]
    public void Factory_manager_sees_employment_details_and_assigns_workstations_but_edits_no_worker_records()
    {
        var p = PermsOf(R.FactoryManager);
        Assert.Contains(P.WorkersView, p);
        Assert.Contains(P.WorkersDetailsView, p);
        Assert.Contains(P.WorkersAssign, p);
        foreach (var forbidden in new[] { P.WorkersManage, P.DepartmentsManage, P.LinesManage }) Assert.DoesNotContain(forbidden, p);
    }

    [Fact]
    public void Supervisor_and_monitoring_operator_see_only_the_basic_directory()
    {
        foreach (var role in new[] { R.ProductionSupervisor, R.MonitoringOperator })
        {
            var p = PermsOf(role);
            Assert.Contains(P.WorkersView, p);
            Assert.DoesNotContain(P.WorkersDetailsView, p);
            Assert.DoesNotContain(P.WorkersManage, p);
        }
        Assert.Equal(RoleScope.Line, AccessCatalog.Role(R.ProductionSupervisor)!.Scope);
        Assert.DoesNotContain(P.WorkersAssign, PermsOf(R.MonitoringOperator));
    }

    [Fact]
    public void Device_administrators_and_workers_have_no_access_to_the_worker_directory()
    {
        foreach (var role in new[] { R.DeviceAdmin, R.Worker })
            foreach (var forbidden in new[] { P.WorkersView, P.WorkersDetailsView, P.WorkersManage, P.WorkersAssign })
                Assert.DoesNotContain(forbidden, PermsOf(role));
        Assert.Contains(P.SelfView, PermsOf(R.Worker));
        Assert.Equal(RoleScope.Self, AccessCatalog.Role(R.Worker)!.Scope);
    }

    [Fact]
    public void Worker_permissions_are_live_and_reach_no_platform_only_role()
    {
        foreach (var code in new[] { P.WorkersView, P.WorkersDetailsView, P.WorkersManage, P.WorkersAssign, P.DepartmentsManage })
        {
            var def = AccessCatalog.Permission(code);
            Assert.NotNull(def);
            Assert.False(def!.Reserved, $"{code} is used by endpoints and must not be reserved");
            Assert.False(def.PlatformOnly);
        }
    }
}
