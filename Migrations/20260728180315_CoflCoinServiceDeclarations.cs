using System;
using Coflnet.Payments.Models;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Payments.Migrations;

[DbContext(typeof(PaymentContext))]
[Migration("20260728180315_CoflCoinServiceDeclarations")]
public partial class CoflCoinServiceDeclarations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "ServicePerformanceDeclarations",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                RequestId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                UserId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ProductId = table.Column<int>(type: "integer", nullable: false),
                ProductSlug = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Count = table.Column<int>(type: "integer", nullable: false),
                PurchaseReference = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                CoinAmount = table.Column<decimal>(type: "numeric", nullable: false),
                StartsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                EndsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                DeclarationRequired = table.Column<bool>(type: "boolean", nullable: false),
                EarlyPerformanceRequested = table.Column<bool>(type: "boolean", nullable: false),
                WithdrawalConsequenceAcknowledged = table.Column<bool>(type: "boolean", nullable: false),
                Locale = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: false),
                DeclarationVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                DeclarationText = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                DeclarationSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                AgreementId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                AgreementHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                WithdrawalVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                WithdrawalSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_ServicePerformanceDeclarations", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_ServicePerformanceDeclarations_RequestId",
            table: "ServicePerformanceDeclarations",
            column: "RequestId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_ServicePerformanceDeclarations_UserId_CreatedAtUtc",
            table: "ServicePerformanceDeclarations",
            columns: new[] { "UserId", "CreatedAtUtc" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "ServicePerformanceDeclarations");
    }
}
