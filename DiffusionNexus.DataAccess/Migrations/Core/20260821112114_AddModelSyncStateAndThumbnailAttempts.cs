using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddModelSyncStateAndThumbnailAttempts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ThumbnailAttemptedAt",
                table: "ModelImages",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThumbnailFailure",
                table: "ModelImages",
                type: "TEXT",
                maxLength: 30,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ModelSyncStates",
                columns: table => new
                {
                    ModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    MetadataCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    MetadataOutcome = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    MetadataAttempts = table.Column<int>(type: "INTEGER", nullable: false),
                    LastError = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    TagsCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    ImagesCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    SidecarSignature = table.Column<string>(type: "TEXT", maxLength: 1100, nullable: true),
                    HeaderCheckedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelSyncStates", x => x.ModelId);
                    table.ForeignKey(
                        name: "FK_ModelSyncStates_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // D2 (issue #521): downloads stored SHA256 uppercase, the viewer's sync stored it lowercase.
            // Normalize once so SQL equality works without ToLower() scans. Idempotent; covered by the
            // pre-migration backup taken by DatabaseRecoveryService.
            migrationBuilder.Sql("UPDATE ModelFiles SET HashSHA256 = upper(HashSHA256) WHERE HashSHA256 IS NOT NULL AND HashSHA256 <> upper(HashSHA256);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelSyncStates");

            migrationBuilder.DropColumn(
                name: "ThumbnailAttemptedAt",
                table: "ModelImages");

            migrationBuilder.DropColumn(
                name: "ThumbnailFailure",
                table: "ModelImages");
        }
    }
}
