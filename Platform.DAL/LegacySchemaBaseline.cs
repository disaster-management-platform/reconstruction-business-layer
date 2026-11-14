using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Platform.DAL.Contexts;

namespace Platform.DAL;

// Adopts existing schemas without running their CREATE/RENAME operations again.
// The original history is retained for traceability; each context receives only its own rows.
internal static class LegacySchemaBaseline
{
    private static readonly Dictionary<string, string[]> ReconstructionColumns = new()
    {
        ["ReconstructionProposals"] = ["Id", "DisasterId", "Zone", "AssetType", "Damage", "AssessmentCount", "ProposedBudget", "Priority", "Status", "CreatedAt"],
        ["ReconstructionProjects"] = ["Id", "ProposalId", "DisasterId", "Status", "FinalBudget", "FundingSource", "Responsible", "Contractor", "CreatedAt", "ClosedAt", "Version"],
        ["ReconstructionActivities"] = ["Id", "ProjectId", "Name", "Responsible", "StartsAt", "DueAt", "Budget", "Status"],
        ["ReconstructionReports"] = ["Id", "ActivityId", "Status", "ActualCost", "EvidenceJson", "DocumentsJson", "Actor", "RegisteredAt"],
        ["OutboxMessages"] = ["Id", "EventType", "PayloadJson", "CreatedUnixMs", "SentAt", "Attempts"],
        ["ReconstructionAudit"] = ["Id", "EventId", "Actor", "Action", "Detail", "DisasterId", "ProjectId", "OccurredAt"],
        ["InboxMessages"] = ["Id", "ProcessedAt"],
        ["ReconstructionTimeline"] = ["Id", "EventId", "DisasterId", "ProjectId", "EventType", "Description", "OccurredAt"]
    };

    public static async Task ApplyAsync(DbContext context, CancellationToken ct)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(ct);
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        var known = context.Database.GetMigrations().ToArray();
        var schemaCount = context is DisasterDbContext
            ? await DisasterSchemaVersionAsync(context, ct)
            : await ReconstructionSchemaVersionAsync(context, ct);
        if (applied.Length > 0)
        {
            if (applied.Except(context.Database.GetMigrations()).Any()
                || (applied.All(known.Contains) &&
                    (!applied.SequenceEqual(known.Take(applied.Length)) || applied.Length != schemaCount)))
                throw new InvalidOperationException("El historial del contexto no coincide con el esquema o contiene migraciones desconocidas.");
            await transaction.CommitAsync(ct);
            return;
        }

        var oldRows = await ReadLegacyHistoryAsync(context, known, ct);
        var ids = oldRows.Select(x => x.MigrationId).ToArray();
        if (ids.Length > 0 && (!ids.SequenceEqual(known.Take(ids.Length)) || ids.Length != schemaCount))
            throw new InvalidOperationException("El historial anterior no coincide con el esquema. Revise una copia de seguridad antes de migrar.");

