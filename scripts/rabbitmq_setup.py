#!/usr/bin/env python3
"""Declare local development topology without consuming or deleting any messages."""
import base64
import json
from pathlib import Path
from urllib.error import HTTPError
from urllib.parse import quote
from urllib.request import Request, urlopen


class RabbitManagement:
    def __init__(self, url="http://127.0.0.1:15672", username="guest", password="guest"):
        self.url = url.rstrip("/")
        self.headers = {"Authorization": "Basic " + base64.b64encode(f"{username}:{password}".encode()).decode(),
                        "Content-Type": "application/json"}

    def call(self, path, method="GET", body=None):
        data = None if body is None else json.dumps(body).encode()
        try:
            with urlopen(Request(self.url + "/api" + path, data=data, method=method, headers=self.headers), timeout=10) as response:
                raw = response.read()
                return json.loads(raw) if raw else None
        except HTTPError as error:
            raise RuntimeError(f"RabbitMQ HTTP {error.code}: {error.read().decode()}") from error


def subscriptions(options):
    return [(options.get("FormalizationQueue", "reconstruction.formalization"), "reconstruction.proposals.generated"),
            (options.get("FormalizationQueue", "reconstruction.formalization"), "reconstruction.proposals.selected"),
            (options.get("FormalizationQueue", "reconstruction.formalization"), "reconstruction.project.created"),
            (options.get("MonitoringQueue", "reconstruction.monitoring"), "reconstruction.project.formalized"),
            (options.get("MonitoringQueue", "reconstruction.monitoring"), "reconstruction.project.budget-revised"),
            (options.get("ClosureQueue", "reconstruction.closure"), "reconstruction.activity.completed"),
            (options.get("ClosureQueue", "reconstruction.closure"), "reconstruction.project.closed"),
            (options.get("IntegrationsQueue", "reconstruction.integrations"), "reconstruction.#"),
            (options.get("TimelineQueue", "reconstruction.timeline"), "reconstruction.#")]


def configure(broker, options):
    vhost = quote(options.get("VirtualHost", "/"), safe="")
    exchange, dlx = options["Exchange"], options.get("DeadLetterExchange", "reconstruction.events.dlx")
    for name, kind in [(exchange, "topic"), (dlx, "direct")]:
        broker.call(f"/exchanges/{vhost}/{quote(name, safe='')}", "PUT", {"type": kind, "durable": True, "auto_delete": False, "arguments": {}})
    for queue, key in subscriptions(options):
        for name in [queue, queue + ".dlq"]:
            broker.call(f"/queues/{vhost}/{quote(name, safe='')}", "PUT", {"durable": True, "auto_delete": False, "arguments": {}})
        for name, source, routing_key in [(queue, exchange, key), (queue + ".dlq", dlx, queue)]:
            broker.call(f"/bindings/{vhost}/e/{quote(source, safe='')}/q/{quote(name, safe='')}", "POST", {"routing_key": routing_key, "arguments": {}})


if __name__ == "__main__":
    config = json.loads((Path(__file__).resolve().parents[1] / "Reconstruction.Api/appsettings.json").read_text())["Messaging"]
    broker = RabbitManagement(username=config["UserName"], password=config["Password"])
    configure(broker, config)
    print("OK: topic exchange, bindings y colas durables con DLQ configurados sin consumir mensajes.")
