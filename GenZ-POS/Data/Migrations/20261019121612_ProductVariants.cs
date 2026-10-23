using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenZPOS.Data.Migrations
{
    /// <summary>
    /// One product = one article; its colours / sizes become <c>ProductVariants</c> (own barcode, prices, stock).
    /// Every old product row becomes a variant WITH THE SAME ID, so purchase / sale / return lines and stock movements
    /// keep pointing at the same item (their ProductId column is only renamed to VariantId). Old rows sharing an article
    /// no. become one product (the lowest Id); rows without one are grouped by company + name + pack (the old "family"
    /// rule) and get a new ART… no. Rows without a barcode get the next SH… no.
    /// </summary>
    public partial class ProductVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(name: "FK_PurchaseLines_Products_ProductId", table: "PurchaseLines");
            migrationBuilder.DropForeignKey(name: "FK_PurchaseReturnLines_Products_ProductId", table: "PurchaseReturnLines");
            migrationBuilder.DropForeignKey(name: "FK_SaleLines_Products_ProductId", table: "SaleLines");
            migrationBuilder.DropForeignKey(name: "FK_SaleReturnLines_Products_ProductId", table: "SaleReturnLines");
            migrationBuilder.DropForeignKey(name: "FK_StockMovements_Products_ProductId", table: "StockMovements");

            migrationBuilder.DropIndex(name: "IX_Products_ArticleNo", table: "Products");
            migrationBuilder.DropIndex(name: "IX_Products_Barcode", table: "Products");

            migrationBuilder.CreateTable(
                name: "ProductVariants",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    ProductId = table.Column<int>(type: "int", nullable: false),
                    Colour = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    Size = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: true),
                    Barcode = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    PurchasePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    SalePrice = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    ReorderLevel = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductVariants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductVariants_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            // 1. Every row gets a barcode (SH000001… from BarcodeSeq, skipping numbers already used; same format as ProductCodes).
            migrationBuilder.Sql("""
                DECLARE @id int, @n bigint, @code nvarchar(50);
                UPDATE Products SET Barcode = NULL WHERE LTRIM(RTRIM(Barcode)) = '';
                WHILE EXISTS (SELECT 1 FROM Products WHERE Barcode IS NULL)
                BEGIN
                    SELECT TOP 1 @id = Id FROM Products WHERE Barcode IS NULL ORDER BY Id;
                    SET @code = NULL;
                    WHILE @code IS NULL OR EXISTS (SELECT 1 FROM Products WHERE Barcode = @code)
                    BEGIN
                        SET @n = NEXT VALUE FOR dbo.[BarcodeSeq];
                        SET @code = N'SH' + CASE WHEN @n < 1000000 THEN RIGHT(N'000000' + CAST(@n AS nvarchar(20)), 6) ELSE CAST(@n AS nvarchar(20)) END;
                    END
                    UPDATE Products SET Barcode = @code WHERE Id = @id;
                END
                """);

            // 2. Rows without an article no.: one new ART… no. per company + name + pack (the old "family" of a legacy product).
            migrationBuilder.Sql("""
                DECLARE @id int, @n bigint, @code nvarchar(50);
                UPDATE Products SET ArticleNo = NULL WHERE LTRIM(RTRIM(ArticleNo)) = '';
                WHILE EXISTS (SELECT 1 FROM Products WHERE ArticleNo IS NULL)
                BEGIN
                    SELECT TOP 1 @id = Id FROM Products WHERE ArticleNo IS NULL ORDER BY Id;
                    SET @code = NULL;
                    WHILE @code IS NULL OR EXISTS (SELECT 1 FROM Products WHERE ArticleNo = @code)
                    BEGIN
                        SET @n = NEXT VALUE FOR dbo.[ArticleNoSeq];
                        SET @code = N'ART' + CASE WHEN @n < 1000000 THEN RIGHT(N'000000' + CAST(@n AS nvarchar(20)), 6) ELSE CAST(@n AS nvarchar(20)) END;
                    END
                    UPDATE p SET ArticleNo = @code
                    FROM Products p JOIN Products f ON f.Id = @id
                    WHERE p.ArticleNo IS NULL AND p.CompanyId = f.CompanyId AND p.Name = f.Name
                      AND ISNULL(p.PackSize, N'') = ISNULL(f.PackSize, N'');
                END
                """);

            // 3. Each old row → a variant with the same Id, under the article's first row (lowest Id).
            migrationBuilder.Sql("""
                SET IDENTITY_INSERT ProductVariants ON;
                INSERT INTO ProductVariants (Id, ProductId, Colour, Size, Barcode, PurchasePrice, SalePrice, ReorderLevel, IsActive)
                SELECT p.Id, a.FirstId, NULLIF(LTRIM(RTRIM(p.Shade)), N''), NULLIF(LTRIM(RTRIM(p.Size)), N''), p.Barcode,
                       p.PurchasePrice, p.SalePrice, p.ReorderLevel, p.IsActive
                FROM Products p
                JOIN (SELECT ArticleNo, MIN(Id) AS FirstId FROM Products GROUP BY ArticleNo) a ON a.ArticleNo = p.ArticleNo;
                SET IDENTITY_INSERT ProductVariants OFF;
                """);

            // 4. The article stays active if any of its rows was; the other rows of an article go (their data is in the variants).
            migrationBuilder.Sql("""
                UPDATE p SET IsActive = 1
                FROM Products p
                WHERE EXISTS (SELECT 1 FROM ProductVariants v WHERE v.ProductId = p.Id AND v.IsActive = 1);
                DELETE p FROM Products p WHERE NOT EXISTS (SELECT 1 FROM ProductVariants v WHERE v.ProductId = p.Id);
                """);

            migrationBuilder.DropColumn(name: "Barcode", table: "Products");
            migrationBuilder.DropColumn(name: "PurchasePrice", table: "Products");
            migrationBuilder.DropColumn(name: "ReorderLevel", table: "Products");
            migrationBuilder.DropColumn(name: "SalePrice", table: "Products");
            migrationBuilder.DropColumn(name: "Shade", table: "Products");
            migrationBuilder.DropColumn(name: "Size", table: "Products");

            // Lines and movements keep their values (variant Id = old product Id); only the column is renamed.
            migrationBuilder.RenameColumn(name: "ProductId", table: "StockMovements", newName: "VariantId");
            migrationBuilder.RenameIndex(name: "IX_StockMovements_ProductId", table: "StockMovements", newName: "IX_StockMovements_VariantId");
            migrationBuilder.RenameColumn(name: "ProductId", table: "SaleReturnLines", newName: "VariantId");
            migrationBuilder.RenameIndex(name: "IX_SaleReturnLines_ProductId", table: "SaleReturnLines", newName: "IX_SaleReturnLines_VariantId");
            migrationBuilder.RenameColumn(name: "ProductId", table: "SaleLines", newName: "VariantId");
            migrationBuilder.RenameIndex(name: "IX_SaleLines_ProductId", table: "SaleLines", newName: "IX_SaleLines_VariantId");
            migrationBuilder.RenameColumn(name: "ProductId", table: "PurchaseReturnLines", newName: "VariantId");
            migrationBuilder.RenameIndex(name: "IX_PurchaseReturnLines_ProductId", table: "PurchaseReturnLines", newName: "IX_PurchaseReturnLines_VariantId");
            migrationBuilder.RenameColumn(name: "ProductId", table: "PurchaseLines", newName: "VariantId");
            migrationBuilder.RenameIndex(name: "IX_PurchaseLines_ProductId", table: "PurchaseLines", newName: "IX_PurchaseLines_VariantId");

            migrationBuilder.AlterColumn<string>(
                name: "ArticleNo",
                table: "Products",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.CreateIndex(name: "IX_Products_ArticleNo", table: "Products", column: "ArticleNo", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ProductVariants_Barcode", table: "ProductVariants", column: "Barcode", unique: true);
            migrationBuilder.CreateIndex(name: "IX_ProductVariants_ProductId_Colour_Size", table: "ProductVariants", columns: new[] { "ProductId", "Colour", "Size" });

            migrationBuilder.AddForeignKey(name: "FK_PurchaseLines_ProductVariants_VariantId", table: "PurchaseLines", column: "VariantId",
                principalTable: "ProductVariants", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_PurchaseReturnLines_ProductVariants_VariantId", table: "PurchaseReturnLines", column: "VariantId",
                principalTable: "ProductVariants", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_SaleLines_ProductVariants_VariantId", table: "SaleLines", column: "VariantId",
                principalTable: "ProductVariants", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_SaleReturnLines_ProductVariants_VariantId", table: "SaleReturnLines", column: "VariantId",
                principalTable: "ProductVariants", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
            migrationBuilder.AddForeignKey(name: "FK_StockMovements_ProductVariants_VariantId", table: "StockMovements", column: "VariantId",
                principalTable: "ProductVariants", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Articles with several sizes / colours can't be folded back into one row each without inventing products.
            // The app takes a backup before upgrading (App.xaml.cs): restore that instead.
            throw new NotSupportedException("The ProductVariants migration can't be undone. Restore the backup taken before the upgrade.");
        }
    }
}
