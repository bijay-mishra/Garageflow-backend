using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomerLocation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "Latitude",
                table: "Customers",
                type: "float",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "Longitude",
                table: "Customers",
                type: "float",
                nullable: true);

            // Backfill for the demo rows. DbSeeder carries the same coordinates,
            // but it only runs against an empty database — without this, anyone
            // whose database already exists opens the map and finds it blank,
            // which reads as a broken feature rather than as missing data.
            //
            // Guarded on the address as well as the id so it cannot overwrite a
            // real customer who happens to occupy CUS-001 in someone's data.
            // Two of the eight are left unpinned on purpose: most customers will
            // not have a location, and the screens have to look right that way.
            foreach (var (id, address, lat, lng) in new[]
            {
                ("CUS-001", "Baneshwor, Kathmandu", 27.6893, 85.3436),
                ("CUS-002", "Lakeside, Pokhara", 28.2096, 83.9556),
                ("CUS-003", "Patan, Lalitpur", 27.6766, 85.3250),
                ("CUS-004", "Dharan, Sunsari", 26.8065, 87.2846),
                ("CUS-006", "Kirtipur, Kathmandu", 27.6789, 85.2774),
                ("CUS-007", "Bhaktapur", 27.6710, 85.4298),
            })
            {
                migrationBuilder.Sql(
                    $"""
                     UPDATE Customers
                     SET Latitude = {lat.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                         Longitude = {lng.ToString(System.Globalization.CultureInfo.InvariantCulture)}
                     WHERE Id = '{id}' AND Address = '{address}'
                       AND Latitude IS NULL AND Longitude IS NULL;
                     """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Latitude",
                table: "Customers");

            migrationBuilder.DropColumn(
                name: "Longitude",
                table: "Customers");
        }
    }
}
