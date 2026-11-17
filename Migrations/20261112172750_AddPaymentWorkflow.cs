using System;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodHotel.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvidenceFileName",
                table: "payments",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "EvidenceSha256",
                table: "payments",
                type: "varchar(64)",
                unicode: false,
                maxLength: 64,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "LegacyDisposition",
                table: "payments",
                type: "varchar(20)",
                maxLength: 20,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "SubmittedAtUtc",
                table: "payments",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SubmittedByUserId",
                table: "payments",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "VerifiedAtUtc",
                table: "payments",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerifiedByUserId",
                table: "payments",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "payment_events",
                columns: table => new
                {
                    EventId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PaymentId = table.Column<int>(type: "int", nullable: false),
                    ActorUserId = table.Column<int>(type: "int", nullable: false),
                    EventType = table.Column<string>(type: "varchar(30)", maxLength: 30, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: true),
                    PaymentMethod = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IdempotencyKey = table.Column<string>(type: "varchar(32)", unicode: false, maxLength: 32, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_events", x => x.EventId);
                    table.ForeignKey(
                        name: "FK_payment_events_payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "payments",
                        principalColumn: "PaymentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_events_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateTable(
                name: "payment_refunds",
                columns: table => new
                {
                    RefundId = table.Column<int>(type: "int", nullable: false)
                        .Annotation("MySql:ValueGenerationStrategy", MySqlValueGenerationStrategy.IdentityColumn),
                    PaymentId = table.Column<int>(type: "int", nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(10,2)", precision: 10, scale: 2, nullable: false),
                    Status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reason = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false, collation: "utf8mb4_unicode_ci")
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    RequestedByUserId = table.Column<int>(type: "int", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ApprovedByUserId = table.Column<int>(type: "int", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_refunds", x => x.RefundId);
                    table.ForeignKey(
                        name: "FK_payment_refunds_payments_PaymentId",
                        column: x => x.PaymentId,
                        principalTable: "payments",
                        principalColumn: "PaymentId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_refunds_users_ApprovedByUserId",
                        column: x => x.ApprovedByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_payment_refunds_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "users",
                        principalColumn: "UserId",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4")
                .Annotation("Relational:Collation", "utf8mb4_unicode_ci");

            migrationBuilder.CreateIndex(
                name: "IX_payments_EvidenceSha256",
                table: "payments",
                column: "EvidenceSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payments_SubmittedByUserId",
                table: "payments",
                column: "SubmittedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_payments_VerifiedByUserId",
                table: "payments",
                column: "VerifiedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_ActorUserId",
                table: "payment_events",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_IdempotencyKey",
                table: "payment_events",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_payment_events_PaymentId_EventId",
                table: "payment_events",
                columns: new[] { "PaymentId", "EventId" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_ApprovedByUserId",
                table: "payment_refunds",
                column: "ApprovedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_PaymentId_Status",
                table: "payment_refunds",
                columns: new[] { "PaymentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_payment_refunds_RequestedByUserId",
                table: "payment_refunds",
                column: "RequestedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_payments_users_SubmittedByUserId",
                table: "payments",
                column: "SubmittedByUserId",
                principalTable: "users",
                principalColumn: "UserId",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_users_VerifiedByUserId",
                table: "payments",
                column: "VerifiedByUserId",
                principalTable: "users",
                principalColumn: "UserId",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_payments_users_SubmittedByUserId",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_users_VerifiedByUserId",
                table: "payments");

            migrationBuilder.DropTable(
                name: "payment_events");

            migrationBuilder.DropTable(
                name: "payment_refunds");

            migrationBuilder.DropIndex(
                name: "IX_payments_EvidenceSha256",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_SubmittedByUserId",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_VerifiedByUserId",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "EvidenceFileName",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "EvidenceSha256",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "LegacyDisposition",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "SubmittedAtUtc",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "SubmittedByUserId",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "VerifiedAtUtc",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "VerifiedByUserId",
                table: "payments");
        }
    }
}
