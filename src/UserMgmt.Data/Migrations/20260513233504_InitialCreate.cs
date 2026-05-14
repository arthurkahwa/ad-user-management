using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UserMgmt.Data.Migrations;

/// <summary>
/// Initial schema for the sidecar SQL store: <c>UserAttributes</c>,
/// <c>AuditEntries</c>, <c>ReconciliationQueue</c>, <c>AppLogs</c>. Also
/// grants append-only enforcement on <c>AuditEntries</c> via
/// <c>DENY UPDATE, DELETE</c>.
/// </summary>
/// <remarks>
/// The grant target principal is sourced at migration-apply time from
/// the <c>UserMgmt__AppPrincipal</c> environment variable (the double
/// underscore mirrors the <c>UserMgmt:AppPrincipal</c> config key under
/// Microsoft.Extensions.Configuration's env-var convention). If unset,
/// the principal defaults to <c>CURRENT_USER</c>, which is correct for
/// local dev and CI but should always be overridden in production
/// deploys to point at the application's gMSA identity.
/// </remarks>
public partial class InitialCreate : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AppLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Message = table.Column<string>(type: "nvarchar(max)", nullable: false),
                MessageTemplate = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Level = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                TimeStamp = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                Exception = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Properties = table.Column<string>(type: "nvarchar(max)", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AppLogs", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "AuditEntries",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Timestamp = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                ActorUpn = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Action = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                TargetUpn = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                FieldName = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                OldValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                NewValue = table.Column<string>(type: "nvarchar(max)", nullable: true),
                Source = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                Reason = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AuditEntries", x => x.Id);
                table.CheckConstraint(
                    "CK_AuditEntries_Reason",
                    "[Reason] IS NULL OR [Reason] IN ('Stale', 'Termination', 'Reorg', 'Compromise')");
            });

        migrationBuilder.CreateTable(
            name: "ReconciliationQueue",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("SqlServer:Identity", "1, 1"),
                Timestamp = table.Column<System.DateTime>(type: "datetime2", nullable: false),
                TargetUpn = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                Operation = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                Payload = table.Column<string>(type: "nvarchar(max)", nullable: false),
                Status = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                ResolvedBy = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                ResolvedAt = table.Column<System.DateTime>(type: "datetime2", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ReconciliationQueue", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "UserAttributes",
            columns: table => new
            {
                Upn = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                EmployeeId = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                CostCenter = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                ContractType = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                StaleRiskScore = table.Column<float>(type: "real", nullable: false),
                ExcludeFromMLScoring = table.Column<bool>(type: "bit", nullable: false),
                RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_UserAttributes", x => x.Upn);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntries_TargetUpn",
            table: "AuditEntries",
            column: "TargetUpn");

        migrationBuilder.CreateIndex(
            name: "IX_AuditEntries_Timestamp",
            table: "AuditEntries",
            column: "Timestamp");

        migrationBuilder.CreateIndex(
            name: "IX_ReconciliationQueue_Status",
            table: "ReconciliationQueue",
            column: "Status");

        migrationBuilder.CreateIndex(
            name: "IX_ReconciliationQueue_TargetUpn",
            table: "ReconciliationQueue",
            column: "TargetUpn");

        // Append-only enforcement: DENY UPDATE, DELETE on AuditEntries for the
        // configured application principal. The principal is supplied via the
        // UserMgmt__AppPrincipal env var (matching the UserMgmt:AppPrincipal
        // configuration key) and defaults to CURRENT_USER for local dev / CI.
        // In production this should be the application's gMSA login.
        var principal = System.Environment.GetEnvironmentVariable("UserMgmt__AppPrincipal");
        if (string.IsNullOrWhiteSpace(principal))
        {
            principal = "CURRENT_USER";
        }

        // SQL Server's DENY requires a literal principal, so we use dynamic SQL
        // built from a parameterised name. Quote the principal with QUOTENAME
        // when it is a real identifier; CURRENT_USER stays as a SQL keyword.
        var principalSql = principal.Equals("CURRENT_USER", System.StringComparison.OrdinalIgnoreCase)
            ? "CURRENT_USER"
            : "QUOTENAME(@principal)";

        migrationBuilder.Sql($@"
DECLARE @principal sysname = N'{principal.Replace("'", "''")}';
DECLARE @stmt NVARCHAR(MAX);
SET @stmt = N'DENY UPDATE, DELETE ON OBJECT::dbo.AuditEntries TO ' + {principalSql} + N';';
EXEC sp_executesql @stmt, N'@principal sysname', @principal = @principal;
");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Reverse the DENY grant before dropping tables so re-creation in a
        // subsequent migration starts from a clean permission state.
        var principal = System.Environment.GetEnvironmentVariable("UserMgmt__AppPrincipal");
        if (string.IsNullOrWhiteSpace(principal))
        {
            principal = "CURRENT_USER";
        }

        var principalSql = principal.Equals("CURRENT_USER", System.StringComparison.OrdinalIgnoreCase)
            ? "CURRENT_USER"
            : "QUOTENAME(@principal)";

        migrationBuilder.Sql($@"
IF OBJECT_ID('dbo.AuditEntries', 'U') IS NOT NULL
BEGIN
    DECLARE @principal sysname = N'{principal.Replace("'", "''")}';
    DECLARE @stmt NVARCHAR(MAX);
    SET @stmt = N'REVOKE UPDATE, DELETE ON OBJECT::dbo.AuditEntries FROM ' + {principalSql} + N';';
    EXEC sp_executesql @stmt, N'@principal sysname', @principal = @principal;
END
");

        migrationBuilder.DropTable(name: "AppLogs");
        migrationBuilder.DropTable(name: "AuditEntries");
        migrationBuilder.DropTable(name: "ReconciliationQueue");
        migrationBuilder.DropTable(name: "UserAttributes");
    }
}
