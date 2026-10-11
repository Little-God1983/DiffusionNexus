using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddCatalogUpdateSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ApplyCatalogUpdatesAutomatically",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CatalogChannel",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 16,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApplyCatalogUpdatesAutomatically",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "CatalogChannel",
                table: "AppSettings");
        }
    }
}
