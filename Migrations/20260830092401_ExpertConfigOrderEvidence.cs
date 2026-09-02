using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class ExpertConfigOrderEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "GrossEurCents",
                table: "ServicePerformanceDeclarations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "OrderDetailsJson",
                table: "ServicePerformanceDeclarations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TaxCountry",
                table: "ServicePerformanceDeclarations",
                type: "character varying(2)",
                maxLength: 2,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "VatEurCents",
                table: "ServicePerformanceDeclarations",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "VatRateBasisPoints",
                table: "ServicePerformanceDeclarations",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GrossEurCents",
                table: "ServicePerformanceDeclarations");

            migrationBuilder.DropColumn(
                name: "OrderDetailsJson",
                table: "ServicePerformanceDeclarations");

            migrationBuilder.DropColumn(
                name: "TaxCountry",
                table: "ServicePerformanceDeclarations");

            migrationBuilder.DropColumn(
                name: "VatEurCents",
                table: "ServicePerformanceDeclarations");

            migrationBuilder.DropColumn(
                name: "VatRateBasisPoints",
                table: "ServicePerformanceDeclarations");

        }
    }
}
