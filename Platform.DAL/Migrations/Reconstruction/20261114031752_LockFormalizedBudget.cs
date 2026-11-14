using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Platform.DAL.Migrations.Reconstruction
{
    /// <inheritdoc />
    public partial class LockFormalizedBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BudgetLockedAt",
                table: "ReconstructionProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ContractRecordedBy",
                table: "ReconstructionProjects",
                type: "TEXT",
                maxLength: 200,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ContractSignedAt",
                table: "ReconstructionProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FormalizationSavedAt",
                table: "ReconstructionProjects",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SignedContractReference",
                table: "ReconstructionProjects",
                type: "TEXT",
                maxLength: 1000,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BudgetLockedAt",
                table: "ReconstructionProjects");

            migrationBuilder.DropColumn(
                name: "ContractRecordedBy",
                table: "ReconstructionProjects");

            migrationBuilder.DropColumn(
                name: "ContractSignedAt",
                table: "ReconstructionProjects");

            migrationBuilder.DropColumn(
                name: "FormalizationSavedAt",
                table: "ReconstructionProjects");

            migrationBuilder.DropColumn(
                name: "SignedContractReference",
                table: "ReconstructionProjects");
        }
    }
}
