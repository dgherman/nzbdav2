<p align="center">
  <img width="1101" height="238" alt="image" src="https://github.com/user-attachments/assets/b14165f4-24ff-4abe-8af6-3ca852e781d4" />
</p>

# Nzb Dav

NzbDav is a WebDAV server that allows you to mount and browse NZB documents as a virtual file system without downloading. It's designed to integrate with other media management tools, like Sonarr and Radarr, by providing a SABnzbd-compatible API. With it, you can build an infinite Plex or Jellyfin media library that streams directly from your usenet provider at maxed-out speeds, without using any storage space on your own server.

Check the video below for a demo:

https://github.com/user-attachments/assets/be3e59bc-99df-440d-8144-43b030a4eaa4

> **Attribution**: The video above contains clips of [Sintel (2010)](https://studio.blender.org/projects/sintel/), by Blender Studios, used under [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/)


# Key Features

* 📁 **WebDAV Server** - *Host your virtual file system over HTTP(S)*
* ☁️ **Mount NZB Documents** - *Mount and browse NZB documents without downloading.*
* 📽️ **Full Streaming and Seeking Abilities** - *Jump ahead to any point in your video streams.*
* 🗃️ **Stream archived contents** - *View, stream, and seek content within RAR and 7z archives.*
* 🔓 **Stream password-protected content** - *View, stream, and seek within password-protected archives.*
* 💙 **Healthchecks & Repairs** - *Automatically replace content that has been removed from your usenet provider*
* 🧩 **SABnzbd-Compatible API** - *Use NzbDav as a drop-in replacement for sabnzbd.*
* 🙌 **Sonarr/Radarr Integration** - *Configure it once, and leave it unattended.*

# Fork Enhancements

This fork introduces significant architectural and feature improvements over the original implementation:

### 🚀 Smart Buffering Engine
*   **Read-Ahead Buffering**: Implements a custom `BufferedSegmentStream` that pre-fetches segments into memory, ensuring smooth playback and eliminating stutter during high-bitrate streams.
*   **Optimized Seeking**: Intelligent segment seeking and stream management for faster seek times.

### 🧠 Intelligent Connection Management
*   **Priority Queuing**: Dynamically prioritizes active streaming connections over background maintenance tasks (like health checks and queue repairs) to prevent playback interruptions.
*   **Load Balancing**: Smart distribution of requests across available Usenet providers and connections.

### 📊 Advanced UI Dashboard
*   **Real-Time Monitoring**: Live visualization of bandwidth usage and active connections.
*   **Granular Connection Insights**: See exactly what each connection is doing (Buffering, Streaming, Repairing) and details about the file being accessed, including its **Usenet age** (e.g., "5d ago").
*   **Server Identification**: Provider cards now display the specific server host address (e.g., `news.example.com`) instead of generic labels, making it easier to track performance across different backbones.
*   **Interactive System Logs**: A built-in log console with per-level filtering (Debug, Info, Error) and optimized memory storage (10k records per level) for easier troubleshooting.

### 🛠️ Modern Tech Stack
*   **Backend**: Upgrade to **.NET 9.0** for improved performance and resource efficiency.
*   **Frontend**: Rebuilt using **React Router v7** with Server-Side Rendering (SSR), React 19, and Bootstrap 5/Tailwind for a snappy, modern user experience.


# Getting Started

The easiest way to get started is by using the official Docker image.

To try it out, run the following command to pull and run the image with port `3000` exposed:

```bash
nzbdav/nzbdav:alpha

If you are developing or prefer to build the image locally:

```bash
docker build -t local/nzbdav:3 .
```

```

And if you would like to persist saved settings, attach a volume at `/config`

```
mkdir -p $(pwd)/nzbdav && \
docker run --rm -it \
  -v $(pwd)/nzbdav:/config \
  -e PUID=1000 \
  -e PGID=1000 \
  -p 3000:3000 \
  nzbdav/nzbdav:alpha
```
After starting the container, be sure to navigate to the Settings page on the UI to finish setting up your usenet connection settings.

<p align="center">
    <img width="600" alt="settings-page" src="https://github.com/user-attachments/assets/ca0a7fa7-be43-412d-9fec-eda24eb25fdb" />
</p>

You'll also want to set up a username and password for logging in to the webdav server

<p align="center">
    <img width="600" alt="webdav-settings" src="https://github.com/user-attachments/assets/833b382c-4e1d-480a-ac25-b9cc674baea4" />
</p>

# RClone

In order to integrate with Plex, Radarr, and Sonarr, you'll need to mount the webdav server onto your filesystem. 

```
[nzb-dav]
type = webdav
url = // your endpoint
vendor = other
user = // your webdav user
pass = // your rclone-obscured password https://rclone.org/commands/rclone_obscure
```


Below are the RClone settings I use.  
```
--vfs-cache-mode=full
--buffer-size=1024
--dir-cache-time=1s
--vfs-cache-max-size=5G
--vfs-cache-max-age=180m
--links
--use-cookies
--allow-other
--uid=1000
--gid=1000
```

* The `--links` setting in RClone is important. It allows *.rclonelink files within the webdav to be translated to symlinks when mounted onto your filesystem.

    > NOTE: Be sure to use an updated version of rclone that supports the `--links` argument.
    > * Version `v1.70.3` has been known to support it.
    > * Version `v1.60.1-DEV` has been known _not_ to support it.

* The `--use-cookies` setting in RClone is also important. Without it, RClone is forced to re-authenticate on every single webdav request, slowing it down considerably.
* The `--allow-other` setting is not required, but it should help if you find that your containers are not able to see the mount contents due to permission issues.

**Optional**
* The `--vfs-cache-max-size=5G` Can be added to set the max total size of objects in the cache (default off), thus possibly consuming all free space.
* The `--vfs-cache-max-age=180m` Can be added to set the max time since last access of objects in the cache (default 1h0m0s). 


# Radarr / Sonarr

Once you have the webdav mounted onto your filesystem (e.g. accessible at `/mnt/nzbdav`), you can configure NZB-Dav as your download-client within Radarr and Sonarr, using the SABnzbd-compatible api.

<p align="center">
    <img width="600" alt="webdav-settings" src="https://github.com/user-attachments/assets/5ef6a362-7393-4b98-980a-a9e0e159ed72" />
</p>

### Steps
* Radar will send an *.nzb to NZB-Dav to "download"
* NZB-Dav will mount the nzb onto the webdav without actually downloading it.
* RClone will make the nzb contents available to your filesystem by streaming, without using any storage space on your server.
* NZB-Dav will tell Radarr that the "download" has completed within the `/mnt/nzbdav/completed-symlinks` folder.
* Radarr will grab the symlinks from `/mnt/nzbdav/completed-symlinks` and will move them to wherever you have your media library.
* The symlinks always point to the `/mnt/nzbdav/.ids` folder which contains the streamable content.
* Plex accesses one of the symlinks from your media library, it will automatically fetch and stream it from the mounted webdav.


# Example Docker Compose Setup
Fully containerized setup for docker compose. 

See rclone [docs](https://rclone.org/docker/) for more info.

Verify FUSER driver is installed:
```
$ fusermount3 --version
```

Install FUSER driver if needed:
- `sudo pacman -S fuse3` OR
- `sudo dnf install fuse3` OR
- `sudo apt install fuse3` OR
- `sudo apk add fuse3`
- etc...


Install the rclone volume plugin:
```
$ sudo mkdir -p /var/lib/docker-plugins/rclone/config
$ sudo mkdir -p /var/lib/docker-plugins/rclone/cache
$ docker plugin install rclone/docker-volume-rclone:amd64 args="-v --links --buffer-size=1024" --alias rclone --grant-all-permissions
```
You can set any options here in the `args="..."` section. The command above sets bare minimum, and must be accompanied with more options in the example compose file.

Move or create `rclone.conf` in `/var/lib/docker-plugins/rclone/config/`. Contents should follow the [example](https://github.com/nzbdav-dev/nzbdav?tab=readme-ov-file#rclone).

In your compose.yaml... **NOTE: Ubuntu container is not required, and is only included for testing the rclone volume.**
```yml
services:
  nzbdav:
    image: ghcr.io/nzbdav-dev/nzbdav
    environment:
      - PUID=1000
      - PGID=1000
    ports:
      - 3000:3000
    volumes:
      - /opt/stacks/nzbdav:/config
    restart: unless-stopped

  ubuntu:
    image: ubuntu
    command: sleep infinity
    volumes:
      - nzbdav:/mnt/nzbdav
    environment:
      - PUID=1000
      - PGID=1000

  radarr:
    volumes:
      - nzbdav:/mnt/nzbdav # Change target path based on SABnzbd rclone mount directory setting.

# the rest of your config ...

volumes:
  nzbdav:
    driver: rclone
    driver_opts:
      remote: 'nzb-dav:'
      allow_other: 'true'
      vfs_cache_mode: off
      dir_cache_time: 1s
      allow_non_empty: 'true'
      uid: 1000
      gid: 1000

```

To verify proper rclone volume creation:
```
$ docker exec -it <ubuntu container name> bash
$ ls -la /mnt/nzbdav
```

## Accessing the rclone volume from a separate stack.
Note: Your rclone volume **must** be already created by another stack, for example: 

- Media backend: nzbdav + sonarr + radarr <--- This stack is creating the rclone volume
- Media frontend: jellyfin <--- Mounts the external arrstack rclone volume

To do so, see the bottom 11 lines in the example compose file in the above section.

The example below uses ubuntu again, but the concept is the same for a different container such as sonarr.


Find the stack name that creates the rclone volume:
```
$ docker-compose ls
```

Combine in the new separate compose file:
```yml
services:
  ubuntu:
    image: ubuntu
    container_name: ubuntu
    command: sleep infinity
    volumes:
      - nzbdav:/mnt/nzbdav # -- IMPORTANT --
    environment:
      - PUID=1000 # Must match UID value from volume in the stack creating the volume (driver_opts).
      - PGID=1000 # Must match GID value from volume in the stack creating the volume (driver_opts).

volumes:
  nzbdav:
    name: <STACK NAME>_nzbdav # See above for finding the stack name. # -- IMPORTANT --
    external: true # -- IMPORTANT --
```


# More screenshots
<img width="300" alt="onboarding" src="https://github.com/user-attachments/assets/4ca1bfed-3b98-4ff2-8108-59ed07a25591" />
<img width="300" alt="queue and history" src="https://github.com/user-attachments/assets/4f69f8dd-0dba-47b4-b02f-3e83ead293db" />
<img width="300" alt="dav-explorer" src="https://github.com/user-attachments/assets/54a1d49b-8a8d-4306-bcda-9740bd5c9f52" />
<img width="300" alt="health-page" src="https://github.com/user-attachments/assets/709b81c2-550b-47d0-ad50-65dc184cd3fa" />

# Changelog

## v0.1.0 (2025-12-06)
*   **Smart Buffering Engine**: Added `BufferedSegmentStream` for read-ahead buffering and optimized seeking.
*   **Intelligent Connection Management**: Implemented priority queuing for streaming vs. background tasks and smart load balancing.
*   **UI Stats Dashboard**:
    *   Added real-time bandwidth and connection monitoring.
    *   Added granular connection details including file age (e.g., "5d ago").
    *   Updated provider cards to display specific server host addresses.
    *   Added an idle latency check (ping) for inactive servers.
*   **System Logs**: Enhanced log console with per-level filtering and optimized storage (10k limit per level).
*   **Tech Stack**: Upgraded backend to .NET 9.0 and frontend to React Router v7.

## v0.1.1 (2025-12-07)
*   **Missing Articles Log**: Added a persistent log and UI table to track missing article events, useful for diagnosing provider retention issues.
*   **Connection Status Badges**: Added visual indicators ("Backup", "Secondary") to active connections to show when a provider is being used as a fallback or for load-balancing retries.
*   **Persistent Logging**: Missing article events are now saved to the database to survive restarts.

## v0.1.2 (2025-12-07)
*   **Latency Measurement Refinement**: Switched latency calculation to an Exponential Moving Average (EMA) for smoother, more stable readings, especially during periods of sparse activity. Latency is now recorded for lightweight NNTP operations only and includes periodic ping checks for active providers to ensure continuous measurement.
*   **Improved Timeout Logging**: Timeout messages in the logs now include the specific provider's server address, instead of just its index, for easier identification and debugging.
*   **Connection Status Accuracy**: Corrected logic for "Backup" and "Secondary" connection badges to accurately reflect provider usage during fallback and load-balancing scenarios.
*   **Missing Article Events Persistence**: Ensured missing article events are persisted to the database, preventing loss of data on application restart.

## v0.1.3 (2025-12-08)
*   **Missing Articles Logging Fix**: Fixed critical bug where missing articles were not being logged to the database due to `ProviderErrorService` not being passed to `MultiProviderNntpClient`.
*   **Enhanced Missing Articles Detection**: Added Info level logging when missing articles are detected, including provider hostname, segment ID, and filename for better diagnostics.
*   **Improved Timeout Diagnostics**: Timeout logs now correctly show provider server addresses by embedding the information in exception messages, resolving issues with context disposal during exception unwinding.
*   **Expandable Missing Articles Table**: Reimplemented the Missing Articles UI table with grouping by provider and filename, featuring expandable tree view to show individual segment details and segment count badges.
*   **Log Management**: Added "Clear Log" buttons to both Missing Articles and Deleted Files tables, with backend DELETE endpoints (`/api/stats/missing-articles` and `/api/stats/deleted-files`) for easy log maintenance.
*   **Health Check Context Fix**: Health check operations now properly set connection context with actual item names instead of generic "Health Check" label, improving log readability.


## v0.1.17 (2025-12-17)
*   **Logging**: Removed `UsenetArticleNotFoundException` error logs from the internal worker loop in `BufferedSegmentStream`. These errors are expected when searching for missing articles across providers and were causing unnecessary noise before the final result was determined.

## v0.1.16 (2025-12-17)
*   **Logging**: Fixed excessive error logging of `System.TimeoutException` in `ThreadSafeNntpClient.GetSegmentStreamAsync`. This ensures that expected timeout errors (often transient) are rethrown to higher layers for handling without generating full stack traces in the low-level client logs.

## v0.1.15 (2025-12-17)
*   **Logging**: Suppressed stack traces for `TimeoutException` in `BufferedSegmentStream`. This further reduces log noise for connection timeouts that bubble up from the low-level client, logging them as warnings instead of errors with full stack traces.

## v0.1.14 (2025-12-17)
*   **Logging**: Suppressed stack traces for `System.IO.IOException` (e.g., "Connection aborted") in `ThreadSafeNntpClient`. This reduces log noise for expected transient network issues during streaming.

## v0.1.13 (2025-12-17)
*   **Logging**: Fixed excessive error logging by ensuring `UsenetArticleNotFoundException` is rethrown without logging in the low-level `ThreadSafeNntpClient`. This prevents stack traces from appearing in the logs for every missing article when expected failures are handled by the multi-provider client.

## v0.1.12 (2025-12-17)
*   **Logging**: Added explicit log messages when a `UsenetArticleNotFoundException` triggers an immediate health check for a `DavItem`. This provides clearer visibility into the system's proactive repair actions.

## v0.1.11 (2025-12-17)
*   **Logging**: Enhanced `UsenetArticleNotFoundException` log messages to include the relevant Job Name/Nzb Name. This provides better context for identifying which specific content is missing articles, across both API requests and streaming operations.

## v0.1.10 (2025-12-17)
*   **Logging**: Suppressed stack traces for `UsenetArticleNotFoundException` in `BufferedSegmentStream`. This reduces log noise when all providers fail to find an article during streaming, while still logging the error message.

## v0.1.9 (2025-12-17)
*   **Maintenance**: Improved automatic cleanup of orphaned "Missing Article" logs. The system now periodically scans for and removes error logs associated with files that have been deleted or have invalid (Empty) Dav IDs, addressing the issue of persistent "00000000..." entries in the Missing Articles table.

## v0.1.8 (2025-12-17)
*   **Performance**: Significantly improved loading performance of the 'Deleted Files' table by adding a database index on `HealthCheckResult` (`RepairStatus`, `CreatedAt`).

## v0.1.7 (2025-12-17)
*   **Logging**: Suppressed stack traces for generic exceptions within `GetSegmentStreamAsync` in `ThreadSafeNntpClient` when the stack trace matches a specific pattern, to reduce log noise for internal client errors.

## v0.1.6 (2025-12-17)
*   **Logging**: Suppressed stack traces for `System.TimeoutException` originating from Usenet provider timeouts to reduce log noise.

## v0.1.5 (2025-12-16)
*   **UI Improvements**: Renamed "Job Name" column to "Scene Name" in the Mapped Files table for clarity.
*   **Performance**: The Mapped Files table now uses a persistent database table (`LocalLinks`), eliminating "Initializing" delays and high memory usage for large libraries. Deletions are automatically synchronized.
*   **Accuracy**: The Missing Articles table now dynamically checks import status against the persistent mapped files table, ensuring the "Imported" badge is always up-to-date.
*   **Log Management**: Added "Delete" button to the Missing Articles table to remove individual file entries from the log.
*   **Fix**: Resolved a `NullReferenceException` in `ArrClient` when attempting to mark history items as failed if the grab event could not be found in history.
*   **Media Management**: Added "Repair" button to the Mapped Files table, allowing manual triggering of file repair (delete & blacklist/search) directly from the mapping view.
*   **Testing**: Added "Manual Repair / Blacklist" tool in Settings > Radarr/Sonarr to test blacklisting logic by manually triggering repair for a release name.
*   **Robustness**: Enhanced repair logic (Health Check & Manual) to better handle Docker volume mapping discrepancies. It now attempts to find media items by filename or folder name if exact path matching fails.
*   **UI Clarity**: Changed the "Imported" column in the Missing Articles table to "Mapped" with a badge indicator for better clarity on files used by Sonarr/Radarr.
*   **Diagnostics**: Added a "NzbDav Path" column to the Missing Articles table, displaying the internal path (`/mnt/remote/nzbdav/.ids/...`) for easier debugging and reference.
*   **UI Improvements**: Cells in the Missing Articles table (Job Name, Filename, NzbDav Path) are now expandable on click to view full text that doesn't fit the column width.
*   **Maintenance**: Added "Connection Management" tools in Settings > Maintenance to forcefully reset active connections by type (Queue, Health Check, Streaming), useful for clearing stalled items.
*   **Log Management**: Added "Orphaned (Empty ID)" filter to the Missing Articles table to easily find errors associated with deleted files. Also added "Delete Selected" functionality for bulk log cleanup.
*   **Filtering**: Added a "Not Mapped" filter to the Missing Articles table to show files not linked in Sonarr/Radarr, and a "Blocked Only" checkbox for quick access to critical errors.
*   **Bug Fix**: Corrected the internal path format in the Missing Articles table to correctly show the nested ID structure (e.g., `/.ids/1/2/3/4/5/uuid...`).
*   **Optimization**: Renamed `BackfillIsImportedStatusAsync` to `BackfillDavItemIdsAsync` and streamlined the startup backfill process to focus on populating missing DavItem IDs for mapped files logic.
*   **Diagnostics**: Enhanced logging in `ArrClient` (Sonarr/Radarr) to dump recent history records when a "grab event" cannot be found during the repair/blacklist process, aiding in troubleshooting matching issues.
## v0.1.4 (2025-12-08)
*   **Performance Optimization**: Addressed slow UI loading for stats pages by refactoring backend services to use asynchronous database queries and enabling SQLite WAL (Write-Ahead Logging) mode for improved concurrency.
*   **Deleted Files UI Improvements**: The "Deleted Files" table now identifies and displays the original NZB/Job name for files with obfuscated filenames, making it easier to track which content was removed.
*   **Log Noise Reduction**: Reduced excessive logging for missing articles in the backend.
