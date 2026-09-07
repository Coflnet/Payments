using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class OwnerManagedTierSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SlotCount",
                table: "Product",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "SlotTier",
                table: "Product",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TierSlots",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    Tier = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Expires = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AssignedUserId = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    MinecraftUuid = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierSlots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TierSlots_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TierSlotGrants",
                columns: table => new
                {
                    TierSlotId = table.Column<long>(type: "bigint", nullable: false),
                    TransactionId = table.Column<long>(type: "bigint", nullable: false),
                    Seconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TierSlotGrants", x => new { x.TransactionId, x.TierSlotId });
                    table.ForeignKey(
                        name: "FK_TierSlotGrants_FiniteTransactions_TransactionId",
                        column: x => x.TransactionId,
                        principalTable: "FiniteTransactions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_TierSlotGrants_TierSlots_TierSlotId",
                        column: x => x.TierSlotId,
                        principalTable: "TierSlots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TierSlotGrants_TierSlotId",
                table: "TierSlotGrants",
                column: "TierSlotId");

            migrationBuilder.CreateIndex(
                name: "IX_TierSlots_AssignedUserId_MinecraftUuid_Expires",
                table: "TierSlots",
                columns: new[] { "AssignedUserId", "MinecraftUuid", "Expires" });

            migrationBuilder.CreateIndex(
                name: "IX_TierSlots_MinecraftUuid_Expires",
                table: "TierSlots",
                columns: new[] { "MinecraftUuid", "Expires" });

            migrationBuilder.CreateIndex(
                name: "IX_TierSlots_UserId",
                table: "TierSlots",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TierSlotGrants");

            migrationBuilder.DropTable(
                name: "TierSlots");

            migrationBuilder.DropColumn(
                name: "SlotCount",
                table: "Product");

            migrationBuilder.DropColumn(
                name: "SlotTier",
                table: "Product");

        }
    }
}
