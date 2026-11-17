"""Regression checks for per-service database configuration."""
import os
from pathlib import Path
import runpy
import unittest
from unittest.mock import MagicMock, patch

SCRIPT = Path(__file__).with_name("run_local.py")


class RunLocalTests(unittest.TestCase):
    def run_script(self, environment):
        process = MagicMock()
        process.poll.return_value = 0
        with patch.dict(os.environ, environment, clear=True), patch("sys.argv", [str(SCRIPT), "--demo"]), \
                patch("pathlib.Path.exists", return_value=True), patch("subprocess.Popen", return_value=process) as start:
            self.start = start
            runpy.run_path(str(SCRIPT), run_name="__main__")
            return start.call_args_list

    def test_api_gets_its_connection_and_gateway_gets_no_database_credentials(self):
        disasters = "Host=local;Database=tests;Username=disasters_api"
        reconstruction = "Host=local;Database=tests;Username=reconstruction_api"
        calls = self.run_script({"DISASTERS_CONNECTION_STRING": disasters,
            "RECONSTRUCTION_CONNECTION_STRING": reconstruction,
            "ConnectionStrings__Disasters": "inherited-connection", "MIGRATION_CONNECTION_STRING": "migration-only",
            "PGPASSWORD": "test-only", "Database__Migrate": "true", "DOTNET_Database__Migrate": "true"})
        self.assertEqual(2, len(calls))
        for call, expected in zip(calls, [reconstruction, None]):
            environment = call.kwargs["env"]
            self.assertEqual(expected, environment.get("ConnectionStrings__Disasters"))
            for key in ["DISASTERS_CONNECTION_STRING", "RECONSTRUCTION_CONNECTION_STRING", "MIGRATION_CONNECTION_STRING",
                        "PGPASSWORD", "Database__Migrate", "DOTNET_Database__Migrate"]:
                self.assertNotIn(key, environment)

    def test_missing_service_connection_stops_before_launch(self):
        with self.assertRaises(SystemExit):
            self.run_script({"DISASTERS_CONNECTION_STRING": "disasters"})
        self.start.assert_not_called()

    def test_disaster_dependency_is_http_and_no_disaster_process_is_started(self):
        calls = self.run_script({"RECONSTRUCTION_CONNECTION_STRING": "reconstruction",
            "ExternalApis__Disasters": "http://127.0.0.1:5511/"})
        self.assertEqual("http://127.0.0.1:5511/", calls[0].kwargs["env"]["ExternalApis__Disasters"])
        self.assertEqual("http://127.0.0.1:5511/", calls[1].kwargs["env"][
            "ReverseProxy__Clusters__disasters-api__Destinations__local__Address"])
        self.assertTrue(all("DisasterLocation.dll" not in " ".join(call.args[0]) for call in calls))


if __name__ == "__main__":
    unittest.main()
