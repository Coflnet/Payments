using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class TrialSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "TrialEndsAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "TrialUsedAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TrialUsages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    TrialStartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExternalSubscriptionId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TrialUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TrialUsages_Product_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Product",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TrialUsages_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TrialUsages_ProductId",
                table: "TrialUsages",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_TrialUsages_TrialStartedAt",
                table: "TrialUsages",
                column: "TrialStartedAt");

            migrationBuilder.CreateIndex(
                name: "IX_TrialUsages_UserId_ProductId",
                table: "TrialUsages",
                columns: new[] { "UserId", "ProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TrialUsages");

            migrationBuilder.DropColumn(
                name: "TrialEndsAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "TrialUsedAt",
                table: "Subscriptions");
        }
    }
}
