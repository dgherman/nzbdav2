#!/bin/sh
# Configures a fresh NzbDav for the strm deployment and loads one release into it.
# Everything goes through the HTTP API — the database is never touched directly.
set -eu

API="http://nzbdav:8080"
NZB="/mock/mock.nzb"

echo "[provision] waiting for the backend to answer /health ..."
i=0
until curl -fsS "$API/health" >/dev/null 2>&1; do
    i=$((i + 1))
    [ "$i" -gt 120 ] && { echo "[provision] backend never became healthy"; exit 1; }
    sleep 2
done

if [ "${PROVIDER_MODE}" = "mock" ]; then
    echo "[provision] waiting for the mock provider to publish its nzb ..."
    i=0
    until [ -f "$NZB" ]; do
        i=$((i + 1))
        [ "$i" -gt 120 ] && { echo "[provision] mock nzb never appeared at $NZB"; exit 1; }
        sleep 2
    done
fi

# Type 1 = Pooled. See backend/Models/ProviderType.cs.
PROVIDERS=$(cat <<EOF
{"Providers":[{"Type":1,"Host":"${USENET_HOST}","Port":${USENET_PORT},"UseSsl":${USENET_SSL},"User":"${USENET_USER}","Pass":"${USENET_PASS}","MaxConnections":${USENET_CONNECTIONS}}]}
EOF
)

echo "[provision] writing config (strm mode, base url ${BASE_URL}, ${USENET_CONNECTIONS} connections) ..."
curl -fsS -X POST "$API/api/update-config" \
    -H "x-api-key: ${API_KEY}" \
    --form-string "usenet.providers=${PROVIDERS}" \
    --form-string "usenet.connections-per-stream=${CONNECTIONS_PER_STREAM}" \
    --form-string "usenet.total-streaming-connections=${USENET_CONNECTIONS}" \
    --form-string "api.import-strategy=strm" \
    --form-string "api.completed-downloads-dir=/strm" \
    --form-string "general.base-url=${BASE_URL}" \
    --form-string "api.ensure-importable-media=false" \
    --form-string "api.sample-filter-enabled=true" \
    --form-string "usenet.prefetch-window=${PREFETCH_WINDOW:-0}" \
    > /dev/null
echo "[provision] config written."

if [ "${PROVIDER_MODE}" != "mock" ]; then
    echo "[provision] PROVIDER_MODE=${PROVIDER_MODE}: add your own nzb via the UI on :3000 or"
    echo "[provision]   curl -F nzbFile=@yours.nzb '$API/api?mode=addfile&apikey=${API_KEY}'"
    exit 0
fi

echo "[provision] queueing mock.nzb ..."
curl -fsS -X POST "$API/api?mode=addfile&cat=movies&priority=0&pp=0&apikey=${API_KEY}" \
    -F "nzbFile=@${NZB};type=application/x-nzb" \
    > /dev/null

echo "[provision] queued. Watch progress at http://localhost:${WEB_PORT:-3000}/queue"
echo "[provision] strm files will appear under the 'strm' volume as items complete."
