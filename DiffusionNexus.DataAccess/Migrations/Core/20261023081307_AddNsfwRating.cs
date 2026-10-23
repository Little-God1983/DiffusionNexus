using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddNsfwRating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "NsfwRating",
                table: "ModelImages",
                type: "INTEGER",
                nullable: false,
                defaultValue: 0);

            // #631. Every image starts unrated: the stored IsNsfw came from a Civitai field the API
            // no longer sends and said SFW for nearly everything, so it is not carried over.
            // Un-stamping the images step for each model that holds a Civitai image makes the next
            // Download Metadata run fetch those images once and store their ratings; a model with
            // only local previews has nothing to fetch. IsNsfw and NsfwLevel stay as they are —
            // older builds still map them (see ModelImage.LegacyIsNsfw).
            migrationBuilder.Sql(
                "UPDATE ModelSyncStates SET ImagesCheckedAt = NULL WHERE ModelId IN (" +
                "SELECT v.ModelId FROM ModelVersions v JOIN ModelImages i ON i.ModelVersionId = v.Id " +
                "WHERE i.Url LIKE 'http://%' OR i.Url LIKE 'https://%');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NsfwRating",
                table: "ModelImages");
        }
    }
}
