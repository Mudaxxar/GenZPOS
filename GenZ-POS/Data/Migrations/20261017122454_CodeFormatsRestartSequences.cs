using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace GenZPOS.Data.Migrations
{
    /// <inheritdoc />
    public partial class CodeFormatsRestartSequences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // New code formats (SH000001 barcodes, ART000001 article nos.): start both counters again at 1.
            // Numbers already taken by a product are skipped by ProductCodes, so nothing can be duplicated.
            migrationBuilder.Sql("ALTER SEQUENCE dbo.[BarcodeSeq] RESTART WITH 1;");
            migrationBuilder.Sql("ALTER SEQUENCE dbo.[ArticleNoSeq] RESTART WITH 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {

        }
    }
}
