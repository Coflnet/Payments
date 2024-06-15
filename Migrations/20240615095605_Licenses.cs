using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations
{
    /// <inheritdoc />
    public partial class Licenses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Licenses",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductId = table.Column<int>(type: "integer", nullable: true),
                    Expires = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TargetId = table.Column<string>(type: "text", nullable: true),
                    groupId = table.Column<int>(type: "integer", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Licenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Licenses_Groups_groupId",
                        column: x => x.groupId,
                        principalTable: "Groups",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Licenses_Product_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Product",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_Licenses_Users_UserId",
                        column: x => x.UserId,
                        principalTable: "Users",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_groupId",
                table: "Licenses",
                column: "groupId");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_ProductId",
                table: "Licenses",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_TargetId_Expires",
                table: "Licenses",
                columns: new[] { "TargetId", "Expires" });

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_UserId_TargetId",
                table: "Licenses",
                columns: new[] { "UserId", "TargetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Licenses_UserId_TargetId_Expires",
                table: "Licenses",
                columns: new[] { "UserId", "TargetId", "Expires" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Licenses");
        }
    }
}
