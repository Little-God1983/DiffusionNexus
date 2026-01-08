using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddDisclaimerAcceptance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DisclaimerAcceptances",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    WindowsUsername = table.Column<string>(type: "TEXT", maxLength: 256, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    Accepted = table.Column<bool>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DisclaimerAcceptances", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DisclaimerAcceptances_WindowsUsername",
                table: "DisclaimerAcceptances",
                column: "WindowsUsername");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DisclaimerAcceptances");
        }
    }
}
