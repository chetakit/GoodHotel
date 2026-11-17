using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GoodHotel.Migrations
{
    /// <inheritdoc />
    public partial class AddPromotionRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DiscountType",
                table: "promotions",
                type: "varchar(10)",
                maxLength: 10,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<decimal>(
                name: "DiscountValue",
                table: "promotions",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "EndDate",
                table: "promotions",
                type: "date",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "promotions",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxDiscount",
                table: "promotions",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MinSpend",
                table: "promotions",
                type: "decimal(10,2)",
                precision: 10,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleType",
                table: "promotions",
                type: "varchar(30)",
                maxLength: 30,
                nullable: true,
                collation: "utf8mb4_unicode_ci")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "RuleValue",
                table: "promotions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartDate",
                table: "promotions",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DiscountType",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "DiscountValue",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "EndDate",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "MaxDiscount",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "MinSpend",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "RuleType",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "RuleValue",
                table: "promotions");

            migrationBuilder.DropColumn(
                name: "StartDate",
                table: "promotions");
        }
    }
}
