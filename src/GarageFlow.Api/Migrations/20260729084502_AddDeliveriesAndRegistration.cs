using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveriesAndRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Defaults matter here in a way EF cannot guess. It writes 0 and
            // false for new non-nullable columns, which would leave an existing
            // workshop with delivery switched off, priced at nothing and capped
            // at nought kilometres — a feature that silently does not work
            // rather than one that has not been configured yet. These match the
            // property initialisers on Workshop, so a row created before this
            // migration and one created after it behave identically.
            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryBaseFee",
                table: "Workshops",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 50m);

            migrationBuilder.AddColumn<bool>(
                name: "DeliveryEnabled",
                table: "Workshops",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryFreeAbove",
                table: "Workshops",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 5000m);

            migrationBuilder.AddColumn<double>(
                name: "DeliveryMaxKm",
                table: "Workshops",
                type: "float",
                nullable: false,
                defaultValue: 15.0);

            migrationBuilder.AddColumn<decimal>(
                name: "DeliveryPerKm",
                table: "Workshops",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 20m);

            migrationBuilder.CreateTable(
                name: "CustomerRegistrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    CustomerId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CompanyCode = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Contact = table.Column<string>(type: "nvarchar(160)", maxLength: 160, nullable: false),
                    CodeHash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Attempts = table.Column<int>(type: "int", nullable: false),
                    ConsumedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomerRegistrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CustomerRegistrations_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Deliveries",
                columns: table => new
                {
                    Id = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    JobCardId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CustomerId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Method = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Address = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: true),
                    Longitude = table.Column<double>(type: "float", nullable: true),
                    DistanceKm = table.Column<double>(type: "float", nullable: true),
                    Fee = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Driver = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    DriverLatitude = table.Column<double>(type: "float", nullable: true),
                    DriverLongitude = table.Column<double>(type: "float", nullable: true),
                    DriverAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ChosenAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    StartedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CompletedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Deliveries_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Deliveries_JobCards_JobCardId",
                        column: x => x.JobCardId,
                        principalTable: "JobCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeliveryPoints",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DeliveryId = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Latitude = table.Column<double>(type: "float", nullable: false),
                    Longitude = table.Column<double>(type: "float", nullable: false),
                    AccuracyMetres = table.Column<double>(type: "float", nullable: true),
                    At = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeliveryPoints", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeliveryPoints_Deliveries_DeliveryId",
                        column: x => x.DeliveryId,
                        principalTable: "Deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRegistrations_CompanyCode_Contact",
                table: "CustomerRegistrations",
                columns: new[] { "CompanyCode", "Contact" });

            migrationBuilder.CreateIndex(
                name: "IX_CustomerRegistrations_CustomerId",
                table: "CustomerRegistrations",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_CustomerId",
                table: "Deliveries",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_JobCardId",
                table: "Deliveries",
                column: "JobCardId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Deliveries_Status",
                table: "Deliveries",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_DeliveryPoints_DeliveryId_At",
                table: "DeliveryPoints",
                columns: new[] { "DeliveryId", "At" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CustomerRegistrations");

            migrationBuilder.DropTable(
                name: "DeliveryPoints");

            migrationBuilder.DropTable(
                name: "Deliveries");

            migrationBuilder.DropColumn(
                name: "DeliveryBaseFee",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "DeliveryEnabled",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "DeliveryFreeAbove",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "DeliveryMaxKm",
                table: "Workshops");

            migrationBuilder.DropColumn(
                name: "DeliveryPerKm",
                table: "Workshops");
        }
    }
}
