using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenZPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductCodesUniqueBarcode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_Barcode",
                table: "Products");

            migrationBuilder.CreateSequence(
                name: "ArticleNoSeq");

            migrationBuilder.CreateSequence(
                name: "BarcodeSeq");

            // Older data may have the same barcode on two products: keep it on the oldest, clear it on the others
            // (they can get a new one with "Generate" in the product dialog), so the unique index can be created.
            migrationBuilder.Sql("""
                WITH d AS (SELECT Id, ROW_NUMBER() OVER (PARTITION BY LTRIM(RTRIM(Barcode)) ORDER BY Id) AS rn
                           FROM Products WHERE Barcode IS NOT NULL)
                UPDATE Products SET Barcode = NULL WHERE Id IN (SELECT Id FROM d WHERE rn > 1);
                UPDATE Products SET Barcode = NULL WHERE LTRIM(RTRIM(Barcode)) = '';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode",
                unique: true,
                filter: "[Barcode] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_Barcode",
                table: "Products");

            migrationBuilder.DropSequence(
                name: "ArticleNoSeq");

            migrationBuilder.DropSequence(
                name: "BarcodeSeq");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Barcode",
                table: "Products",
                column: "Barcode");
        }
    }
}
