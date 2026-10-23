using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddSyncSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLibrarySyncAt",
                table: "AppSettings",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SyncErrorRetryDays",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "SyncNotIdentifiedRetryDays",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<int>(
                name: "SyncThumbnailConcurrency",
                table: "AppSettings",
                type: "INTEGER",
                nullable: false,
                defaultValue: 4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastLibrarySyncAt",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "SyncErrorRetryDays",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "SyncNotIdentifiedRetryDays",
                table: "AppSettings");

            migrationBuilder.DropColumn(
                name: "SyncThumbnailConcurrency",
                table: "AppSettings");
        }
    }
}
