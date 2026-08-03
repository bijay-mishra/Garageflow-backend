using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantIsolation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Vehicles",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Services",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Payments",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Notifications",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "JobLines",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "JobCards",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Invoices",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Deliveries",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Customers",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Bookings",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "CompanyCode",
                table: "Activities",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "");


            // Every row that exists today belongs to the one company that has
            // ever run on this database. Without this they would all carry the
            // empty default, match no tenant filter, and vanish from the app —
            // the data would still be there and nobody could reach it.
            //
            // Ordinary UPDATEs rather than a default constraint, because the
            // right value is a fact about the existing data, not about the
            // column.
            foreach (var table in new[]
            {
                "Customers", "Vehicles", "JobCards", "JobLines", "Invoices",
                "Payments", "Bookings", "Deliveries", "Services",
                "Activities", "Notifications",
            })
            {
                migrationBuilder.Sql(
                    $"UPDATE [{table}] SET CompanyCode = 'DEMO' WHERE CompanyCode = '';");
            }

            migrationBuilder.CreateIndex(
                name: "IX_Vehicles_CompanyCode",
                table: "Vehicles",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Services_CompanyCode",
                table: "Services",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CompanyCode",
                table: "Payments",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_CompanyCode",
                table: "Notifications",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_JobLines_CompanyCode",
                table: "JobLines",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_JobCards_CompanyCode",
                table: "JobCards",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_CompanyCode",
                table: "Invoices",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_CompanyCode",
                table: "Deliveries",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_CompanyCode",
                table: "Customers",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Bookings_CompanyCode",
                table: "Bookings",
                column: "CompanyCode");

            migrationBuilder.CreateIndex(
                name: "IX_Activities_CompanyCode",
                table: "Activities",
                column: "CompanyCode");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Vehicles_CompanyCode",
                table: "Vehicles");

            migrationBuilder.DropIndex(
                name: "IX_Services_CompanyCode",
                table: "Services");

            migrationBuilder.DropIndex(
                name: "IX_Payments_CompanyCode",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Notifications_CompanyCode",
                table: "Notifications");

            migrationBuilder.DropIndex(
                name: "IX_JobLines_CompanyCode",
                table: "JobLines");

            migrationBuilder.DropIndex(
                name: "IX_JobCards_CompanyCode",
                table: "JobCards");

            migrationBuilder.DropIndex(
                name: "IX_Invoices_CompanyCode",
                table: "Invoices");

            migrationBuilder.DropIndex(
                name: "IX_Deliveries_CompanyCode",
                table: "Deliveries");

            migrationBuilder.DropIndex(
                name: "IX_Customers_CompanyCode",
                table: "Customers");

            migrationBuilder.DropIndex(
                name: "IX_Bookings_CompanyCode",
                table: "Bookings");

            migrationBuilder.DropIndex(
                name: "IX_Activities_CompanyCode",
                table: "Activities");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Vehicles");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Services");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "JobLines");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "JobCards");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Deliveries");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Bookings");

            migrationBuilder.DropColumn(
                name: "CompanyCode",
                table: "Activities");
        }
    }
}
