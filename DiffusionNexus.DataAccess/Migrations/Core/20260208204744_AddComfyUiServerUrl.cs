using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddComfyUiServerUrl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComfyUiServerUrl",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 2000,
                nullable: false,
                defaultValue: "http://127.0.0.1:8188/");

            migrationBuilder.Sql(
                "CREATE INDEX IF NOT EXISTS IX_ImageGalleries_FolderPath ON ImageGalleries (FolderPath);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ImageGalleries_FolderPath",
                table: "ImageGalleries");

            migrationBuilder.DropColumn(
                name: "ComfyUiServerUrl",
                table: "AppSettings");
        }
    }
}
