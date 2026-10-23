using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenZPOS.Data.Migrations
{
    /// <summary>
    /// The product is only the master now; what is bought, sold and stocked is a stock item. ProductVariants is renamed
    /// to StockItems (same rows, same Ids) and lines / movements' VariantId to StockItemId. The article no. moves from
    /// the product onto each of its stock items (they keep the no. their product had).
    /// </summary>
    public partial class StockItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_PurchaseLines_ProductVariants_VariantId", table: "PurchaseLines");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseReturnLines_ProductVariants_VariantId", table: "PurchaseReturnLines");
            migrationBuilder.DropForeignKey(name: "FK_SaleLines_ProductVariants_VariantId", table: "SaleLines");
            migrationBuilder.DropForeignKey(name: "FK_SaleReturnLines_ProductVariants_VariantId", table: "SaleReturnLines");
            migrationBuilder.DropForeignKey(name: "FK_StockMovements_ProductVariants_VariantId", table: "StockMovements");
            migrationBuilder.DropForeignKey(name: "FK_ProductVariants_Products_ProductId", table: "ProductVariants");
            migrationBuilder.DropPrimaryKey(name: "PK_ProductVariants", table: "ProductVariants");
            migrationBuilder.DropIndex(name: "IX_ProductVariants_ProductId_Colour_Size", table: "ProductVariants");

            migrationBuilder.RenameTable(name: "ProductVariants", newName: "StockItems");
            migrationBuilder.RenameIndex(name: "IX_ProductVariants_Barcode", table: "StockItems", newName: "IX_StockItems_Barcode");
            migrationBuilder.AddPrimaryKey(name: "PK_StockItems", table: "StockItems", column: "Id");

            // Article no.: from the product onto each of its stock items.
            migrationBuilder.AddColumn<string>(
                name: "ArticleNo", table: "StockItems", type: "nvarchar(50)", maxLength: 50, nullable: false, defaultValue: "");
            migrationBuilder.Sql("UPDATE i SET ArticleNo = p.ArticleNo FROM StockItems i JOIN Products p ON p.Id = i.ProductId;");
            migrationBuilder.DropIndex(name: "IX_Products_ArticleNo", table: "Products");
            migrationBuilder.DropColumn(name: "ArticleNo", table: "Products");

            // Lines and movements keep their values (same stock item Ids); only the column is renamed.
            migrationBuilder.RenameColumn(name: "VariantId", table: "StockMovements", newName: "StockItemId");
            migrationBuilder.RenameIndex(name: "IX_StockMovements_VariantId", table: "StockMovements", newName: "IX_StockMovements_StockItemId");
            migrationBuilder.RenameColumn(name: "VariantId", table: "SaleReturnLines", newName: "StockItemId");
            migrationBuilder.RenameIndex(name: "IX_SaleReturnLines_VariantId", table: "SaleReturnLines", newName: "IX_SaleReturnLines_StockItemId");
            migrationBuilder.RenameColumn(name: "VariantId", table: "SaleLines", newName: "StockItemId");
            migrationBuilder.RenameIndex(name: "IX_SaleLines_VariantId", table: "SaleLines", newName: "IX_SaleLines_StockItemId");
            migrationBuilder.RenameColumn(name: "VariantId", table: "PurchaseReturnLines", newName: "StockItemId");
            migrationBuilder.RenameIndex(name: "IX_PurchaseReturnLines_VariantId", table: "PurchaseReturnLines", newName: "IX_PurchaseReturnLines_StockItemId");
            migrationBuilder.RenameColumn(name: "VariantId", table: "PurchaseLines", newName: "StockItemId");
            migrationBuilder.RenameIndex(name: "IX_PurchaseLines_VariantId", table: "PurchaseLines", newName: "IX_PurchaseLines_StockItemId");

            migrationBuilder.CreateIndex(name: "IX_StockItems_ArticleNo", table: "StockItems", column: "ArticleNo");
            migrationBuilder.CreateIndex(name: "IX_StockItems_ProductId_ArticleNo_Colour_Size", table: "StockItems",
                columns: new[] { "ProductId", "ArticleNo", "Colour", "Size" });

            migrationBuilder.AddForeignKey(name: "FK_StockItems_Products_ProductId", table: "StockItems", column: "ProductId",
                principalTable: "Products", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_PurchaseLines_StockItems_StockItemId", table: "PurchaseLines", column: "StockItemId",
                principalTable: "StockItems", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_PurchaseReturnLines_StockItems_StockItemId", table: "PurchaseReturnLines", column: "StockItemId",
                principalTable: "StockItems", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_SaleLines_StockItems_StockItemId", table: "SaleLines", column: "StockItemId",
                principalTable: "StockItems", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_SaleReturnLines_StockItems_StockItemId", table: "SaleReturnLines", column: "StockItemId",
                principalTable: "StockItems", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_StockMovements_StockItems_StockItemId", table: "StockMovements", column: "StockItemId",
                principalTable: "StockItems", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A product's stock items may now carry several article nos., which can't be folded back into one.
            // The app takes a backup before upgrading (App.xaml.cs): restore that instead.
            throw new NotSupportedException("The StockItems migration can't be undone. Restore the backup taken before the upgrade.");
        }
    }
}
