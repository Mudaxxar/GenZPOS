using GenZPOS.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenZPOS.Data.Migrations
{
    /// <summary>
    /// Exchange on the New Sale screen (owner 2026-10-23): a sales return can be saved together with a new invoice;
    /// <c>SaleReturns.ExchangeSaleId</c> links it to that invoice. Nullable, no data change. (Written by hand while the
    /// app was running; the model snapshot was updated to match.)
    /// </summary>
    [DbContext(typeof(AppDbContext))]
    [Migration("20261023120000_SaleExchange")]
    public partial class SaleExchange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ExchangeSaleId",
                table: "SaleReturns",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_ExchangeSaleId",
                table: "SaleReturns",
                column: "ExchangeSaleId");

            migrationBuilder.AddForeignKey(
                name: "FK_SaleReturns_Sales_ExchangeSaleId",
                table: "SaleReturns",
                column: "ExchangeSaleId",
                principalTable: "Sales",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_SaleReturns_Sales_ExchangeSaleId",
                table: "SaleReturns");

            migrationBuilder.DropIndex(
                name: "IX_SaleReturns_ExchangeSaleId",
                table: "SaleReturns");

            migrationBuilder.DropColumn(
                name: "ExchangeSaleId",
                table: "SaleReturns");
        }
    }
}
