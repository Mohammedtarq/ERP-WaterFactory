#!/usr/bin/env bash
# يشغّل SQL Server 2022 في Docker، ينشئ قاعدة بيانات نظيفة، ينفّذ الملفات 00 → 09 ثم الاختبارات.
# الاستخدام: ./tests/run_tests.sh
set -euo pipefail
cd "$(dirname "$0")/.."

CONTAINER=erp-sql-test
PASS='Test_Pass123!'
DB=ERP_Test_$(date +%s)

if ! docker ps --format '{{.Names}}' | grep -qx "$CONTAINER"; then
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$CONTAINER" -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASS" \
    mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi

sq() { docker exec -i "$CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$PASS" -b -f 65001 "$@"; }

for _ in $(seq 1 60); do sq -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done

sq -Q "IF DB_ID('ERP_ControlDB') IS NULL EXEC('CREATE DATABASE ERP_ControlDB')" >/dev/null
sed '/^CREATE DATABASE ERP_ControlDB;/d' 00_control_db.sql | sq -d ERP_ControlDB -Q "IF OBJECT_ID('Projects') IS NULL SELECT 1" >/dev/null

sq -Q "CREATE DATABASE [$DB] COLLATE Arabic_CI_AS" >/dev/null
for f in 0[1-9]_*.sql; do
  echo "▶ $f"
  sq -d "$DB" < "$f" >/dev/null
done

echo "▶ tests/test_sales.sql"
status=0
sq -d "$DB" < tests/test_sales.sql || status=$?
sq -Q "DROP DATABASE [$DB]" >/dev/null
exit $status
