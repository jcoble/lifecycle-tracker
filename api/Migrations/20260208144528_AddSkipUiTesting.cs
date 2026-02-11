using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class AddSkipUiTesting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SkipUiTesting",
                table: "Tasks",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SkipUiTesting",
                table: "Tasks");
        }
    }
}
