using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class AddSourceTaskIdAndAgentPlanContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceTaskId",
                table: "Tasks",
                type: "INTEGER",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestPlanContent",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LatestPlanFileName",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LatestPlanUpdatedAt",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Tasks_SourceTaskId",
                table: "Tasks",
                column: "SourceTaskId");

            migrationBuilder.AddForeignKey(
                name: "FK_Tasks_Tasks_SourceTaskId",
                table: "Tasks",
                column: "SourceTaskId",
                principalTable: "Tasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Tasks_Tasks_SourceTaskId",
                table: "Tasks");

            migrationBuilder.DropIndex(
                name: "IX_Tasks_SourceTaskId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "SourceTaskId",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "LatestPlanContent",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LatestPlanFileName",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LatestPlanUpdatedAt",
                table: "AgentSessions");
        }
    }
}
