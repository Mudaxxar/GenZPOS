using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenZPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReturnsCostSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "Cost",
                table: "SaleLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // Backfill: existing sale lines get the product's current purchase price (best available for old data).
            migrationBuilder.Sql("UPDATE l SET Cost = p.PurchasePrice FROM SaleLines l JOIN Products p ON p.Id = l.ProductId;");

            migrationBuilder.CreateTable(
                name: "PurchaseReturns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReturnDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SupplierId = table.Column<int>(type: "int", nullable: false),
                    PurchaseId = table.Column<int>(type: "int", nullable: true),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Received = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReturns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseReturns_Purchases_PurchaseId",
                        column: x => x.PurchaseId,
                        principalTable: "Purchases",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReturns_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleReturns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ReturnDate = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CustomerId = table.Column<int>(type: "int", nullable: false),
                    SaleId = table.Column<int>(type: "int", nullable: true),
                    Total = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Refunded = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Notes = table.Column<string>(type: "nvarchar(250)", maxLength: 250, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleReturns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleReturns_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleReturns_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Settings",
                columns: table => new
                {
                    Key = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    Value = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Settings", x => x.Key);
                });

            migrationBuilder.CreateTable(
                name: "PurchaseReturnLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    PurchaseReturnId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PurchaseReturnLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PurchaseReturnLines_PurchaseReturns_PurchaseReturnId",
                        column: x => x.PurchaseReturnId,
                        principalTable: "PurchaseReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SaleReturnLines",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    SaleReturnId = table.Column<int>(type: "int", nullable: false),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Qty = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Rate = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    Cost = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleReturnLines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleReturnLines_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleReturnLines_SaleReturns_SaleReturnId",
                        column: x => x.SaleReturnId,
                        principalTable: "SaleReturns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_ProductId",
                table: "PurchaseReturnLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturnLines_PurchaseReturnId",
                table: "PurchaseReturnLines",
                column: "PurchaseReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_PurchaseId",
                table: "PurchaseReturns",
                column: "PurchaseId");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_ReturnDate",
                table: "PurchaseReturns",
                column: "ReturnDate");

            migrationBuilder.CreateIndex(
                name: "IX_PurchaseReturns_SupplierId_ReturnDate",
                table: "PurchaseReturns",
                columns: new[] { "SupplierId", "ReturnDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturnLines_ProductId",
                table: "SaleReturnLines",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturnLines_SaleReturnId",
                table: "SaleReturnLines",
                column: "SaleReturnId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_CustomerId_ReturnDate",
                table: "SaleReturns",
                columns: new[] { "CustomerId", "ReturnDate" });

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_ReturnDate",
                table: "SaleReturns",
                column: "ReturnDate");

            migrationBuilder.CreateIndex(
                name: "IX_SaleReturns_SaleId",
                table: "SaleReturns",
                column: "SaleId");

            // Default shop name for invoices (owner edits it on the Settings page).
            migrationBuilder.Sql("IF NOT EXISTS (SELECT 1 FROM Settings WHERE [Key] = 'Shop.Name') INSERT INTO Settings ([Key], [Value]) VALUES ('Shop.Name', 'GenZ-POS');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PurchaseReturnLines");

            migrationBuilder.DropTable(
                name: "SaleReturnLines");

            migrationBuilder.DropTable(
                name: "Settings");

            migrationBuilder.DropTable(
                name: "PurchaseReturns");

            migrationBuilder.DropTable(
                name: "SaleReturns");

            migrationBuilder.DropColumn(
                name: "Cost",
                table: "SaleLines");
        }
    }
}
