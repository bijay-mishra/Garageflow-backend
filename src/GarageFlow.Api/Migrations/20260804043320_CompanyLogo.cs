using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GarageFlow.Api.Migrations
{
    /// <inheritdoc />
    public partial class CompanyLogo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LogoPath",
                table: "Workshops",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LogoPath",
                table: "Workshops");
        }
    }
}
