using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodHotel.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingStays : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "booking_stays",
                columns: table => new
                {
                    BookingId = table.Column<int>(type: "int", nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    CheckedInAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    CheckedInByUserId = table.Column<int>(type: "int", nullable: false),
                    CheckedOutAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CheckedOutByUserId = table.Column<int>(type: "int", nullable: true),
                    IsEarlyCheckIn = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    CheckInNote = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_booking_stays", x => x.BookingId);
                    table.ForeignKey(
                        name: "FK_booking_stays_bookings_BookingId",
                        column: x => x.BookingId,
                        principalTable: "bookings",
                        principalColumn: "BookingId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_booking_stays_users_CheckedInByUserId",
                        column: x => x.CheckedInByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_booking_stays_users_CheckedOutByUserId",
                        column: x => x.CheckedOutByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "IX_booking_stays_CheckedInByUserId",
                table: "booking_stays",
                column: "CheckedInByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_booking_stays_CheckedOutByUserId",
                table: "booking_stays",
                column: "CheckedOutByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "booking_stays");
        }
    }
}
