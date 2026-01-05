using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Creators",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    AvatarUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Creators", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    NormalizedName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Models",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CivitaiId = table.Column<int>(type: "INTEGER", nullable: true),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    Type = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsNsfw = table.Column<bool>(type: "INTEGER", nullable: false),
                    IsPoi = table.Column<bool>(type: "INTEGER", nullable: false),
                    Mode = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    LastSyncedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    AllowNoCredit = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowCommercialUse = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    AllowDerivatives = table.Column<bool>(type: "INTEGER", nullable: false),
                    AllowDifferentLicense = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreatorId = table.Column<int>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Models", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Models_Creators_CreatorId",
                        column: x => x.CreatorId,
                        principalTable: "Creators",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ModelTags",
                columns: table => new
                {
                    ModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    TagId = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelTags", x => new { x.ModelId, x.TagId });
                    table.ForeignKey(
                        name: "FK_ModelTags_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ModelTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CivitaiId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelId = table.Column<int>(type: "INTEGER", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: true),
                    BaseModel = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    BaseModelRaw = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    DownloadUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    EarlyAccessDays = table.Column<int>(type: "INTEGER", nullable: false),
                    DownloadCount = table.Column<int>(type: "INTEGER", nullable: false),
                    RatingCount = table.Column<int>(type: "INTEGER", nullable: false),
                    Rating = table.Column<double>(type: "REAL", nullable: false),
                    ThumbsUpCount = table.Column<int>(type: "INTEGER", nullable: false),
                    ThumbsDownCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelVersions_Models_ModelId",
                        column: x => x.ModelId,
                        principalTable: "Models",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CivitaiId = table.Column<int>(type: "INTEGER", nullable: true),
                    ModelVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    FileName = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    SizeKB = table.Column<double>(type: "REAL", nullable: false),
                    FileType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: false),
                    IsPrimary = table.Column<bool>(type: "INTEGER", nullable: false),
                    Format = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Precision = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    SizeType = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    DownloadUrl = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: true),
                    PickleScanResult = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    PickleScanMessage = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    VirusScanResult = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    ScannedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    HashAutoV1 = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    HashAutoV2 = table.Column<string>(type: "TEXT", maxLength: 20, nullable: true),
                    HashSHA256 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    HashCRC32 = table.Column<string>(type: "TEXT", maxLength: 10, nullable: true),
                    HashBLAKE3 = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    LocalPath = table.Column<string>(type: "TEXT", maxLength: 1000, nullable: true),
                    IsLocalFileValid = table.Column<bool>(type: "INTEGER", nullable: false),
                    LocalFileVerifiedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelFiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelFiles_ModelVersions_ModelVersionId",
                        column: x => x.ModelVersionId,
                        principalTable: "ModelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ModelImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    CivitaiId = table.Column<long>(type: "INTEGER", nullable: true),
                    ModelVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 2000, nullable: false),
                    IsNsfw = table.Column<bool>(type: "INTEGER", nullable: false),
                    NsfwLevel = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Width = table.Column<int>(type: "INTEGER", nullable: false),
                    Height = table.Column<int>(type: "INTEGER", nullable: false),
                    BlurHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    SortOrder = table.Column<int>(type: "INTEGER", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    PostId = table.Column<int>(type: "INTEGER", nullable: true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    ThumbnailData = table.Column<byte[]>(type: "BLOB", nullable: true),
                    ThumbnailMimeType = table.Column<string>(type: "TEXT", maxLength: 50, nullable: true),
                    ThumbnailWidth = table.Column<int>(type: "INTEGER", nullable: true),
                    ThumbnailHeight = table.Column<int>(type: "INTEGER", nullable: true),
                    LocalCachePath = table.Column<string>(type: "TEXT", maxLength: 500, nullable: true),
                    IsLocalCacheValid = table.Column<bool>(type: "INTEGER", nullable: false),
                    CachedAt = table.Column<DateTimeOffset>(type: "TEXT", nullable: true),
                    CachedFileSize = table.Column<long>(type: "INTEGER", nullable: true),
                    Prompt = table.Column<string>(type: "TEXT", nullable: true),
                    NegativePrompt = table.Column<string>(type: "TEXT", nullable: true),
                    Seed = table.Column<long>(type: "INTEGER", nullable: true),
                    Steps = table.Column<int>(type: "INTEGER", nullable: true),
                    Sampler = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    CfgScale = table.Column<double>(type: "REAL", nullable: true),
                    GenerationModel = table.Column<string>(type: "TEXT", maxLength: 200, nullable: true),
                    DenoisingStrength = table.Column<double>(type: "REAL", nullable: true),
                    LikeCount = table.Column<int>(type: "INTEGER", nullable: false),
                    HeartCount = table.Column<int>(type: "INTEGER", nullable: false),
                    CommentCount = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ModelImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ModelImages_ModelVersions_ModelVersionId",
                        column: x => x.ModelVersionId,
                        principalTable: "ModelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TriggerWords",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    ModelVersionId = table.Column<int>(type: "INTEGER", nullable: false),
                    Word = table.Column<string>(type: "TEXT", maxLength: 500, nullable: false),
                    Order = table.Column<int>(type: "INTEGER", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TriggerWords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TriggerWords_ModelVersions_ModelVersionId",
                        column: x => x.ModelVersionId,
                        principalTable: "ModelVersions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Creators_Username",
                table: "Creators",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ModelFiles_CivitaiId",
                table: "ModelFiles",
                column: "CivitaiId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelFiles_HashSHA256",
                table: "ModelFiles",
                column: "HashSHA256");

            migrationBuilder.CreateIndex(
                name: "IX_ModelFiles_LocalPath",
                table: "ModelFiles",
                column: "LocalPath");

            migrationBuilder.CreateIndex(
                name: "IX_ModelFiles_ModelVersionId",
                table: "ModelFiles",
                column: "ModelVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelImages_CivitaiId",
                table: "ModelImages",
                column: "CivitaiId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelImages_ModelVersionId",
                table: "ModelImages",
                column: "ModelVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelImages_ModelVersionId_SortOrder",
                table: "ModelImages",
                columns: new[] { "ModelVersionId", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_Models_CivitaiId",
                table: "Models",
                column: "CivitaiId",
                unique: true,
                filter: "[CivitaiId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Models_CreatedAt",
                table: "Models",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_Models_CreatorId",
                table: "Models",
                column: "CreatorId");

            migrationBuilder.CreateIndex(
                name: "IX_Models_Name",
                table: "Models",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Models_Type",
                table: "Models",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_ModelTags_TagId",
                table: "ModelTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_BaseModel",
                table: "ModelVersions",
                column: "BaseModel");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_CivitaiId",
                table: "ModelVersions",
                column: "CivitaiId",
                unique: true,
                filter: "[CivitaiId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_CreatedAt",
                table: "ModelVersions",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ModelVersions_ModelId",
                table: "ModelVersions",
                column: "ModelId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_NormalizedName",
                table: "Tags",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TriggerWords_ModelVersionId",
                table: "TriggerWords",
                column: "ModelVersionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ModelFiles");

            migrationBuilder.DropTable(
                name: "ModelImages");

            migrationBuilder.DropTable(
                name: "ModelTags");

            migrationBuilder.DropTable(
                name: "TriggerWords");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "ModelVersions");

            migrationBuilder.DropTable(
                name: "Models");

            migrationBuilder.DropTable(
                name: "Creators");
        }
    }
}
