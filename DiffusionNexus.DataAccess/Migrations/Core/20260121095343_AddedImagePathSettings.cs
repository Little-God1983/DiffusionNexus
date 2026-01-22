using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddedImagePathSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImageGalleries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    AppSettingsId = table.Column<int>(type: "INTEGER", nullable: false),
                    FolderPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    IsEnabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageGalleries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageGalleries_AppSettings_AppSettingsId",
                        column: x => x.AppSettingsId,
                        principalTable: "AppSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageGalleries_AppSettingsId",
                table: "ImageGalleries",
                column: "AppSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageGalleries_FolderPath",
                table: "ImageGalleries",
                column: "FolderPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageGalleries");
        }
    }
}
