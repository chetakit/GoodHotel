using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodHotel.Migrations
{
    /// <inheritdoc />
    public partial class AddMaintenanceReports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "maintenance_reports",
                columns: table => new
                {
                    ReportId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RoomId = table.Column<int>(type: "int", nullable: false),
                    Description = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BlocksRoom = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    BlockFrom = table.Column<DateOnly>(type: "date", nullable: true),
                    BlockUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    ReportedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ReportedByUserId = table.Column<int>(type: "int", nullable: false),
                    ResolvedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ResolvedByUserId = table.Column<int>(type: "int", nullable: true),
                    ResolutionNote = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_maintenance_reports", x => x.ReportId);
                    table.ForeignKey(
                        name: "FK_maintenance_reports_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "RoomId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_reports_users_ReportedByUserId",
                        column: x => x.ReportedByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_maintenance_reports_users_ResolvedByUserId",
                        column: x => x.ResolvedByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_reports_ReportedByUserId",
                table: "maintenance_reports",
                column: "ReportedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_reports_ResolvedByUserId",
                table: "maintenance_reports",
                column: "ResolvedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_maintenance_reports_RoomId_Status_BlocksRoom",
                table: "maintenance_reports",
                columns: new[] { "RoomId", "Status", "BlocksRoom" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "maintenance_reports");
        }
    }
}
