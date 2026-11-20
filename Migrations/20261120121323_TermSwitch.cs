using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class TermSwitch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SubscriptionTermSwitches",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SubscriptionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Provider = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    UserId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    NewSubscriptionId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousProductId = table.Column<int>(type: "integer", nullable: true),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    RenewalProductId = table.Column<int>(type: "integer", nullable: true),
                    Weeks = table.Column<int>(type: "integer", nullable: false),
                    RegularCents = table.Column<long>(type: "bigint", nullable: false),
                    ChargedCents = table.Column<long>(type: "bigint", nullable: false),
                    RenewalCents = table.Column<long>(type: "bigint", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    PromotionCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    AccessEndsBefore = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    InvoiceId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PaymentUrl = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    PaymentRequestId = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SubscriptionTermSwitches", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SubscriptionTermSwitches_Product_PreviousProductId",
                        column: x => x.PreviousProductId,
                        principalTable: "Product",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubscriptionTermSwitches_Product_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Product",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SubscriptionTermSwitches_Product_RenewalProductId",
                        column: x => x.RenewalProductId,
                        principalTable: "Product",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermSwitches_InvoiceId",
                table: "SubscriptionTermSwitches",
                column: "InvoiceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermSwitches_NewSubscriptionId",
                table: "SubscriptionTermSwitches",
                column: "NewSubscriptionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermSwitches_PreviousProductId",
                table: "SubscriptionTermSwitches",
                column: "PreviousProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermSwitches_ProductId",
                table: "SubscriptionTermSwitches",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermSwitches_RenewalProductId",
                table: "SubscriptionTermSwitches",
                column: "RenewalProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SubscriptionTermSwitches_SubscriptionId_Status",
                table: "SubscriptionTermSwitches",
                columns: new[] { "SubscriptionId", "Status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SubscriptionTermSwitches");
        }
    }
}
