using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddCivitaiModelPageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CivitaiModelPageId",
                table: "Models",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Models_CivitaiModelPageId",
                table: "Models",
                column: "CivitaiModelPageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Models_CivitaiModelPageId",
                table: "Models");

            migrationBuilder.DropColumn(
                name: "CivitaiModelPageId",
                table: "Models");
        }
    }
}
