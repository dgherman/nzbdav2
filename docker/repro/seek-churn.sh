#!/bin/sh
# Drives the strm playback URL the way a player does, and reports what came back.
#
# Why this and not a real player: BufferedSegmentStream is not seekable (CanSeek => false), so every
# seek is a fresh HTTP Range request that builds a new stream and abandons the one in flight. That
# means the whole reported failure mode — "seek a few times and it dies", "open files too quickly in
# succession and VLC errors" — is expressible as a sequence of range requests. This makes it
# repeatable, and puts an HTTP status next to every failure instead of a player's error dialog.
#
# Modes:
#   seek        open, read, jump to a random offset, read, repeat  (Jellyfin fast-forward)
#   rapid-open  open and abandon immediately, back to back         (VLC open-check-close-check)
#   sequential  one continuous read from byte 0                    (the control case)
set -eu

MODE="${MODE:-seek}"
ITERATIONS="${ITERATIONS:-25}"
READ_BYTES="${READ_BYTES:-2097152}"
GAP_SECONDS="${GAP_SECONDS:-1}"

echo "[churn] waiting for a *.strm file to appear under /strm ..."
i=0
STRM=""
while [ -z "$STRM" ]; do
    STRM=$(find /strm -name '*.strm' -type f 2>/dev/null | head -1 || true)
    [ -n "$STRM" ] && break
    i=$((i + 1))
    [ "$i" -gt 300 ] && { echo "[churn] no strm file after 10 minutes — did the queue item fail?"; exit 1; }
    sleep 2
done

URL=$(cat "$STRM")
echo "[churn] target: $STRM"
echo "[churn] url:    $URL"

# Total size, so seek offsets land inside the file rather than past the end (a 416 would look like a
# failure that is really a bad test).
LENGTH=$(curl -fsS -o /dev/null -D - -r 0-0 "$URL" 2>/dev/null \
    | tr -d '\r' | awk -F'/' '/^[Cc]ontent-[Rr]ange:/ {print $2}' | head -1)
if [ -z "${LENGTH:-}" ] || [ "$LENGTH" -le 0 ] 2>/dev/null; then
    echo "[churn] could not read the file length from Content-Range — is the URL reachable from this container?"
    exit 1
fi
echo "[churn] length: $LENGTH bytes"
echo "[churn] mode:   $MODE, $ITERATIONS iterations, ${READ_BYTES}B per read, ${GAP_SECONDS}s gap"
echo ""

ok=0
bad=0
seed=$$

request() {
    # $1 = start byte, $2 = bytes to read, $3 = label
    start="$1"
    want="$2"
    label="$3"
    end=$((start + want - 1))
    [ "$end" -ge "$LENGTH" ] && end=$((LENGTH - 1))

    result=$(curl -sS -o /dev/null -r "${start}-${end}" \
        -w '%{http_code} %{size_download} %{time_starttransfer} %{time_total}' \
        --max-time 120 "$URL" 2>/dev/null || echo "000 0 0 0")

    code=$(echo "$result" | awk '{print $1}')
    size=$(echo "$result" | awk '{print $2}')
    ttfb=$(echo "$result" | awk '{print $3}')
    total=$(echo "$result" | awk '{print $4}')

    if [ "$code" = "206" ] || [ "$code" = "200" ]; then
        ok=$((ok + 1))
        printf '  %-12s offset=%-14s http=%s bytes=%-9s ttfb=%ss total=%ss\n' \
            "$label" "$start" "$code" "$size" "$ttfb" "$total"
    else
        bad=$((bad + 1))
        printf '  %-12s offset=%-14s http=%s bytes=%-9s ttfb=%ss total=%ss   <-- FAILED\n' \
            "$label" "$start" "$code" "$size" "$ttfb" "$total"
    fi
}

n=0
while [ "$n" -lt "$ITERATIONS" ]; do
    n=$((n + 1))
    echo "[churn] iteration $n/$ITERATIONS"

    case "$MODE" in
        seek)
            # Header read, then the body, then a jump — one playback start plus one fast-forward.
            request 0 65536 "open"
            request 0 "$READ_BYTES" "play"
            seed=$(( (seed * 1103515245 + 12345) % 2147483647 ))
            offset=$(( (seed % (LENGTH - READ_BYTES - 1)) ))
            [ "$offset" -lt 0 ] && offset=$(( -offset ))
            request "$offset" "$READ_BYTES" "seek"
            ;;
        rapid-open)
            # Open and walk away, with no gap: what checking a batch of files for samples looks like.
            request 0 65536 "open"
            ;;
        sequential)
            offset=$(( (n - 1) * READ_BYTES ))
            [ "$offset" -ge "$LENGTH" ] && break
            request "$offset" "$READ_BYTES" "read"
            ;;
        *)
            echo "[churn] unknown MODE=$MODE"
            exit 1
            ;;
    esac

    [ "$MODE" = "rapid-open" ] || sleep "$GAP_SECONDS"
done

echo ""
echo "[churn] ═══════════════════════════════════════════════"
echo "[churn] mode=$MODE  ok=$ok  failed=$bad"
echo "[churn] ═══════════════════════════════════════════════"
[ "$bad" -eq 0 ] || exit 1
