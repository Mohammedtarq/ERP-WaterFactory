#!/usr/bin/env bash
# يشغّل كل الاختبارات على SQL Server 2022 حقيقي داخل Docker:
#   1) ملفات Database/00 → 10 على قاعدة نظيفة + اختبارات SQL (tests/test_sales.sql)
#   2) بناء ERP.Data و ERP.SeedTool + اختبارات تكامل C# (tests/ERP.Data.IntegrationTests)
# الاستخدام: ./tests/run_tests.sh      (يتطلب Docker فقط)
set -euo pipefail
cd "$(dirname "$0")/.."
ROOT=$(pwd)

SQL_CONTAINER=erp-sql-test
PASS='Test_Pass123!'
PORT=${ERP_TEST_SQL_PORT:-14333}
STAMP=$(date +%s)

if ! docker ps --format '{{.Names}}' | grep -qx "$SQL_CONTAINER"; then
  docker rm -f "$SQL_CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$SQL_CONTAINER" -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$PASS" \
    -p "$PORT:1433" mcr.microsoft.com/mssql/server:2022-latest >/dev/null
fi

# -I = QUOTED_IDENTIFIER ON (نفس سلوك SSMS)
sq() { docker exec -i "$SQL_CONTAINER" /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$PASS" -b -I -f 65001 "$@"; }

for _ in $(seq 1 60); do sq -Q "SELECT 1" >/dev/null 2>&1 && break; sleep 2; done

new_project_db() {
  sq -Q "CREATE DATABASE [$1] COLLATE Arabic_CI_AS" >/dev/null
  for f in Database/0[1-9]_*.sql Database/1[0-9]_*.sql; do
    [ -e "$f" ] || continue
    sq -d "$1" < "$f" >/dev/null
  done
}

echo "▶ Database/00_control_db.sql"
sq -Q "IF DB_ID('ERP_ControlDB') IS NOT NULL BEGIN ALTER DATABASE ERP_ControlDB SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE ERP_ControlDB; END" >/dev/null
sq < Database/00_control_db.sql >/dev/null

status=0

echo "▶ اختبارات SQL"
SQL_DB="ERP_SqlTest_$STAMP"
new_project_db "$SQL_DB"
sq -d "$SQL_DB" < tests/test_sales.sql | grep -E '✓|✗|✅|❌|رسالة' || status=1
sq -Q "DROP DATABASE [$SQL_DB]" >/dev/null

echo "▶ بناء C# واختبارات التكامل"
NET_DB="ERP_NetTest_$STAMP"
new_project_db "$NET_DB"
PROXY_ARGS=()
if [ -n "${HTTPS_PROXY:-}" ]; then
  PROXY_ARGS+=(-e "HTTPS_PROXY=$HTTPS_PROXY")
  [ -f /root/.ccr/ca-bundle.crt ] && PROXY_ARGS+=(-e SSL_CERT_FILE=/ca.crt -v /root/.ccr/ca-bundle.crt:/ca.crt:ro)
fi
docker run --rm --network host "${PROXY_ARGS[@]}" \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  -e "ERP_TEST_CONNECTION=Server=localhost,$PORT;Database=$NET_DB;User Id=sa;Password=$PASS;TrustServerCertificate=True;" \
  -v "$ROOT":/src -v erp-nuget:/root/.nuget -w /src mcr.microsoft.com/dotnet/sdk:9.0 sh -c '
    set -e
    dotnet build ERP.Data/ERP.Data.csproj -nologo -v q -warnaserror
    dotnet build ERP.SeedTool/ERP.SeedTool.csproj -nologo -v q
    dotnet test tests/ERP.Data.IntegrationTests -nologo -v q --logger "console;verbosity=normal"
  ' || status=1
sq -Q "DROP DATABASE [$NET_DB]" >/dev/null

[ $status -eq 0 ] && echo "✅ كل الاختبارات نجحت" || echo "❌ يوجد فشل — راجع المخرجات أعلاه"
exit $status
