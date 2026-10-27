using System.IO;
using GenZPOS.Data;

namespace GenZPOS.Tests;

/// <summary>Owner 2026-10-26: dotnet ef / Package Manager Console use the project's appsettings.json connection string.</summary>
public class AppConfigTests
{
    [Fact]
    public void Tools_read_the_project_appsettings_from_the_solution_or_project_folder()
    {
        var root = Path.Combine(Path.GetTempPath(), "GenZPOS-config-test-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "GenZ-POS");
        var deeper = Path.Combine(project, "Data", "Migrations");
        Directory.CreateDirectory(deeper);
        try
        {
            // No file anywhere → the built-in default.
            Assert.Equal(AppDbContext.DefaultConnectionString, AppConfig.ConnectionStringForTools(root));

            File.WriteAllText(Path.Combine(project, "appsettings.json"), """
                {
                  // a comment, like the real file
                  "ConnectionString": "Server=.\\SQLEXPRESS;Database=ShopData;Trusted_Connection=True;TrustServerCertificate=True",
                }
                """);
            // From the solution folder (GenZ-POS sub-folder), the project folder, and a folder inside it.
            foreach (var start in new[] { root, project, deeper })
                Assert.Contains(@"Server=.\SQLEXPRESS;Database=ShopData", AppConfig.ConnectionStringForTools(start));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
