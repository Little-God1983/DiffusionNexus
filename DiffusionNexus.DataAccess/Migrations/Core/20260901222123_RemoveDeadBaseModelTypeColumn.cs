using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class RemoveDeadBaseModelTypeColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ModelVersions_BaseModel",
                table: "ModelVersions");

            migrationBuilder.DropColumn(
                name: "BaseModel",
                table: "ModelVersions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BaseModel",
                table: "ModelVersions",
                type: "TEXT",
                maxLength: 50,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_BaseModel",
                table: "ModelVersions",
                column: "BaseModel");
        }
    }
}
