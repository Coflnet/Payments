using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class GooglePlaySubscriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProviderToken",
                table: "Subscriptions",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProviderToken",
                table: "Subscriptions");
        }
    }
}
