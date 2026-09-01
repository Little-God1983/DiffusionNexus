using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <summary>
    /// Drops the write-only <c>ModelVersions.BaseModel</c> column and its index (#553). The enum
    /// it stored is deleted from the model, so this migration is FORWARD-ONLY in practice: builds
    /// before it still map the property, and rolling back to one leaves that build querying a
    /// column that no longer exists (its recovery service has no repair that re-adds it, and its
    /// CleanStaleMigrationHistory un-stamps this row so the schema and history disagree). A manual
    /// rollback needs <c>Down()</c> below — or, equivalently,
    /// <c>ALTER TABLE ModelVersions ADD COLUMN BaseModel TEXT NOT NULL DEFAULT 'Unknown'</c>.
    /// The stamped-without-run direction (MarkPendingMigrationsAsApplied) is healed by
    /// <c>DatabaseRecoveryService.DropLeftoverModelVersionsBaseModelColumn</c>.
    /// </summary>
    public partial class RemoveDeadBaseModelTypeColumn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS instead of DropIndex: a downgrade's CleanStaleMigrationHistory can un-stamp
            // this migration after the drop already ran, so a later re-upgrade replays it against a
            // database where the index (and column) are already gone.
            migrationBuilder.Sql("DROP INDEX IF EXISTS \"IX_ModelVersions_BaseModel\";");

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
                defaultValue: "Unknown");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_BaseModel",
                table: "ModelVersions",
                column: "BaseModel");
        }
    }
}
