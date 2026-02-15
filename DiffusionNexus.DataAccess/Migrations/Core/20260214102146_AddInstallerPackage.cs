using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddInstallerPackage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "InstallerPackages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", nullable: false),
                    InstallationPath = table.Column<string>(type: "TEXT", nullable: false),
                    Type = table.Column<int>(type: "INTEGER", nullable: false),
                    Arguments = table.Column<string>(type: "TEXT", nullable: false),
                    ExecutablePath = table.Column<string>(type: "TEXT", nullable: true),
                    Version = table.Column<string>(type: "TEXT", nullable: true),
                    Branch = table.Column<string>(type: "TEXT", nullable: true),
                    IconPath = table.Column<string>(type: "TEXT", nullable: true),
                    IsUpdateAvailable = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InstallerPackages", x => x.Id);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "InstallerPackages");
        }
    }
}
