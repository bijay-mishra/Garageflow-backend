using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkshopAndPaymentChannels : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults matter here in a way EF cannot guess. Every payment that
            // already exists is money that was genuinely taken, so it has to
            // land as Completed with a real channel — an empty Status would drop
            // all of it out of the collections report and off printed bills,
            // both of which count only Completed rows. The literal backfill
            // below fixes Channel, which cannot be expressed as one default.
            migrationBuilder.AddColumn<string>(
                name: "Channel",
                table: "Payments",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "cash");

            migrationBuilder.AddColumn<string>(
                name: "FailureReason",
                table: "Payments",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InitiatedAt",
                table: "Payments",
                type: "datetime2",
                nullable: false,
                defaultValue: new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified));

            migrationBuilder.AddColumn<string>(
                name: "ProviderRef",
                table: "Payments",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Reference",
                table: "Payments",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "Payments",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Completed");

            migrationBuilder.CreateTable(
                name: "Workshops",
                columns: table => new
                {
                    CompanyCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Name = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    LegalName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Phone = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Email = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    TaxNumber = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    InvoiceFooter = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    OpeningHours = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workshops", x => x.CompanyCode);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Reference",
                table: "Payments",
                column: "Reference",
                unique: true,
                filter: "[Reference] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Status_Channel",
                table: "Payments",
                columns: new[] { "Status", "Channel" });

            // Channel from method, for the rows that predate the column. The
            // default above put everything in "cash"; this moves the ones that
            // were not. Kept in step with Vocabulary.ChannelFor — a card
            // terminal settles through the bank, not through a wallet.
            migrationBuilder.DeferredSql(
                """
                UPDATE Payments SET Channel = 'bank'
                WHERE Method IN ('Bank Transfer', 'Card');

                UPDATE Payments SET Channel = 'online'
                WHERE Method IN ('eSewa', 'Khalti');
                """);

            // InitiatedAt has no meaningful history, so it is anchored to when
            // the money actually landed. Leaving it at year 1 would make every
            // old payment look like an attempt abandoned two millennia ago.
            migrationBuilder.DeferredSql("UPDATE Payments SET InitiatedAt = At;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Workshops");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Reference",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Status_Channel",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Channel",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "FailureReason",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "InitiatedAt",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ProviderRef",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Reference",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "Payments");
        }
    }
}
