using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Lifecycle.Migrations
{
    /// <inheritdoc />
    public partial class AddAgentActivityTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TriggerStatuses",
                table: "TeamMembers",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PullRequestUrl",
                table: "Tasks",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Settings",
                table: "Projects",
                type: "TEXT",
                maxLength: 10000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CurrentActivity",
                table: "AgentSessions",
                type: "TEXT",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastHeartbeatAt",
                table: "AgentSessions",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TriggerStatuses",
                table: "TeamMembers");

            migrationBuilder.DropColumn(
                name: "PullRequestUrl",
                table: "Tasks");

            migrationBuilder.DropColumn(
                name: "Settings",
                table: "Projects");

            migrationBuilder.DropColumn(
                name: "CurrentActivity",
                table: "AgentSessions");

            migrationBuilder.DropColumn(
                name: "LastHeartbeatAt",
                table: "AgentSessions");
        }
    }
}
