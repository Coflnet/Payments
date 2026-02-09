using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class PaymentRecordsForTaxCompliance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PaymentRecords",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ExternalUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Country = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    ZipCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    State = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    City = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    GrossAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric", nullable: false),
                    DiscountAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    TaxAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    TaxName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TaxRate = table.Column<double>(type: "double precision", nullable: false),
                    TaxRemittedByProcessor = table.Column<bool>(type: "boolean", nullable: false),
                    NetAmount = table.Column<decimal>(type: "numeric", nullable: false),
                    ProcessorFee = table.Column<decimal>(type: "numeric", nullable: false),
                    CreatorCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatorCodeDiscount = table.Column<decimal>(type: "numeric", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ExchangeRateToEur = table.Column<decimal>(type: "numeric", nullable: true),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PaymentMethod = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ExternalOrderId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ExternalTransactionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProductSlug = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    CoinAmount = table.Column<long>(type: "bigint", nullable: false),
                    PaidAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RecordedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RefundedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    BuyerEmail = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    BuyerName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Locale = table.Column<string>(type: "character varying(5)", maxLength: 5, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    IsSubscriptionPayment = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PaymentRecords_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecords_Country_PaidAt",
                table: "PaymentRecords",
                columns: new[] { "Country", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecords_Country_Provider_PaidAt",
                table: "PaymentRecords",
                columns: new[] { "Country", "Provider", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecords_ExternalOrderId",
                table: "PaymentRecords",
                column: "ExternalOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecords_ExternalUserId",
                table: "PaymentRecords",
                column: "ExternalUserId");

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecords_Provider_PaidAt",
                table: "PaymentRecords",
                columns: new[] { "Provider", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecords_Status_PaidAt",
                table: "PaymentRecords",
                columns: new[] { "Status", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRecords_UserId",
                table: "PaymentRecords",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PaymentRecords");
        }
    }
}
