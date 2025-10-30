using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class CreatorCodeSystem : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreatorCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatorUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DiscountPercent = table.Column<decimal>(type: "numeric", nullable: false),
                    RevenueSharePercent = table.Column<decimal>(type: "numeric", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    MaxUses = table.Column<int>(type: "integer", nullable: true),
                    TimesUsed = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatorCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CreatorCodeRevenues",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CreatorCodeId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    OriginalPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    FinalPrice = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatorRevenue = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TransactionReference = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    PurchasedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsPaidOut = table.Column<bool>(type: "boolean", nullable: false),
                    PaidOutAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreatorCodeRevenues", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CreatorCodeRevenues_CreatorCodes_CreatorCodeId",
                        column: x => x.CreatorCodeId,
                        principalTable: "CreatorCodes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreatorCodeRevenues_CreatorCodeId",
                table: "CreatorCodeRevenues",
                column: "CreatorCodeId");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorCodeRevenues_CreatorCodeId_PurchasedAt",
                table: "CreatorCodeRevenues",
                columns: new[] { "CreatorCodeId", "PurchasedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_CreatorCodeRevenues_IsPaidOut",
                table: "CreatorCodeRevenues",
                column: "IsPaidOut");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorCodeRevenues_PurchasedAt",
                table: "CreatorCodeRevenues",
                column: "PurchasedAt");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorCodes_Code",
                table: "CreatorCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreatorCodes_CreatorUserId",
                table: "CreatorCodes",
                column: "CreatorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_CreatorCodes_IsActive",
                table: "CreatorCodes",
                column: "IsActive");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreatorCodeRevenues");

            migrationBuilder.DropTable(
                name: "CreatorCodes");
        }
    }
}
