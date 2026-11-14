#!/usr/bin/env python3
"""Import the old reconstruction database into disasters.db with both APIs stopped.

Creates SQLite backups of both files, retains their original data/history, rejects
conflicting rows, and rolls the entire import back on error. Run an API afterwards
to validate/adopt the histories and apply only pending migrations.
"""
import argparse
from contextlib import closing
from datetime import datetime, timezone
from pathlib import Path
import sqlite3


TABLES = (
    "ReconstructionProposals", "ReconstructionProjects", "ReconstructionActivities",
    "ReconstructionReports", "OutboxMessages", "ReconstructionAudit", "InboxMessages",
    "ReconstructionTimeline",
)
RECON_HISTORY = "__EFMigrationsHistory_Reconstruction"
INITIAL = "202611130001_InitialReconstruction"
DISASTER_MIGRATIONS = {
    "20261106013444_InitialCreate", "20261106013504_AddOperationId",
    "20261106013525_UseClientRequestId",
}
EXPECTED_COLUMNS = {
    "ReconstructionProposals": "Id DisasterId Zone AssetType Damage AssessmentCount ProposedBudget Priority Status CreatedAt",
    "ReconstructionProjects": "Id ProposalId DisasterId Status FinalBudget FundingSource Responsible Contractor CreatedAt ClosedAt Version",
    "ReconstructionActivities": "Id ProjectId Name Responsible StartsAt DueAt Budget Status",
    "ReconstructionReports": "Id ActivityId Status ActualCost EvidenceJson DocumentsJson Actor RegisteredAt",
    "OutboxMessages": "Id EventType PayloadJson CreatedUnixMs SentAt Attempts",
    "ReconstructionAudit": "Id EventId Actor Action Detail DisasterId ProjectId OccurredAt",
    "InboxMessages": "Id ProcessedAt",
    "ReconstructionTimeline": "Id EventId DisasterId ProjectId EventType Description OccurredAt",
}
UNIQUE_INDEXES = {
    "ReconstructionProposals": ("DisasterId", "Zone", "AssetType", "Damage"),
    "ReconstructionProjects": ("ProposalId",),
    "ReconstructionAudit": ("EventId",),
    "ReconstructionTimeline": ("EventId",),
}


def quote(identifier):
    return '"' + identifier.replace('"', '""') + '"'


def table_exists(db, schema, table):
    return db.execute(f"SELECT 1 FROM {schema}.sqlite_master WHERE type='table' AND name=?", (table,)).fetchone() is not None


def backup(path, stamp):
    destination = path.with_name(path.name + f".{stamp}.bak")
    with closing(sqlite3.connect(path.as_uri() + "?mode=ro", uri=True)) as source:
        with closing(sqlite3.connect(destination)) as target:
            source.backup(target)
    return destination


