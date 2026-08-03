using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddSuperAdminConsole : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "CreatedAt",
                table: "Workshops",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "EnabledModules",
                table: "Workshops",
                type: "nvarchar(max)",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "Workshops",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "ImpersonationLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UserEmail = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CompanyCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImpersonationLogs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImpersonationLogs_CompanyCode_At",
                table: "ImpersonationLogs",
                columns: new[] { "CompanyCode", "At" });

            // Existing companies predate module configuration. Given the full
            // set rather than an empty one — empty would read as "every module
            // off" and blank the menu for a workshop already using them.
            migrationBuilder.Sql(
                "UPDATE Workshops SET EnabledModules = " +
                "'services,billing,reports,serviceHistory,staff,deliveries,fiscalYear,multiBranch,onlineBooking,onlinePayment' " +
                "WHERE EnabledModules IS NULL OR EnabledModules = '';");

            // And they are active. A bit column added to an existing table
            // defaults to 0, which would suspend every company on deploy —
            // locking every current user out the moment this ships.
            migrationBuilder.Sql("UPDATE Workshops SET IsActive = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImpersonationLogs");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "EnabledModules",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "Workshops");
        }
    }
}
