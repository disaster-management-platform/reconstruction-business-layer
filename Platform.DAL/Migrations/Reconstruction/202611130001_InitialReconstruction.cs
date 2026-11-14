using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

using Platform.DAL.Contexts;

namespace Platform.DAL.Migrations.Reconstruction;

[DbContext(typeof(ReconstructionDbContext))]
[Migration("202611130001_InitialReconstruction")]
public sealed class InitialReconstruction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Tablas del módulo: nunca modifica las tablas del contexto de Desastres.
        migrationBuilder.Sql("""
            CREATE TABLE IF NOT EXISTS "ReconstructionProposals" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "DisasterId" INTEGER NOT NULL,
                "Zone" TEXT NOT NULL,
                "AssetType" TEXT NOT NULL,
                "Damage" INTEGER NOT NULL,
                "AssessmentCount" INTEGER NOT NULL,
                "ProposedBudget" TEXT NOT NULL,
                "Priority" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "CreatedAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReconstructionProposals_DisasterId_Zone_AssetType_Damage"
                ON "ReconstructionProposals"("DisasterId", "Zone", "AssetType", "Damage");

            CREATE TABLE IF NOT EXISTS "ReconstructionProjects" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ProposalId" TEXT NOT NULL,
                "DisasterId" INTEGER NOT NULL,
                "Status" INTEGER NOT NULL,
                "FinalBudget" TEXT NOT NULL,
                "FundingSource" TEXT NOT NULL,
                "Responsible" TEXT NOT NULL,
                "Contractor" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ClosedAt" TEXT NULL,
                "Version" INTEGER NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReconstructionProjects_ProposalId"
                ON "ReconstructionProjects"("ProposalId");

            CREATE TABLE IF NOT EXISTS "ReconstructionActivities" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ProjectId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Responsible" TEXT NOT NULL,
                "StartsAt" TEXT NOT NULL,
                "DueAt" TEXT NOT NULL,
                "Budget" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                FOREIGN KEY("ProjectId") REFERENCES "ReconstructionProjects"("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ReconstructionActivities_ProjectId"
                ON "ReconstructionActivities"("ProjectId");

            CREATE TABLE IF NOT EXISTS "ReconstructionReports" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ActivityId" TEXT NOT NULL,
                "Status" INTEGER NOT NULL,
                "ActualCost" TEXT NOT NULL,
                "EvidenceJson" TEXT NOT NULL,
                "DocumentsJson" TEXT NOT NULL,
                "Actor" TEXT NOT NULL,
                "RegisteredAt" TEXT NOT NULL,
                FOREIGN KEY("ActivityId") REFERENCES "ReconstructionActivities"("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_ReconstructionReports_ActivityId"
                ON "ReconstructionReports"("ActivityId");

            CREATE TABLE IF NOT EXISTS "OutboxMessages" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "EventType" TEXT NOT NULL,
                "PayloadJson" TEXT NOT NULL,
                "CreatedUnixMs" INTEGER NOT NULL,
                "SentAt" TEXT NULL,
                "Attempts" INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_OutboxMessages_SentAt_CreatedUnixMs"
                ON "OutboxMessages"("SentAt", "CreatedUnixMs");

            CREATE TABLE IF NOT EXISTS "ReconstructionAudit" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "EventId" TEXT NOT NULL,
                "Actor" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "Detail" TEXT NOT NULL,
                "DisasterId" INTEGER NOT NULL,
                "ProjectId" TEXT NULL,
                "OccurredAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReconstructionAudit_EventId" ON "ReconstructionAudit"("EventId");
            CREATE INDEX IF NOT EXISTS "IX_ReconstructionAudit_ProjectId" ON "ReconstructionAudit"("ProjectId");

            CREATE TABLE IF NOT EXISTS "InboxMessages" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "ProcessedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "ReconstructionTimeline" (
                "Id" TEXT NOT NULL PRIMARY KEY,
                "EventId" TEXT NOT NULL,
                "DisasterId" INTEGER NOT NULL,
                "ProjectId" TEXT NULL,
                "EventType" TEXT NOT NULL,
                "Description" TEXT NOT NULL,
                "OccurredAt" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReconstructionTimeline_EventId"
                ON "ReconstructionTimeline"("EventId");
            CREATE INDEX IF NOT EXISTS "IX_ReconstructionTimeline_ProjectId"
                ON "ReconstructionTimeline"("ProjectId");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE IF EXISTS "ReconstructionTimeline";
            DROP TABLE IF EXISTS "InboxMessages";
            DROP TABLE IF EXISTS "ReconstructionAudit";
            DROP TABLE IF EXISTS "OutboxMessages";
            DROP TABLE IF EXISTS "ReconstructionReports";
            DROP TABLE IF EXISTS "ReconstructionActivities";
            DROP TABLE IF EXISTS "ReconstructionProjects";
            DROP TABLE IF EXISTS "ReconstructionProposals";
            """);
    }
}
