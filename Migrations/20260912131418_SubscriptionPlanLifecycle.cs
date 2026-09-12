using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionPlanLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ChangeLockUntil",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ProviderVariantId",
                table: "Subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionId",
                table: "OwnerShip",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubscriptionId",
                table: "FiniteTransactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "RefundedSubscriptionInvoices",
                columns: table => new
                {
                    InvoiceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubscriptionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BillingReason = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RefundedSubscriptionInvoices", x => x.InvoiceId);
                });

            migrationBuilder.CreateTable(
                name: "SubscriptionPlanChanges",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubscriptionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousProductId = table.Column<int>(type: "integer", nullable: true),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    ChangedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InvoiceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Refunded = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionPlanChanges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionPlanChanges_Product_PreviousProductId",
                        column: x => x.PreviousProductId,
                        principalTable: "Product",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubscriptionPlanChanges_Product_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Product",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_OwnerShip_UserId_SubscriptionId",
                table: "OwnerShip",
                columns: new[] { "UserId", "SubscriptionId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_OwnerShip_UserId",
                table: "OwnerShip");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPlanChanges_InvoiceId",
                table: "SubscriptionPlanChanges",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPlanChanges_PreviousProductId",
                table: "SubscriptionPlanChanges",
                column: "PreviousProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPlanChanges_ProductId",
                table: "SubscriptionPlanChanges",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionPlanChanges_SubscriptionId_ChangedAt",
                table: "SubscriptionPlanChanges",
                columns: new[] { "SubscriptionId", "ChangedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RefundedSubscriptionInvoices");

            migrationBuilder.DropTable(
                name: "SubscriptionPlanChanges");

            migrationBuilder.CreateIndex(
                name: "IX_OwnerShip_UserId",
                table: "OwnerShip",
                column: "UserId");

            migrationBuilder.DropIndex(
                name: "IX_OwnerShip_UserId_SubscriptionId",
                table: "OwnerShip");

            migrationBuilder.DropColumn(
                name: "ChangeLockUntil",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderVariantId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                table: "OwnerShip");

            migrationBuilder.DropColumn(
                name: "SubscriptionId",
                table: "FiniteTransactions");
        }
    }
}
