using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerMemberships : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "About",
                table: "Workshops",
                type: "nvarchar(600)",
                maxLength: 600,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<bool>(
                name: "IsListed",
                table: "Workshops",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "UserWorkshopLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    UserId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompanyCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    CustomerId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    IsPrimary = table.Column<bool>(type: "bit", nullable: false),
                    JoinedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserWorkshopLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_UserWorkshopLinks_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_UserWorkshopLinks_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Workshops_IsListed",
                table: "Workshops",
                column: "IsListed");

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkshopLinks_CustomerId",
                table: "UserWorkshopLinks",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserWorkshopLinks_UserId_CompanyCode",
                table: "UserWorkshopLinks",
                columns: new[] { "UserId", "CompanyCode" },
                unique: true);

            // Every customer account that already exists was bound to one garage
            // through User.CompanyCode + User.CustomerId. Without a link row they
            // would still see that garage's data — the cursor is unchanged — but
            // the account would appear to belong to nowhere: no garage in "mine",
            // and select-workshop refusing the very workshop they are looking at.
            // This turns the old implicit binding into an explicit membership.
            migrationBuilder.DeferredSql(
                """
                INSERT INTO UserWorkshopLinks (UserId, CompanyCode, CustomerId, IsPrimary, JoinedAt)
                SELECT u.Id, u.CompanyCode, u.CustomerId, 1, u.CreatedAt
                FROM Users u
                WHERE u.Role = 'Customer'
                  AND u.CustomerId IS NOT NULL
                  AND u.CompanyCode <> ''
                  AND NOT EXISTS (
                      SELECT 1 FROM UserWorkshopLinks l
                      WHERE l.UserId = u.Id AND l.CompanyCode = u.CompanyCode);
                """);

            // The demo workshop opts into the directory so the customer app has
            // something to show on the first run. A real deployment leaves every
            // workshop unlisted until it chooses otherwise, which is why this is
            // scoped to DEMO rather than being the column default.
            migrationBuilder.DeferredSql("UPDATE Workshops SET IsListed = 1 WHERE CompanyCode = 'DEMO';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserWorkshopLinks");

            migrationBuilder.DropIndex(
                name: "IX_Workshops_IsListed",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "About",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "IsListed",
                table: "Workshops");
        }
    }
}
