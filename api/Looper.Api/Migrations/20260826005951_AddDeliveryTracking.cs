using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Looper.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "DryRun",
                table: "Runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "Escalated",
                table: "Runs",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "EscalationReason",
                table: "Runs",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AutonomyLevel",
                table: "Agents",
                type: "INTEGER",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.CreateTable(
                name: "PullRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: true),
                    Title = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Url = table.Column<string>(type: "TEXT", maxLength: 600, nullable: true),
                    Repository = table.Column<string>(type: "TEXT", maxLength: 300, nullable: false),
                    Number = table.Column<int>(type: "INTEGER", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    OpenedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    MergedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    ClosedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Additions = table.Column<int>(type: "INTEGER", nullable: false),
                    Deletions = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewRounds = table.Column<int>(type: "INTEGER", nullable: false),
                    ReviewComments = table.Column<int>(type: "INTEGER", nullable: false),
                    HumanCommits = table.Column<int>(type: "INTEGER", nullable: false),
                    RepoPath = table.Column<string>(type: "TEXT", nullable: true),
                    MergeCommitSha = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true),
                    SurvivalCheckedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SurvivingAdditions = table.Column<int>(type: "INTEGER", nullable: true),
                    LastSyncedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    SyncError = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PullRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PullRequests_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_AgentId",
                table: "PullRequests",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_Status",
                table: "PullRequests",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_PullRequests_Url",
                table: "PullRequests",
                column: "Url");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PullRequests");

            migrationBuilder.DropColumn(
                name: "DryRun",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "Escalated",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "EscalationReason",
                table: "Runs");

            migrationBuilder.DropColumn(
                name: "AutonomyLevel",
                table: "Agents");
        }
    }
}
