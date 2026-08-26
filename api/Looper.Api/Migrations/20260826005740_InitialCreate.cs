using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Looper.Api.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Agents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    Prompt = table.Column<string>(type: "TEXT", nullable: false),
                    Model = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Effort = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    IntervalMinutes = table.Column<int>(type: "INTEGER", nullable: false),
                    Enabled = table.Column<bool>(type: "INTEGER", nullable: false),
                    DryRun = table.Column<bool>(type: "INTEGER", nullable: false),
                    MaxTurns = table.Column<int>(type: "INTEGER", nullable: false),
                    MaxBudgetUsd = table.Column<decimal>(type: "TEXT", nullable: true),
                    WorkingDirectory = table.Column<string>(type: "TEXT", nullable: true),
                    AllowedTools = table.Column<string>(type: "TEXT", nullable: true),
                    BypassPermissions = table.Column<bool>(type: "INTEGER", nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    LastRunAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Agents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ResourceModules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TypeKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    DisplayName = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Icon = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    Blurb = table.Column<string>(type: "TEXT", nullable: false),
                    SourceCode = table.Column<string>(type: "TEXT", nullable: false),
                    DllFileName = table.Column<string>(type: "TEXT", maxLength: 260, nullable: false),
                    GenerationPrompt = table.Column<string>(type: "TEXT", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ResourceModules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Resources",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Type = table.Column<string>(type: "TEXT", maxLength: 40, nullable: false),
                    CustomTypeKey = table.Column<string>(type: "TEXT", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    ConfigJson = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Resources", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    AgentId = table.Column<Guid>(type: "TEXT", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    Status = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Trigger = table.Column<string>(type: "TEXT", maxLength: 20, nullable: false),
                    Model = table.Column<string>(type: "TEXT", nullable: false),
                    CostUsd = table.Column<decimal>(type: "TEXT", nullable: false),
                    InputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    OutputTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheReadTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    CacheCreationTokens = table.Column<long>(type: "INTEGER", nullable: false),
                    NumTurns = table.Column<int>(type: "INTEGER", nullable: false),
                    DurationMs = table.Column<long>(type: "INTEGER", nullable: false),
                    ResultText = table.Column<string>(type: "TEXT", nullable: true),
                    ErrorMessage = table.Column<string>(type: "TEXT", nullable: true),
                    TestResultsJson = table.Column<string>(type: "TEXT", nullable: true),
                    TestsPassed = table.Column<bool>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Runs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Runs_Agents_AgentId",
                        column: x => x.AgentId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LoopAgentResource",
                columns: table => new
                {
                    AgentsId = table.Column<Guid>(type: "TEXT", nullable: false),
                    ResourcesId = table.Column<Guid>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LoopAgentResource", x => new { x.AgentsId, x.ResourcesId });
                    table.ForeignKey(
                        name: "FK_LoopAgentResource_Agents_AgentsId",
                        column: x => x.AgentsId,
                        principalTable: "Agents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LoopAgentResource_Resources_ResourcesId",
                        column: x => x.ResourcesId,
                        principalTable: "Resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RunLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    RunId = table.Column<Guid>(type: "TEXT", nullable: false),
                    TimestampUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    Level = table.Column<string>(type: "TEXT", maxLength: 10, nullable: false),
                    Message = table.Column<string>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RunLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RunLogs_Runs_RunId",
                        column: x => x.RunId,
                        principalTable: "Runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LoopAgentResource_ResourcesId",
                table: "LoopAgentResource",
                column: "ResourcesId");

            migrationBuilder.CreateIndex(
                name: "IX_ResourceModules_TypeKey",
                table: "ResourceModules",
                column: "TypeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Resources_Type",
                table: "Resources",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_RunLogs_RunId",
                table: "RunLogs",
                column: "RunId");

            migrationBuilder.CreateIndex(
                name: "IX_Runs_AgentId_StartedAtUtc",
                table: "Runs",
                columns: new[] { "AgentId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Runs_StartedAtUtc",
                table: "Runs",
                column: "StartedAtUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LoopAgentResource");

            migrationBuilder.DropTable(
                name: "ResourceModules");

            migrationBuilder.DropTable(
                name: "RunLogs");

            migrationBuilder.DropTable(
                name: "Resources");

            migrationBuilder.DropTable(
                name: "Runs");

            migrationBuilder.DropTable(
                name: "Agents");
        }
    }
}