def consolidate(disaster_path, reconstruction_path):
    disaster_path, reconstruction_path = Path(disaster_path).resolve(), Path(reconstruction_path).resolve()
    if not disaster_path.is_file() or not reconstruction_path.is_file():
        raise ValueError("Ambas bases deben existir. No se crearán bases vacías para sustituir archivos faltantes.")
    if disaster_path == reconstruction_path or disaster_path.samefile(reconstruction_path):
        raise ValueError("El origen y el destino deben ser archivos distintos.")
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    disaster_backup = backup(disaster_path, stamp)
    reconstruction_backup = backup(reconstruction_path, stamp)
    with closing(sqlite3.connect(disaster_path, isolation_level=None)) as db:
        db.execute("PRAGMA foreign_keys=ON")
        # Import a consistent read-only snapshot. The source file is never modified.
        db.execute("ATTACH DATABASE ? AS legacy", (reconstruction_backup.as_uri() + "?mode=ro",))
        db.execute("BEGIN IMMEDIATE")
        try:
            if not table_exists(db, "main", "Desastres"):
                raise ValueError("El destino no contiene Desastres; revise la ruta de disasters.db.")
            for table in TABLES:
                if not table_exists(db, "legacy", table):
                    raise ValueError(f"Origen incompleto: falta {table}.")
            for table, expected in UNIQUE_INDEXES.items():
                indexes = db.execute(f"PRAGMA legacy.index_list({quote(table)})").fetchall()
                if not any(index[2] and not index[4] and tuple(row[2] for row in db.execute(
                    f"PRAGMA legacy.index_info({quote(index[1])})")) == expected for index in indexes):
                    raise ValueError(f"Falta el índice único de {table}{expected} en el origen.")
            if db.execute("PRAGMA legacy.foreign_key_check").fetchall():
                raise ValueError("El origen viola claves foráneas.")
            # Only accept the original, known schema version; never silently downgrade.
            source_history = RECON_HISTORY if table_exists(db, "legacy", RECON_HISTORY) else "__EFMigrationsHistory"
            history = []
            if table_exists(db, "legacy", source_history):
                history = db.execute(f"SELECT MigrationId, ProductVersion FROM legacy.{quote(source_history)} ORDER BY MigrationId").fetchall()
                if any(r[0] not in DISASTER_MIGRATIONS | {INITIAL} for r in history):
                    raise ValueError("El origen contiene migraciones de Reconstrucción no soportadas.")
                if source_history == RECON_HISTORY and any(r[0] != INITIAL for r in history):
                    raise ValueError("Historial de Reconstrucción no soportado.")

            copied = {}
            for table in TABLES:
                name = quote(table)
                source_columns = db.execute(f"PRAGMA legacy.table_info({name})").fetchall()
                if {row[1] for row in source_columns} != set(EXPECTED_COLUMNS[table].split()):
                    raise ValueError(f"Columnas desconocidas o incompletas en {table}.")
                if table_exists(db, "main", table):
                    target_columns = db.execute(f"PRAGMA main.table_info({name})").fetchall()
                    if source_columns != target_columns:
                        raise ValueError(f"Esquemas distintos en {table}; se cancela la importación.")
                else:
                    sql = db.execute("SELECT sql FROM legacy.sqlite_master WHERE type='table' AND name=?", (table,)).fetchone()[0]
                    db.execute(sql)
                columns = [row[1] for row in source_columns]
                if "Id" not in columns:
                    raise ValueError(f"Falta Id en {table}.")
                fields = ", ".join(map(quote, columns))
                inserted = 0
                for row in db.execute(f"SELECT {fields} FROM legacy.{name}"):
                    existing = db.execute(f"SELECT {fields} FROM main.{name} WHERE Id=?", (row[columns.index("Id")],)).fetchone()
                    if existing is not None:
                        if existing != row:
                            raise ValueError(f"Conflicto en {table}, Id={row[columns.index('Id')]}; no se sobrescriben datos.")
                    else:
                        db.execute(f"INSERT INTO main.{name} ({fields}) VALUES ({', '.join('?' for _ in columns)})", row)
                        inserted += 1
                copied[table] = inserted
                for (sql,) in db.execute("SELECT sql FROM legacy.sqlite_master WHERE type='index' AND tbl_name=? AND sql IS NOT NULL", (table,)).fetchall():
                    # Repeated imports retain already-created indexes.
                    db.execute(sql.replace("CREATE UNIQUE INDEX ", "CREATE UNIQUE INDEX IF NOT EXISTS ", 1)
                               .replace("CREATE INDEX ", "CREATE INDEX IF NOT EXISTS ", 1))

            for table in ("ReconstructionProposals", "ReconstructionProjects", "ReconstructionAudit", "ReconstructionTimeline"):
                if db.execute(f"SELECT 1 FROM {quote(table)} r LEFT JOIN Desastres d ON d.Id=r.DisasterId WHERE d.Id IS NULL LIMIT 1").fetchone():
                    raise ValueError(f"{table} referencia un desastre ausente en el destino. No se reasignarán IDs automáticamente.")
            if db.execute("PRAGMA main.foreign_key_check").fetchall():
                raise ValueError("La importación viola claves foráneas.")
            if db.execute("PRAGMA main.integrity_check").fetchone()[0] != "ok":
                raise ValueError("La comprobación de integridad SQLite falló.")

            # Keep old history intact. Startup will validate the complete schema and
            # adopt INITIAL if the source was created without migrations.
            if history:
                if table_exists(db, "main", RECON_HISTORY):
                    if any(row[0] != INITIAL for row in db.execute(f"SELECT MigrationId FROM {quote(RECON_HISTORY)}")):
                        raise ValueError("El destino contiene migraciones de Reconstrucción no soportadas por este importador.")
                db.execute(f'CREATE TABLE IF NOT EXISTS {quote(RECON_HISTORY)} ("MigrationId" TEXT NOT NULL PRIMARY KEY, "ProductVersion" TEXT NOT NULL)')
                for migration_id, version in history:
                    if migration_id == INITIAL:
                        db.execute(f"INSERT OR IGNORE INTO {quote(RECON_HISTORY)} VALUES (?, ?)", (migration_id, version))
            db.execute("COMMIT")
        except BaseException:
            db.execute("ROLLBACK")
            raise
    return copied, (disaster_backup, reconstruction_backup)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--disasters", type=Path, required=True)
    parser.add_argument("--reconstruction", type=Path, required=True)
    args = parser.parse_args()
    copied, backups = consolidate(args.disasters, args.reconstruction)
    print("Consolidación terminada:", copied)
    print("Copias de seguridad:", *(str(path) for path in backups), sep="\n  ")
    print("Inicie una API para adaptar los historiales y aplicar únicamente migraciones pendientes.")


if __name__ == "__main__":
    main()
