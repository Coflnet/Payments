using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class BeforeLicenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FiniteTransactions_Users_UserId",
                table: "FiniteTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_OwnerShip_Users_UserId",
                table: "OwnerShip");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Users_UserId",
                table: "PaymentRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_PlanedTransactions_Users_UserId",
                table: "PlanedTransactions");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "PlanedTransactions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "PaymentRequests",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "OwnerShip",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "FiniteTransactions",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

    /*        migrationBuilder.AddForeignKey(
                name: "FK_FiniteTransactions_Users_UserId",
                table: "FiniteTransactions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_OwnerShip_Users_UserId",
                table: "OwnerShip",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequests_Users_UserId",
                table: "PaymentRequests",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_PlanedTransactions_Users_UserId",
                table: "PlanedTransactions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id");*/
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_FiniteTransactions_Users_UserId",
                table: "FiniteTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_OwnerShip_Users_UserId",
                table: "OwnerShip");

            migrationBuilder.DropForeignKey(
                name: "FK_PaymentRequests_Users_UserId",
                table: "PaymentRequests");

            migrationBuilder.DropForeignKey(
                name: "FK_PlanedTransactions_Users_UserId",
                table: "PlanedTransactions");

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "PlanedTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "PaymentRequests",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "OwnerShip",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "UserId",
                table: "FiniteTransactions",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_FiniteTransactions_Users_UserId",
                table: "FiniteTransactions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_OwnerShip_Users_UserId",
                table: "OwnerShip",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PaymentRequests_Users_UserId",
                table: "PaymentRequests",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_PlanedTransactions_Users_UserId",
                table: "PlanedTransactions",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }
    }
}
