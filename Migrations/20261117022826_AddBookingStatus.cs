using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodHotel.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Status",
                table: "bookings",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Booked",
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "CancelledAtUtc",
                table: "bookings",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "CancelledAtUtc", table: "bookings");
            migrationBuilder.DropColumn(name: "Status", table: "bookings");
        }
    }
}
