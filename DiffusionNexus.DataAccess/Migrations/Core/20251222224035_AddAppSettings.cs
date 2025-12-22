using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddAppSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    EncryptedCivitaiApiKey = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    ShowNsfw = table.Column<bool>(type: "INTEGER", nullable: false),
                    GenerateVideoThumbnails = table.Column<bool>(type: "INTEGER", nullable: false),
                    ShowVideoPreview = table.Column<bool>(type: "INTEGER", nullable: false),
                    UseForgeStylePrompts = table.Column<bool>(type: "INTEGER", nullable: false),
                    MergeLoraSources = table.Column<bool>(type: "INTEGER", nullable: false),
                    LoraSortSourcePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    LoraSortTargetPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    DeleteEmptySourceFolders = table.Column<bool>(type: "INTEGER", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LoraSources",
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
                    table.PrimaryKey("PK_LoraSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LoraSources_AppSettings_AppSettingsId",
                        column: x => x.AppSettingsId,
                        principalTable: "AppSettings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoraSources_AppSettingsId",
                table: "LoraSources",
                column: "AppSettingsId");

            migrationBuilder.CreateIndex(
                name: "IX_LoraSources_FolderPath",
                table: "LoraSources",
                column: "FolderPath");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoraSources");

            migrationBuilder.DropTable(
                name: "AppSettings");
        }
    }
}
