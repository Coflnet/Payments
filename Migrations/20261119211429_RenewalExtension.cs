using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class RenewalExtension : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ContinuationRefusedAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ExtensionPriceCents",
                table: "Subscriptions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExtensionStartsAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtensionSubscriptionId",
                table: "Subscriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PreviousSubscriptionId",
                table: "Subscriptions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RenewalReminderSentFor",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExtendsSubscriptionId",
                table: "PaymentRequests",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Subscriptions_ExtensionSubscriptionId",
                table: "Subscriptions",
                column: "ExtensionSubscriptionId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Subscriptions_ExtensionSubscriptionId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ContinuationRefusedAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ExtensionPriceCents",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ExtensionStartsAt",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ExtensionSubscriptionId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "PreviousSubscriptionId",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "RenewalReminderSentFor",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ExtendsSubscriptionId",
                table: "PaymentRequests");
        }
    }
}
