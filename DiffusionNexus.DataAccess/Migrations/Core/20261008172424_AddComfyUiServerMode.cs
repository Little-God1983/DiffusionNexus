using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddComfyUiServerMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ComfyUiServerMode",
                table: "AppSettings",
                type: "TEXT",
                maxLength: 32,
                nullable: false,
                defaultValue: "Engine");

            // Rows that exist when this migration runs belong to users whose Inpaint/Outpaint ran on
            // their own ComfyUI. Keep them there; only rows created later (fresh installs, the
            // publish.ps1 seed) get the column default, Engine. See the #606 spec, section 4.1.
            migrationBuilder.Sql("UPDATE AppSettings SET ComfyUiServerMode = 'CustomUrl';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ComfyUiServerMode",
                table: "AppSettings");
        }
    }
}
