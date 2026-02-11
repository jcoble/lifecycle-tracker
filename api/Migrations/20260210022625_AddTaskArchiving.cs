using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class AddTaskArchiving : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ArchivedAt",
                table: "Tasks",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_IsArchived",
                table: "Tasks",
                column: "IsArchived");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Tasks_IsArchived",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "ArchivedAt",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Tasks");
        }
    }
}
