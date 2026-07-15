using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddDatabaseBackupSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "AutoBackupEnabled",
                table: "AppSettings",
                newName: "BackupDatasetImagesEnabled");

            migrationBuilder.AddColumn<bool>(
                name: "BackupDatabaseEnabled",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            // Existing users who already had automatic backup enabled (now
            // BackupDatasetImagesEnabled) start protecting their database too — this is the
            // safety gap the feature closes. Users who had backup off keep it off.
            migrationBuilder.Sql(
                "UPDATE AppSettings SET BackupDatabaseEnabled = 1 WHERE BackupDatasetImagesEnabled = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackupDatabaseEnabled",
                table: "AppSettings");

            migrationBuilder.RenameColumn(
                name: "BackupDatasetImagesEnabled",
                table: "AppSettings",
                newName: "AutoBackupEnabled");
        }
    }
}
