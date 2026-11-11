using GoodHotel.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace GoodHotel.Migrations;

[DbContext(typeof(GoodHotelDbContext))]
[Migration("20261111193000_AddRoomImagePath")]
public partial class AddRoomImagePath : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
                name: "ImagePath",
                table: "rooms",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true,
                collation: "utf8mb3_general_ci")
            .Annotation("MySql:CharSet", "utf8mb3");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ImagePath",
            table: "rooms");
    }
}
