# NZBGet Download Mechanism Analysis

## Overview
NZBGet achieves high performance (25MB/s+ on low-end hardware) through a highly optimized, chunk-based download pipeline that minimizes memory copies and method calls. Unlike NzbDav's current implementation, which is heavily reliant on .NET's `StreamReader` and line-based processing, NZBGet interacts directly with raw socket buffers.

## Core Architecture

### 1. Connection Management
*   **Source:** `daemon/nntp/NntpConnection.cpp`, `daemon/connect/Connection.cpp`
*   **Buffers:**
    *   **Control Buffer:** A small internal buffer (10KB) is used for `ReadLine` operations (handshakes, headers).
    *   **Data Buffer:** A large, configurable buffer (default often 128KB+) is used for the article body.
*   **Mechanism:**
    *   The `Connection` class exposes a `TryRecv` method that calls the system `recv` directly into a caller-provided buffer.
    *   It bypasses internal buffering for bulk data transfer.

### 2. The Download Loop
*   **Source:** `daemon/nntp/ArticleDownloader.cpp`
*   **Logic:**
    1.  Sends `BODY <msgid>`.
    2.  Allocates a large `CharBuffer` (Chunk Size).
    3.  **Direct Socket Read:** Calls `m_connection->TryRecv(lineBuf)` to read raw bytes directly from the TCP socket into the buffer.
    4.  **No Line Parsing:** It does *not* read line-by-line to check for `.` termination during the bulk of the transfer.
    5.  **Streaming Decode:** Passes the raw buffer (`buffer`, `len`) directly to `m_decoder.DecodeBuffer`.

### 3. Decoding Strategy
*   **Source:** `daemon/nntp/Decoder.cpp`
*   **State Machine:** The decoder maintains state (`m_state`) to handle split markers (like `=ybegin`, `=yend`, `
.
`) across buffer boundaries.
*   **In-Place Decoding:** `DecodeYenc` (using `YEncode::decode` with SIMD) often decodes in-place or with minimal copying.
*   **Chunk-Based:** It processes whatever amount of data was received in `recv`. It does not wait for full lines.

## Comparison with NzbDav

| Feature | NZBGet | NzbDav (Current Optimized) | NzbDav (Baseline) |
| :--- | :--- | :--- | :--- |
| **I/O Strategy** | Direct `recv` to app buffer | `StreamReader` (64KB buffer) | `StreamReader` (1KB buffer) |
| **Parsing** | Chunk-based (State machine) | Line-based (`ReadLineAsync`) | Line-based (`ReadLineAsync`) |
| **Data Flow** | Socket -> Decoder -> Writer | Socket -> Buffer -> String -> Pipe -> Decoder | Socket -> Buffer -> String -> Pipe -> Decoder |
| **Overhead** | Minimal (System calls + SIMD) | Moderate (String allocs, Encoding) | High (Many syscalls, Allocs) |

## Recommendations for NzbDav

To close the gap to 25MB/s+, NzbDav needs to move away from `StreamReader.ReadLineAsync` for the message body.

### 1. Implement Direct Socket Reading (High Impact)
Instead of `_reader.ReadLineAsync()`, we should access the underlying `NetworkStream` (or `SslStream`) directly.
*   **Action:** In `UsenetClient`, bypass `StreamReader` for the body. Read raw bytes into a pooled `Memory<byte>`.
*   **Status:** Initial attempt with `PipeReader` caused segmentation faults and buffer issues with `SslStream`. Reverted to optimized `StreamReader` (64KB buffer) which yielded 11.6 MB/s. Future work should implement a custom raw buffer reader safer than `PipeReader`.

### 2. Chunk-Based Decoding (High Impact)
Currently, `UsenetClient` manually parses lines to find the `.` terminator and unescape `..`. This requires inspecting every byte and often copying data.
*   **Action:** Update `UsenetClient` to scan the raw buffer for the termination sequence (`
.
`) using vectorized instructions (e.g., `IndexOf`).
*   **Action:** Feed the raw chunks directly to `RapidYencSharp`. Check if `RapidYencSharp` can ignore/handle the CRLF structure or if we need to strip it efficiently.

### 3. Pipeline Optimization
NZBGet decodes immediately after reading. NzbDav writes to a `Pipe`, and a separate `YencStream` reads/decodes.
*   **Action:** Consider decoding *before* writing to the output stream/pipe to reduce the volume of data moving through the system (yEnc is ~2% larger than binary).

## Summary
We have already achieved significant gains (8.8 -> 11.6 MB/s) by optimizing the `StreamReader` buffer. The next leap requires fundamentally changing `UsenetClient` to operate on **Raw Chunks** rather than **Text Lines**.
