using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentRequestExportIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_Provider_State_UpdatedAt",
                table: "PaymentRequests",
                columns: new[] { "Provider", "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_PaymentRequests_State_UpdatedAt",
                table: "PaymentRequests",
                columns: new[] { "State", "UpdatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_Provider_State_UpdatedAt",
                table: "PaymentRequests");

            migrationBuilder.DropIndex(
                name: "IX_PaymentRequests_State_UpdatedAt",
                table: "PaymentRequests");
        }
    }
}
