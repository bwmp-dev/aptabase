"""Run actual ClickHouse error query templates against duplicate fixture rows, without a server."""
import json
import re
import subprocess
import tempfile
from pathlib import Path

root = Path(__file__).resolve().parents[1]
args = {"app_id": "test-app", "start_date": "2026-01-01 00:00:00", "end_date": "2026-12-31 00:00:00", "limit": "1", "offset": "0"}
schema = (root / "etc/clickhouse/0005-error_events.sql").read_text()
schema = schema[:schema.index("ENGINE =")] + "ENGINE = Memory;\n"
fixtures = """
INSERT INTO error_events (error_id,app_id,timestamp,error_message,error_type,ttl)
VALUES ('one','test-app','2026-11-12 01:00:00','first','Type','2027-01-01 00:00:00'),
('one','test-app','2026-11-12 01:00:00','first','Type','2027-01-01 00:00:00'),
('two','test-app','2026-11-12 00:00:00','second','Type','2027-01-01 00:00:00'),
('one','other-app','2026-11-12 01:00:00','other','Type','2027-01-01 00:00:00');
"""
queries = []
for name in ["get_error_count__v1", "get_error_types__v1", "get_errors__v1"]:
    query = (root / f"etc/clickhouse/queries/{name}.liquid").read_text()
    query = re.sub(r"^.*{% if .*?{% endif %}.*$", "", query, flags=re.MULTILINE)
    for key, value in args.items():
        query = query.replace("{{" + key + "}}", value)
    queries.append(query.strip() + " FORMAT JSONEachRow;")
    if name == "get_errors__v1":
        queries.append(query.replace("OFFSET 0", "OFFSET 1").strip() + " FORMAT JSONEachRow;")
with tempfile.TemporaryDirectory(prefix="aptabase-query-test-") as directory:
    Path(directory, "test.sql").write_text(schema + fixtures + "\n".join(queries))
    result = subprocess.run([
        "docker", "run", "--rm", "--pull=never", "--network=none", "--cpus=0.5", "--memory=512m",
        "--mount", f"type=bind,source={directory},target=/work,readonly", "--entrypoint", "clickhouse",
        "clickhouse/clickhouse-server:24.8-alpine", "local", "--max_threads=1", "--queries-file", "/work/test.sql"
    ], capture_output=True, text=True, timeout=60, check=True)
    rows = [json.loads(line) for line in result.stdout.splitlines() if line.startswith("{")]
    assert int(rows[0]["Count"]) == 2, rows
    assert int(rows[1]["Value"]) == 2, rows
    assert rows[2]["ErrorId"] == "one", rows
    assert rows[3]["ErrorId"] == "two", rows
    print("PASS: raw error count/type/list/pagination deduplicate replayed event IDs and isolate apps")
