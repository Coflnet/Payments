using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class PaymentRequestCoinAmount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CoinAmount",
                table: "PaymentRequests",
                type: "numeric",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CoinAmount",
                table: "PaymentRequests");
        }
    }
}