        if (schemaCount > 0)
        {
            var history = context.GetService<IHistoryRepository>();
            await context.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct);
            foreach (var id in known.Take(schemaCount))
            {
                var row = oldRows.SingleOrDefault(x => x.MigrationId == id)
                    ?? new HistoryRow(id, ProductInfo.GetVersion());
                await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(row), ct);
            }
        }
        await transaction.CommitAsync(ct);
    }

    private static async Task<int> DisasterSchemaVersionAsync(DbContext context, CancellationToken ct)
    {
        var columns = await ColumnsAsync(context, "Desastres", ct);
        if (columns.Count == 0) return 0;
        string[] originals = ["Id", "Nombre", "Ubicacion", "Severidad", "Descripcion", "Latitud", "Longitud"];
        if (!columns.IsSupersetOf(originals) || (columns.Contains("OperationId") && columns.Contains("ClientRequestId")))
            throw new InvalidOperationException("El esquema Desastres no corresponde a una versión anterior conocida.");
        var key = columns.Contains("ClientRequestId") ? "ClientRequestId" : columns.Contains("OperationId") ? "OperationId" : null;
        if (key is not null)
            await RequireUniqueIndexAsync(context, "Desastres", [key], ct);
        return columns.IsSupersetOf(["Phase", "Version", "Radio"]) ? 4 : key == "ClientRequestId" ? 3 : key == "OperationId" ? 2 : 1;
    }

    private static async Task<int> ReconstructionSchemaVersionAsync(DbContext context, CancellationToken ct)
    {
        var present = 0;
        foreach (var (table, expected) in ReconstructionColumns)
        {
            var columns = await ColumnsAsync(context, table, ct);
            if (columns.Count == 0) continue;
            present++;
            if (!columns.IsSupersetOf(expected))
                throw new InvalidOperationException($"Esquema incompatible en {table}; no se adoptarán migraciones.");
        }
        if (present != 0 && present != ReconstructionColumns.Count)
            throw new InvalidOperationException("El esquema de Reconstrucción está incompleto; restaure o consolide una base válida.");
        if (present > 0)
        {
            await RequireUniqueIndexAsync(context, "ReconstructionProjects", ["ProposalId"], ct);
            await RequireUniqueIndexAsync(context, "ReconstructionAudit", ["EventId"], ct);
            await RequireUniqueIndexAsync(context, "ReconstructionTimeline", ["EventId"], ct);
        }
        if (present == 0) return 0;
        var proposalColumns = await ColumnsAsync(context, "ReconstructionProposals", ct);
        string[] definitionColumns = ["EvaluationsJson", "IsSelectedForReconstruction", "SelectedForProjectId"];
        var addedColumns = definitionColumns.Count(proposalColumns.Contains);
        if (addedColumns != 0 && addedColumns != definitionColumns.Length)
            throw new InvalidOperationException("El esquema de definición de propuestas está incompleto.");
        var projectColumns = await ColumnsAsync(context, "ReconstructionProjects", ct);
        string[] formalizationColumns = ["FormalizationSavedAt", "SignedContractReference", "ContractSignedAt", "ContractRecordedBy", "BudgetLockedAt"];
        var formalizationCount = formalizationColumns.Count(projectColumns.Contains);
        if (formalizationCount != 0 && (formalizationCount != formalizationColumns.Length || addedColumns == 0))
            throw new InvalidOperationException("El esquema de formalización de proyectos está incompleto.");
        var inboxColumns = await ColumnsAsync(context, "InboxMessages", ct);
        var inboxAdditions = new[] { "ConsumerName", "OutcomeJson" }.Count(inboxColumns.Contains);
        if (inboxAdditions != 0 && (inboxAdditions != 2 || formalizationCount == 0))
            throw new InvalidOperationException("El esquema Inbox por consumidor está incompleto.");
        var modernGrouping = (await ColumnsAsync(context, "ReconstructionReports", ct)).Contains("RequestId");
        var proposalIndexIsPartial = await RequireUniqueIndexAsync(context, "ReconstructionProposals",
            modernGrouping ? ["DisasterId", "Zone", "AssetType"] : ["DisasterId", "Zone", "AssetType", "Damage"], ct, "\"Status\" = 0 AND \"IsSelectedForReconstruction\" = 0");
        if (proposalIndexIsPartial && inboxAdditions != 2)
            throw new InvalidOperationException("El esquema de actualización de propuestas está incompleto.");
        var fundingColumns = await ColumnsAsync(context, "ReconstructionFundingReservations", ct);
        if (fundingColumns.Count > 0)
        {
            if (!proposalIndexIsPartial || inboxAdditions != 2
                || !fundingColumns.IsSupersetOf(["DisasterId", "ReservedAmount", "Version"]))
                throw new InvalidOperationException("El esquema de reservas de financiación está incompleto.");
            using var key = Command(context, "SELECT COUNT(*) FROM pragma_table_info('ReconstructionFundingReservations') WHERE name='DisasterId' AND pk=1;");
            if (Convert.ToInt32(await key.ExecuteScalarAsync(ct)) != 1)
                throw new InvalidOperationException("Falta la clave por desastre en las reservas de financiación.");
        }
        var workflowColumns = await ColumnsAsync(context, "ReconstructionWorkflow", ct);
        if (workflowColumns.Count > 0)
        {
            if (fundingColumns.Count == 0 || !workflowColumns.IsSupersetOf(["Kind", "SubjectId", "DisasterId", "ProjectId",
                "ProposalId", "IsActive", "DataJson", "SourceEventId", "UpdatedAt", "Version"]))
                throw new InvalidOperationException("El esquema de proyecciones del flujo está incompleto.");
            await RequireUniqueIndexAsync(context, "ReconstructionWorkflow", ["Kind", "SubjectId"], ct);
        }
        var hasReviewFingerprint = proposalColumns.Contains("ReviewFingerprint");
        if (hasReviewFingerprint && workflowColumns.Count == 0)
            throw new InvalidOperationException("El esquema de reconciliación de propuestas está incompleto.");
        var hasProposalVersion = proposalColumns.Contains("Version");
        if (hasProposalVersion && !hasReviewFingerprint)
            throw new InvalidOperationException("El esquema de concurrencia de propuestas está incompleto.");
        if (modernGrouping)
        {
            var audit = await ColumnsAsync(context, "ReconstructionAudit", ct);
            if (!audit.IsSupersetOf(["BeforeJson", "AfterJson", "DataJson"]))
                throw new InvalidOperationException("El esquema de auditoría estructurada está incompleto.");
            await RequireUniqueIndexAsync(context, "ReconstructionReports", ["RequestId"], ct, "\"RequestId\" IS NOT NULL");
            using var defaults = Command(context, "SELECT dflt_value FROM pragma_table_info('ReconstructionReports') WHERE name='EvidenceJson';");
            var hasDefaults = await defaults.ExecuteScalarAsync(ct) is string;
            return projectColumns.Contains("FundingSourceId") ? 12 : hasDefaults ? 11 : 10;
        }
        if (inboxAdditions == 2)
        {
            await RequireUniqueIndexAsync(context, "InboxMessages", ["ConsumerName", "Id"], ct);
            return hasProposalVersion ? 9 : hasReviewFingerprint ? 8 : workflowColumns.Count > 0 ? 7 : fundingColumns.Count > 0 ? 6 : proposalIndexIsPartial ? 5 : 4;
        }
        return formalizationCount > 0 ? 3 : addedColumns == 0 ? 1 : 2;
    }

    private static async Task<bool> RequireUniqueIndexAsync(DbContext context, string table, string[] expectedColumns,
        CancellationToken ct, string? allowedPartialFilter = null)
    {
        var indexes = new List<(string Name, bool Partial)>();
        using (var command = Command(context, $"PRAGMA index_list(\"{table}\");"))
        {
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                if (reader.GetInt32(2) == 1 && (reader.GetInt32(4) == 0 || allowedPartialFilter is not null))
                    indexes.Add((reader.GetString(1), reader.GetInt32(4) != 0));
        }
        foreach (var (index, partial) in indexes)
        {
            using var command = Command(context, $"PRAGMA index_info(\"{index.Replace("\"", "\"\"")}\");");
            var columns = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync(ct))
                while (await reader.ReadAsync(ct)) columns.Add(reader.IsDBNull(2) ? "" : reader.GetString(2));
            if (!columns.SequenceEqual(expectedColumns)) continue;
            if (!partial) return false;
            using var definition = Command(context, "SELECT sql FROM sqlite_master WHERE type='index' AND name=@name;");
            var name = definition.CreateParameter(); name.ParameterName = "@name"; name.Value = index;
            definition.Parameters.Add(name);
            var sql = (await definition.ExecuteScalarAsync(ct)) as string;
            if (sql?.TrimEnd(';', ' ', '\n', '\r').EndsWith("WHERE " + allowedPartialFilter, StringComparison.OrdinalIgnoreCase) == true)
                return true;
        }
        throw new InvalidOperationException($"Falta el índice único de {table}({string.Join(", ", expectedColumns)}); no se adoptará el esquema.");
    }

    private static async Task<List<HistoryRow>> ReadLegacyHistoryAsync(DbContext context, string[] known, CancellationToken ct)
    {
        var rows = new List<HistoryRow>();
        if ((await ColumnsAsync(context, "__EFMigrationsHistory", ct)).Count == 0) return rows;
        using var command = Command(context, "SELECT MigrationId, ProductVersion FROM \"__EFMigrationsHistory\" ORDER BY MigrationId;");
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (known.Contains(reader.GetString(0))) rows.Add(new HistoryRow(reader.GetString(0), reader.GetString(1)));
        return rows;
    }

    private static async Task<HashSet<string>> ColumnsAsync(DbContext context, string table, CancellationToken ct)
    {
        // Table identifiers are internal constants, never request data.
        using var command = Command(context, $"PRAGMA table_info(\"{table}\");");
        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) columns.Add(reader.GetString(1));
        return columns;
    }

    private static DbCommand Command(DbContext context, string sql)
    {
        var command = context.Database.GetDbConnection().CreateCommand();
        command.Transaction = context.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText = sql;
        return command;
    }
}
