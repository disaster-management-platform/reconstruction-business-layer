"""PostgreSQL schema initialization, integrity and transaction regressions."""
from contextlib import closing
import subprocess
import tempfile
import unittest

import psycopg2
from smoke_test import test_database, migrate_database


class DatabaseInitializationTests(unittest.TestCase):
    def setUp(self):
        self.database = self.enterContext(test_database())
        self.log = self.enterContext(tempfile.TemporaryFile(mode="w+"))
        migrate_database(self.database, self.log)
        self.connection = self.enterContext(closing(psycopg2.connect(self.database)))
        self.cursor = self.enterContext(self.connection.cursor())

    def test_complete_schema_and_histories_are_repeatable(self):
        migrate_database(self.database, self.log)
        self.cursor.execute("SELECT table_schema, table_name FROM information_schema.tables WHERE table_schema IN ('disasters','reconstruction')")
        tables = set(self.cursor.fetchall())
        self.assertEqual({("disasters", t) for t in ["Desastres", "DisasterTypes", "DisasterPhaseAudit", "__EFMigrationsHistory_Disaster"]}
            | {("reconstruction", t) for t in ["ReconstructionProposals", "ReconstructionProjects", "ReconstructionActivities",
                "ReconstructionReports", "ReconstructionFundingReservations", "ReconstructionWorkflow", "OutboxMessages", "InboxMessages",
                "ReconstructionAudit", "ReconstructionTimeline", "__EFMigrationsHistory_Reconstruction"]}, tables)
        for schema, history in [("disasters", "__EFMigrationsHistory_Disaster"), ("reconstruction", "__EFMigrationsHistory_Reconstruction")]:
            self.cursor.execute(f'SELECT "MigrationId" FROM {schema}."{history}"')
            self.assertEqual(3 if schema == "disasters" else 2, len(self.cursor.fetchall()))

    def test_uniqueness_failure_rolls_back_all_writes(self):
        self.cursor.execute('''INSERT INTO disasters."Desastres" ("Nombre","Ubicacion","Severidad","Descripcion","Latitud","Longitud") VALUES ('Keep','Bogota',4,'Keep',0,0) RETURNING "Id"''')
        record = self.cursor.fetchone()[0]
        self.connection.commit()
        with self.assertRaises(psycopg2.errors.UniqueViolation):
            self.cursor.execute("""UPDATE disasters."Desastres" SET "Nombre"='Rollback'""")
            self.cursor.execute('INSERT INTO disasters."Desastres" SELECT * FROM disasters."Desastres" WHERE "Id"=%s', (record,))
        self.connection.rollback()
        self.cursor.execute('SELECT "Nombre" FROM disasters."Desastres"')
        self.assertEqual([("Keep",)], self.cursor.fetchall())

    def test_orphan_references_are_rejected(self):
        with self.assertRaises(psycopg2.errors.ForeignKeyViolation):
            self.cursor.execute("""INSERT INTO reconstruction."ReconstructionActivities"
                ("Id","ProjectId","Name","Responsible","StartsAt","DueAt","Budget","Status")
                VALUES(gen_random_uuid(),gen_random_uuid(),'Repair','Team',now(),now(),100.12345,0)""")
        self.connection.rollback()
        self.cursor.execute('SELECT COUNT(*) FROM reconstruction."ReconstructionActivities"')
        self.assertEqual(0, self.cursor.fetchone()[0])

    def test_partial_schema_is_rejected_without_overwriting_rows(self):
        other = self.enterContext(test_database())
        with closing(psycopg2.connect(other)) as connection:
            with connection.cursor() as cursor:
                cursor.execute("""CREATE SCHEMA reconstruction; CREATE TABLE reconstruction."ReconstructionProposals" ("Id" text PRIMARY KEY); INSERT INTO reconstruction."ReconstructionProposals" VALUES ('preserve')""")
                connection.commit()
                with self.assertRaises(subprocess.CalledProcessError):
                    migrate_database(other, self.log)
                cursor.execute('SELECT "Id" FROM reconstruction."ReconstructionProposals"')
                self.assertEqual([("preserve",)], cursor.fetchall())


if __name__ == "__main__":
    unittest.main()
