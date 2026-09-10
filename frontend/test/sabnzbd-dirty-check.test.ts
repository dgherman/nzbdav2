import { test } from "node:test";
import assert from "node:assert/strict";
import { isSabnzbdSettingsUpdated } from "../app/routes/settings/sabnzbd/sabnzbd-dirty-check.ts";

// Issue #37: editing only the SABnzbd "User Agent" (api.user-agent) must mark the
// form dirty so the Save button enables.

const base: Record<string, string> = {
  "api.key": "abc",
  "api.categories": "",
  "api.manual-category": "uncategorized",
  "api.user-agent": "",
  "rclone.mount-dir": "",
  "api.max-queue-connections": "",
  "api.ensure-importable-video": "true",
  "api.ensure-article-existence": "false",
  "api.ignore-history-limit": "true",
  "api.duplicate-nzb-behavior": "increment",
  "api.download-extension-blacklist": ".nfo, .par2, .sfv",
  "api.download-filename-blacklist": "",
  "api.sample-filter-enabled": "true",
  "api.import-strategy": "symlinks",
  "api.completed-downloads-dir": "",
  "general.base-url": "",
  "api.history-retention-hours": "",
};

test("returns false when nothing differs", () => {
  assert.equal(isSabnzbdSettingsUpdated(base, { ...base }), false);
});

test("returns true when only api.user-agent differs", () => {
  assert.equal(
    isSabnzbdSettingsUpdated(base, { ...base, "api.user-agent": "Sonarr/4.0" }),
    true,
  );
});

test("still returns true for a previously-covered key (regression guard)", () => {
  assert.equal(
    isSabnzbdSettingsUpdated(base, { ...base, "api.categories": "tv,movies" }),
    true,
  );
});
