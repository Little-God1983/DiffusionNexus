using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddImageTagIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ImageMediaTagIndexes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    FilePath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: false),
                    FileSizeBytes = table.Column<long>(type: "INTEGER", nullable: false),
                    FileLastWriteTimeUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    RatingLabel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    RatingScore = table.Column<float>(type: "REAL", nullable: false),
                    IsNsfw = table.Column<bool>(type: "INTEGER", nullable: false),
                    IndexedAtUtc = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageMediaTagIndexes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImageTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageTags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImageMediaTagAssignments",
                columns: table => new
                {
                    ImageMediaTagIndexId = table.Column<int>(type: "INTEGER", nullable: false),
                    ImageTagId = table.Column<int>(type: "INTEGER", nullable: false),
                    Confidence = table.Column<float>(type: "REAL", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageMediaTagAssignments", x => new { x.ImageMediaTagIndexId, x.ImageTagId });
                    table.ForeignKey(
                        name: "FK_ImageMediaTagAssignments_ImageMediaTagIndexes_ImageMediaTagIndexId",
                        column: x => x.ImageMediaTagIndexId,
                        principalTable: "ImageMediaTagIndexes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageMediaTagAssignments_ImageTags_ImageTagId",
                        column: x => x.ImageTagId,
                        principalTable: "ImageTags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ImageMediaTagAssignments_ImageTagId",
                table: "ImageMediaTagAssignments",
                column: "ImageTagId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageMediaTagIndexes_FilePath",
                table: "ImageMediaTagIndexes",
                column: "FilePath",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageMediaTagIndexes_RatingLabel",
                table: "ImageMediaTagIndexes",
                column: "RatingLabel");

            migrationBuilder.CreateIndex(
                name: "IX_ImageTags_Name",
                table: "ImageTags",
                column: "Name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ImageMediaTagAssignments");

            migrationBuilder.DropTable(
                name: "ImageMediaTagIndexes");

            migrationBuilder.DropTable(
                name: "ImageTags");
        }
    }
}
