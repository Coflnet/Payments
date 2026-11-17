using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class DirectSubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CheckoutCountry",
                table: "Subscriptions",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_CheckoutCountry_Status",
                table: "Subscriptions",
                columns: new[] { "CheckoutCountry", "Status" });

            // Partial: only reversal rows (new with this release) are unique; sale rows of earlier releases may hold duplicates.
            migrationBuilder.CreateIndex(
                name: "UX_CreatorCodeRevenues_Reversal",
                table: "CreatorCodeRevenues",
                column: "TransactionReference",
                unique: true,
                filter: "\"TransactionReference\" LIKE '%#refund:%'");

            migrationBuilder.AddColumn<string>(
                name: "CreatorCode",
                table: "Subscriptions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "Subscriptions",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscountCode",
                table: "Subscriptions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManagedPayments",
                table: "Subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PriceCents",
                table: "Subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderPlanId",
                table: "Subscriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "RecurringAmount",
                table: "PaymentRequests",
                type: "numeric",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Country",
                table: "PaymentRequests",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CreatorCode",
                table: "PaymentRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DiscountCode",
                table: "PaymentRequests",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ManagedPayments",
                table: "PaymentRequests",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "DiscountCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AmountType = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Amount = table.Column<decimal>(type: "numeric", nullable: false),
                    PeriodAmounts = table.Column<string>(type: "text", nullable: true),
                    AutoApply = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SubscriptionOnly = table.Column<bool>(type: "boolean", nullable: false),
                    Duration = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    MaxRedemptions = table.Column<int>(type: "integer", nullable: true),
                    TimesRedeemed = table.Column<int>(type: "integer", nullable: false),
                    StartsAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscountCodes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PayPalPlans",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BillingWeeks = table.Column<int>(type: "integer", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PlanId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PayPalProductId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PayPalPlans", x => x.Id);
                });

            // Rows written before providers were recorded are Lemon Squeezy subscriptions.
            migrationBuilder.Sql(@"UPDATE ""Subscriptions"" SET ""ProviderSlug"" = 'lemonsqueezy' WHERE ""ProviderSlug"" IS NULL OR ""ProviderSlug"" = '';");
            // Rows of one provider subscription that belong to different users need a human decision: stop here.
            migrationBuilder.Sql(@"DO $$ BEGIN
IF EXISTS (SELECT 1 FROM ""Subscriptions"" s JOIN ""Subscriptions"" o
    ON s.""ProviderSlug"" = o.""ProviderSlug"" AND s.""ExternalId"" = o.""ExternalId"" AND s.""UserId"" <> o.""UserId"") THEN
  RAISE EXCEPTION 'Subscriptions: one provider subscription is recorded for several users; resolve before migrating';
END IF; END $$;");
            // Keep the newest row of a user's duplicated subscription, as the subscription list already did on read.
            // Down() can not restore these rows.
            migrationBuilder.Sql(@"DELETE FROM ""Subscriptions"" s USING ""Subscriptions"" newer
WHERE s.""ProviderSlug"" = newer.""ProviderSlug"" AND s.""ExternalId"" = newer.""ExternalId"" AND s.""UserId"" = newer.""UserId""
  AND (s.""UpdatedAt"" < newer.""UpdatedAt"" OR (s.""UpdatedAt"" = newer.""UpdatedAt"" AND s.""Id"" < newer.""Id""));");

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ProviderSlug_ExternalId",
                table: "Subscriptions",
                columns: new[] { "ProviderSlug", "ExternalId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DiscountCodes_Code",
                table: "DiscountCodes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PayPalPlans_ProductSlug_BillingWeeks_Currency",
                table: "PayPalPlans",
                columns: new[] { "ProductSlug", "BillingWeeks", "Currency" },
                unique: true);

            migrationBuilder.AddColumn<int>(
                name: "PaymentRequestId",
                table: "ServicePerformanceDeclarations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BillingCountry",
                table: "PaymentRecords",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CheckoutCountry",
                table: "PaymentRecords",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentMethodCountry",
                table: "PaymentRecords",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UX_CreatorCodeRevenues_Reversal",
                table: "CreatorCodeRevenues");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_CheckoutCountry_Status",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "CheckoutCountry",
                table: "Subscriptions");

            migrationBuilder.DropTable(
                name: "DiscountCodes");

            migrationBuilder.DropTable(
                name: "PayPalPlans");

            migrationBuilder.DropColumn(
                name: "RecurringAmount",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "Country",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "CreatorCode",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "DiscountCode",
                table: "PaymentRequests");

            migrationBuilder.DropColumn(
                name: "ManagedPayments",
                table: "PaymentRequests");

            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_ProviderSlug_ExternalId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "CreatorCode",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "DiscountCode",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ManagedPayments",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PriceCents",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderPlanId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PaymentRequestId",
                table: "ServicePerformanceDeclarations");

            migrationBuilder.DropColumn(
                name: "BillingCountry",
                table: "PaymentRecords");

            migrationBuilder.DropColumn(
                name: "CheckoutCountry",
                table: "PaymentRecords");

            migrationBuilder.DropColumn(
                name: "PaymentMethodCountry",
                table: "PaymentRecords");
        }
    }
}
