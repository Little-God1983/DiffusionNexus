using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class LinkInstallerPackageToImageGallery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InstallerPackageId",
                table: "ImageGalleries",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageGalleries_InstallerPackageId",
                table: "ImageGalleries",
                column: "InstallerPackageId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_ImageGalleries_InstallerPackages_InstallerPackageId",
                table: "ImageGalleries",
                column: "InstallerPackageId",
                principalTable: "InstallerPackages",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ImageGalleries_InstallerPackages_InstallerPackageId",
                table: "ImageGalleries");

            migrationBuilder.DropIndex(
                name: "IX_ImageGalleries_InstallerPackageId",
                table: "ImageGalleries");

            migrationBuilder.DropColumn(
                name: "InstallerPackageId",
                table: "ImageGalleries");
        }
    }
}
