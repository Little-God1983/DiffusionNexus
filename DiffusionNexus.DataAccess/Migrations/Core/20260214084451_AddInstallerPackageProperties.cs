using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DiffusionNexus.DataAccess.Migrations.Core
{
    /// <inheritdoc />
    public partial class AddInstallerPackageProperties : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Models",
                type: "TEXT",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "ImageGalleries",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "ImageGalleries",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                table: "DisclaimerAcceptances",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "DisclaimerAcceptances",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "DatasetCategories",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Creators",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "ImageGalleries");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "ImageGalleries");

            migrationBuilder.DropColumn(
                name: "CreatedAt",
                table: "DisclaimerAcceptances");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "DisclaimerAcceptances");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "DatasetCategories");

            migrationBuilder.DropColumn(
                name: "UpdatedAt",
                table: "Creators");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                table: "Models",
                type: "TEXT",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "TEXT",
                oldNullable: true);
        }
    }
}
