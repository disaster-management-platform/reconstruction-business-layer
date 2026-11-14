using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Reconstruction
{
    /// <inheritdoc />
    public partial class DefineReconstructionProposals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EvaluationsJson",
                table: "ReconstructionProposals",
                type: "TEXT",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<bool>(
                name: "IsSelectedForReconstruction",
                table: "ReconstructionProposals",
                type: "INTEGER",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SelectedForProjectId",
                table: "ReconstructionProposals",
                type: "TEXT",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EvaluationsJson",
                table: "ReconstructionProposals");

            migrationBuilder.DropColumn(
                name: "IsSelectedForReconstruction",
                table: "ReconstructionProposals");

            migrationBuilder.DropColumn(
                name: "SelectedForProjectId",
                table: "ReconstructionProposals");
        }
    }
}
