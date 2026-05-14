using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace UserMgmt.Data.Migrations
{
    /// <summary>
    /// Replaces the SQL Server <c>rowversion</c> column on
    /// <c>UserAttributes.RowVersion</c> with an app-bumped
    /// <c>uniqueidentifier</c> concurrency token.
    /// </summary>
    /// <remarks>
    /// Background: SQL Server's <c>rowversion</c> type is server-generated and
    /// monotonically bumped on every <c>UPDATE</c>; SQLite has no equivalent,
    /// which left M1.1's RowVersion concurrency test deferred. Switching to
    /// an app-managed <see cref="Guid"/> token makes the concurrency mechanism
    /// behave identically across SQL Server, SQLite, and EF Core's in-memory
    /// provider — see <c>docs/ARCHITECTURE-NOTES.md</c> for the full rationale.
    /// <para>
    /// The migration assumes the <c>UserAttributes</c> table is empty (M1.1
    /// has only just landed and no row has been inserted in any deployed
    /// environment). If you are re-running this against a populated DB,
    /// add a manual <c>UPDATE UserAttributes SET RowVersion = NEWID()</c>
    /// step before applying — the auto-generated <c>AlterColumn</c> will
    /// drop the <c>rowversion</c> bytes and write a default <c>Guid.Empty</c>
    /// into every row, which is not what you want.
    /// </para>
    /// </remarks>
    public partial class ChangeRowVersionToGuidConcurrencyToken : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AlterColumn<Guid>(
                name: "RowVersion",
                table: "UserAttributes",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(byte[]),
                oldType: "rowversion",
                oldRowVersion: true,
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AlterColumn<byte[]>(
                name: "RowVersion",
                table: "UserAttributes",
                type: "rowversion",
                rowVersion: true,
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");
        }
    }
}
