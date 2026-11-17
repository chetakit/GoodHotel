using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodHotel.Migrations
{
    /// <inheritdoc />
    public partial class AddHousekeepingTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "housekeeping_tasks",
                columns: table => new
                {
                    TaskId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    RoomId = table.Column<int>(type: "int", nullable: false),
                    StayBookingId = table.Column<int>(type: "int", nullable: true),
                    TaskType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AssignedToUserId = table.Column<int>(type: "int", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "int", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    CompletedByUserId = table.Column<int>(type: "int", nullable: true),
                    VerifiedByUserId = table.Column<int>(type: "int", nullable: true),
                    VerifiedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_housekeeping_tasks", x => x.TaskId);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_rooms_RoomId",
                        column: x => x.RoomId,
                        principalTable: "rooms",
                        principalColumn: "RoomId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_users_AssignedToUserId",
                        column: x => x.AssignedToUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_users_CompletedByUserId",
                        column: x => x.CompletedByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_users_CreatedByUserId",
                        column: x => x.CreatedByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_tasks_users_VerifiedByUserId",
                        column: x => x.VerifiedByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "housekeeping_task_events",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    TaskId = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    PreviousStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NewStatus = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PreviousAssignee = table.Column<int>(type: "int", nullable: true),
                    NewAssignee = table.Column<int>(type: "int", nullable: true),
                    Reason = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_housekeeping_task_events", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_housekeeping_task_events_housekeeping_tasks_TaskId",
                        column: x => x.TaskId,
                        principalTable: "housekeeping_tasks",
                        principalColumn: "TaskId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_task_events_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_task_events_users_NewAssignee",
                        column: x => x.NewAssignee,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_housekeeping_task_events_users_PreviousAssignee",
                        column: x => x.PreviousAssignee,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_task_events_ActorUserId_OccurredAtUtc",
                table: "housekeeping_task_events",
                columns: new[] { "ActorUserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_task_events_NewAssignee",
                table: "housekeeping_task_events",
                column: "NewAssignee");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_task_events_PreviousAssignee",
                table: "housekeeping_task_events",
                column: "PreviousAssignee");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_task_events_TaskId",
                table: "housekeeping_task_events",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_AssignedToUserId",
                table: "housekeeping_tasks",
                column: "AssignedToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_CompletedByUserId",
                table: "housekeeping_tasks",
                column: "CompletedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_CreatedByUserId",
                table: "housekeeping_tasks",
                column: "CreatedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_RoomId",
                table: "housekeeping_tasks",
                column: "RoomId");

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_Status_AssignedToUserId",
                table: "housekeeping_tasks",
                columns: new[] { "Status", "AssignedToUserId" });

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_StayBookingId_TaskType",
                table: "housekeeping_tasks",
                columns: new[] { "StayBookingId", "TaskType" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_housekeeping_tasks_VerifiedByUserId",
                table: "housekeeping_tasks",
                column: "VerifiedByUserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "housekeeping_task_events");

            migrationBuilder.DropTable(
                name: "housekeeping_tasks");
        }
    }
}
