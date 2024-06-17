using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class LicensesIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Licenses_UserId_TargetId",
                table: "Licenses");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_UserId_TargetId_ProductId",
                table: "Licenses",
                columns: new[] { "UserId", "TargetId", "ProductId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Licenses_UserId_TargetId_ProductId",
                table: "Licenses");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_UserId_TargetId",
                table: "Licenses",
                columns: new[] { "UserId", "TargetId" },
                unique: true);
        }
    }
}
