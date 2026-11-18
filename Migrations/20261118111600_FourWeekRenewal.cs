using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class FourWeekRenewal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PayPalPlans_ProductSlug_BillingWeeks_Currency",
                table: "PayPalPlans");

            migrationBuilder.AddColumn<long>(
                name: "RenewalPriceCents",
                table: "Subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RenewalProductId",
                table: "Subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RenewalScheduleId",
                table: "Subscriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RenewalWeeks",
                table: "PayPalPlans",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // The plans made so far repeat their billing period (13 and 52 week plans included)
            migrationBuilder.Sql("UPDATE \"PayPalPlans\" SET \"RenewalWeeks\" = \"BillingWeeks\" WHERE \"RenewalWeeks\" = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_RenewalProductId",
                table: "Subscriptions",
                column: "RenewalProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PayPalPlans_ProductSlug_BillingWeeks_RenewalWeeks_Currency",
                table: "PayPalPlans",
                columns: new[] { "ProductSlug", "BillingWeeks", "RenewalWeeks", "Currency" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Subscriptions_Product_RenewalProductId",
                table: "Subscriptions",
                column: "RenewalProductId",
                principalTable: "Product",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Subscriptions_Product_RenewalProductId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_RenewalProductId",
                table: "Subscriptions");

            migrationBuilder.DropIndex(
                name: "IX_PayPalPlans_ProductSlug_BillingWeeks_RenewalWeeks_Currency",
                table: "PayPalPlans");

            migrationBuilder.DropColumn(
                name: "RenewalPriceCents",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RenewalProductId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RenewalScheduleId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RenewalWeeks",
                table: "PayPalPlans");

            migrationBuilder.CreateIndex(
                name: "IX_PayPalPlans_ProductSlug_BillingWeeks_Currency",
                table: "PayPalPlans",
                columns: new[] { "ProductSlug", "BillingWeeks", "Currency" },
                unique: true);
        }
    }
}
