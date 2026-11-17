"""Regression tests for data preservation and atomic consolidation, using temporary files."""
from contextlib import closing
from pathlib import Path
import re
import json
import sqlite3
import tempfile
import unittest

from consolidate_databases import INITIAL, TABLES, consolidate


class ConsolidationTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="consolidation-test-")
        self.addCleanup(self.temp.cleanup)
        self.disasters = Path(self.temp.name) / "disasters.db"
        self.reconstruction = Path(self.temp.name) / "reconstruction.db"
        with closing(sqlite3.connect(self.disasters)) as db:
            db.executescript("""
                CREATE TABLE Desastres (Id INTEGER PRIMARY KEY, ClientRequestId TEXT,
                    Nombre TEXT, Ubicacion TEXT, Severidad INTEGER, Descripcion TEXT,
                    Latitud REAL, Longitud REAL);
                CREATE UNIQUE INDEX IX_Desastres_ClientRequestId ON Desastres(ClientRequestId);
                INSERT INTO Desastres VALUES (42, NULL, 'Histórico', 'Bogotá', 4, 'Conservar', 4.71, -74.07);
                CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);
                INSERT INTO __EFMigrationsHistory VALUES ('20261106013444_InitialCreate', '10.0.14');
                INSERT INTO __EFMigrationsHistory VALUES ('20261106013504_AddOperationId', '10.0.14');
                INSERT INTO __EFMigrationsHistory VALUES ('20261106013525_UseClientRequestId', '10.0.14');
            """)
        assets = json.loads((Path(__file__).resolve().parents[1] / "Reconstruction.Tests/obj/project.assets.json").read_text())
        package = next(value["path"] for name, value in assets["libraries"].items() if name.startswith("DisasterPlatform.DAL/"))
        migration = next(Path(folder) / package / "contentFiles/any/any/Migrations/Reconstruction/202611130001_InitialReconstruction.cs"
                         for folder in assets["packageFolders"] if (Path(folder) / package).is_dir()).read_text()
        sql = re.search(r'migrationBuilder.Sql\("""(.*?)"""\);', migration, re.S).group(1)
        with closing(sqlite3.connect(self.reconstruction)) as db:
            db.executescript(sql)
            db.executescript("""
                INSERT INTO ReconstructionProposals VALUES ('proposal',42,'Norte','Puente',3,1,'100',2,1,'2026-11-13');
                INSERT INTO ReconstructionProjects VALUES ('project','proposal',42,3,'100','Donaciones','Entidad','Contratista','2026-11-13','2026-11-14',5);
                INSERT INTO ReconstructionActivities VALUES ('activity','project','Reparar','Equipo','2026-11-13','2026-11-14','100',2);
                INSERT INTO ReconstructionReports VALUES ('report','activity',2,'90','["foto"]','["acta"]','operador','2026-11-14');
                INSERT INTO OutboxMessages VALUES ('event','reconstruction.project.closed','{}',1,NULL,0);
                INSERT INTO ReconstructionAudit VALUES ('audit','event','operador','reconstruction.project.closed','Conservar',42,'project','2026-11-14');
                INSERT INTO InboxMessages VALUES ('event','2026-11-14');
                INSERT INTO ReconstructionTimeline VALUES ('timeline','event',42,'project','reconstruction.project.closed','Conservar','2026-11-14');
                CREATE TABLE __EFMigrationsHistory(MigrationId TEXT PRIMARY KEY, ProductVersion TEXT);
                INSERT INTO __EFMigrationsHistory VALUES ('202611130001_InitialReconstruction','10.0.14');
            """)

    def test_all_data_and_histories_survive_and_import_is_repeatable(self):
        copied, backups = consolidate(self.disasters, self.reconstruction)
        self.assertEqual({t: 1 for t in TABLES}, copied)
        self.assertTrue(all(p.is_file() for p in backups))
        with closing(sqlite3.connect(self.disasters)) as target, closing(sqlite3.connect(self.reconstruction)) as source:
            for table in TABLES:
                self.assertEqual(source.execute(f'SELECT * FROM "{table}"').fetchall(), target.execute(f'SELECT * FROM "{table}"').fetchall())
            self.assertEqual(3, target.execute("SELECT COUNT(*) FROM __EFMigrationsHistory").fetchone()[0])
            self.assertEqual(INITIAL, target.execute("SELECT MigrationId FROM __EFMigrationsHistory_Reconstruction").fetchone()[0])
            self.assertEqual("Histórico", target.execute("SELECT Nombre FROM Desastres").fetchone()[0])
            self.assertEqual([], target.execute("PRAGMA foreign_key_check").fetchall())
        repeated, _ = consolidate(self.disasters, self.reconstruction)
        self.assertEqual({t: 0 for t in TABLES}, repeated)

    def test_conflicting_rows_roll_back_the_whole_import(self):
        consolidate(self.disasters, self.reconstruction)
        with closing(sqlite3.connect(self.reconstruction)) as db:
            # First proposal would be inserted before a conflicting project is found.
            db.execute("INSERT INTO ReconstructionProposals VALUES ('new',42,'Sur','Escuela',2,1,'20',1,0,'2026-11-13')")
            db.execute("UPDATE ReconstructionProjects SET FinalBudget='999'")
            db.commit()
        with self.assertRaisesRegex(ValueError, "Conflicto"):
            consolidate(self.disasters, self.reconstruction)
        with closing(sqlite3.connect(self.disasters)) as db:
            self.assertEqual(1, db.execute("SELECT COUNT(*) FROM ReconstructionProposals").fetchone()[0])
            self.assertEqual('100', db.execute("SELECT FinalBudget FROM ReconstructionProjects").fetchone()[0])

    def test_missing_disaster_rolls_back_tables_and_history(self):
        with closing(sqlite3.connect(self.disasters)) as db:
            db.execute("DELETE FROM Desastres")
            db.commit()
        with self.assertRaisesRegex(ValueError, "desastre ausente"):
            consolidate(self.disasters, self.reconstruction)
        with closing(sqlite3.connect(self.disasters)) as db:
            self.assertIsNone(db.execute("SELECT name FROM sqlite_master WHERE name='ReconstructionProjects'").fetchone())
            self.assertEqual(3, db.execute("SELECT COUNT(*) FROM __EFMigrationsHistory").fetchone()[0])

    def test_incomplete_source_is_rejected(self):
        with closing(sqlite3.connect(self.reconstruction)) as db:
            db.execute("DROP TABLE InboxMessages")
        with self.assertRaisesRegex(ValueError, "incompleto"):
            consolidate(self.disasters, self.reconstruction)


if __name__ == "__main__":
    unittest.main()
