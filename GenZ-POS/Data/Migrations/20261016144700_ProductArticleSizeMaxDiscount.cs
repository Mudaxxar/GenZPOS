using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenZPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class ProductArticleSizeMaxDiscount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArticleNo",
                table: "Products",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MaxDiscountPercent",
                table: "Products",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Size",
                table: "Products",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_ArticleNo",
                table: "Products",
                column: "ArticleNo");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_ArticleNo",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ArticleNo",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "MaxDiscountPercent",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "Size",
                table: "Products");
        }
    }
}
