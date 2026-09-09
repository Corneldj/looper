using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Looper.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflows : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowId",
                table: "Resources",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001")); // every pre-existing row joins the default workflow

            migrationBuilder.AddColumn<Guid>(
                name: "WorkflowId",
                table: "Agents",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000001")); // every pre-existing row joins the default workflow

            migrationBuilder.CreateTable(
                name: "Workflows",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    Name = table.Column<string>(type: "TEXT", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "TEXT", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Workflows", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Workflows",
                columns: new[] { "Id", "CreatedAtUtc", "Description", "Name", "UpdatedAtUtc" },
                values: new object[] { new Guid("00000000-0000-0000-0000-000000000001"), new DateTime(2026, 9, 9, 0, 0, 0, 0, DateTimeKind.Utc), "The original workbench. Rename it, or create more workflows for other loops.", "Default", new DateTime(2026, 9, 9, 0, 0, 0, 0, DateTimeKind.Utc) });

            migrationBuilder.CreateIndex(
                name: "IX_Resources_WorkflowId",
                table: "Resources",
                column: "WorkflowId");

            migrationBuilder.CreateIndex(
                name: "IX_Agents_WorkflowId",
                table: "Agents",
                column: "WorkflowId");

            migrationBuilder.AddForeignKey(
                name: "FK_Agents_Workflows_WorkflowId",
                table: "Agents",
                column: "WorkflowId",
                principalTable: "Workflows",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Resources_Workflows_WorkflowId",
                table: "Resources",
                column: "WorkflowId",
                principalTable: "Workflows",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Agents_Workflows_WorkflowId",
                table: "Agents");

            migrationBuilder.DropForeignKey(
                name: "FK_Resources_Workflows_WorkflowId",
                table: "Resources");

            migrationBuilder.DropTable(
                name: "Workflows");

            migrationBuilder.DropIndex(
                name: "IX_Resources_WorkflowId",
                table: "Resources");

            migrationBuilder.DropIndex(
                name: "IX_Agents_WorkflowId",
                table: "Agents");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "Resources");

            migrationBuilder.DropColumn(
                name: "WorkflowId",
                table: "Agents");
        }
    }
}
