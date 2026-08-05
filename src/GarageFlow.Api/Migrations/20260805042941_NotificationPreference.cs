using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <summary>
    /// Adds the per-account "do you want your phone to buzz" switch.
    /// </summary>
    /// <remarks>
    /// <c>defaultValue</c> is spelled out as <c>true</c>, and the backfill below
    /// exists, because neither is automatic. EF scaffolds a bool column with
    /// <c>defaultValue: false</c> regardless of the C# property initializer — it
    /// does not read <c>= true</c> off the property — so the generated migration
    /// silently switched notifications *off* for every account that already
    /// existed, and would have done the same for any row inserted without naming
    /// the column.
    /// </remarks>
    public partial class NotificationPreference : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NotificationsEnabled",
                table: "Users",
                type: "bit",
                nullable: false,
                defaultValue: true);

            // Belt and braces. `defaultValue` fills the existing rows as the
            // ALTER runs and covers inserts from here on; this covers the case
            // where it did not. Harmless if the default did its job, and the
            // difference between a working app and a silent one if it did not.
            //
            // DeferredSql, not Sql: this reads a column the statement above is
            // adding, and a generated deployment script puts both in one batch
            // that SQL Server compiles before executing.
            migrationBuilder.DeferredSql("UPDATE Users SET NotificationsEnabled = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NotificationsEnabled",
                table: "Users");
        }
    }
}
