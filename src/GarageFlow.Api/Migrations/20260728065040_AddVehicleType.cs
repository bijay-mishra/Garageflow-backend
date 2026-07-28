using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddVehicleType : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // "Car" rather than EF's generated "" — the column is a closed
            // vocabulary (Vocabulary.VehicleTypes) and "" is not in it, so an
            // empty default would leave every existing row holding a value the
            // API's own validation rejects.
            migrationBuilder.AddColumn<string>(
                name: "Type",
                table: "Vehicles",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Car");

            // Rows that predate the column are now all "Car". Reclassify the
            // ones that are plainly not cars, so the workshop does not have to
            // re-type its whole book by hand.
            //
            // Matching is on make first — these manufacturers build nothing with
            // four wheels, so the make alone is decisive. Yamaha belongs here:
            // it builds motorcycles and marine engines, never cars.
            //
            // Honda and Suzuki are deliberately absent. Both sell cars *and*
            // two-wheelers here, so a make match would turn every Honda CR-V
            // into a bike; they are left to the model pass below.
            migrationBuilder.Sql("""
                UPDATE Vehicles
                SET    Type = 'Bike'
                WHERE  Make IN ('Bajaj', 'TVS', 'Hero', 'Hero Honda', 'Royal Enfield',
                                'KTM', 'Yamaha', 'Vespa', 'Aprilia', 'Benelli',
                                'Crossfire', 'Keeway', 'CFMoto');
                """);

            // Then on model, which settles the mixed-marque makers. SQL Server's
            // default collation is case-insensitive, so these need no casing.
            //
            // Short names are anchored with a prefix match rather than wrapped in
            // wildcards: a bare '%CB%' would also claim any car whose model
            // merely contains those letters.
            migrationBuilder.Sql("""
                UPDATE Vehicles
                SET    Type = 'Bike'
                WHERE  Type <> 'Bike'
                AND    (Model LIKE '%Activa%'   OR Model LIKE '%Pulsar%'
                     OR Model LIKE '%Splendor%' OR Model LIKE '%Shine%'
                     OR Model LIKE '%Unicorn%'  OR Model LIKE '%Dio%'
                     OR Model LIKE '%Scooty%'   OR Model LIKE '%Apache%'
                     OR Model LIKE '%Bullet%'   OR Model LIKE '%Himalayan%'
                     OR Model LIKE '%Duke%'     OR Model LIKE '%Jupiter%'
                     OR Model LIKE '%Burgman%'  OR Model LIKE '%Ntorq%'
                     OR Model LIKE '%Raider%'   OR Model LIKE '%Hornet%'
                     OR Model LIKE '%Fascino%'  OR Model LIKE '%Gixxer%'
                     OR Model LIKE '%Intruder%' OR Model LIKE '%Vespa%'
                     OR Model LIKE '%Livo%'     OR Model LIKE '%Fazer%'
                     OR Model LIKE 'FZ%'        OR Model LIKE 'R15%'
                     OR Model LIKE 'MT-%'       OR Model LIKE 'CB%'
                     OR Model LIKE 'Ray %'      OR Model LIKE 'Access %');
                """);

            // Commercial-only marques. Tata and Mahindra are excluded on purpose
            // — both sell passenger cars (Nexon, Scorpio), so the make proves
            // nothing about the body.
            migrationBuilder.Sql("""
                UPDATE Vehicles
                SET    Type = 'Truck'
                WHERE  Make IN ('Ashok Leyland', 'Eicher', 'BharatBenz');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Type",
                table: "Vehicles");
        }
    }
}
