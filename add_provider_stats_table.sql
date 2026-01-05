-- Manual migration: Add NzbProviderStats table
-- Run this SQL directly on your database instead of using EF migrations

CREATE TABLE IF NOT EXISTS "NzbProviderStats" (
    "JobName" TEXT NOT NULL,
    "ProviderIndex" INTEGER NOT NULL,
    "SuccessfulSegments" INTEGER NOT NULL,
    "FailedSegments" INTEGER NOT NULL,
    "TotalBytes" INTEGER NOT NULL,
    "TotalTimeMs" INTEGER NOT NULL,
    "LastUsed" INTEGER NOT NULL,
    PRIMARY KEY ("JobName", "ProviderIndex")
);

CREATE INDEX IF NOT EXISTS "IX_NzbProviderStats_JobName" ON "NzbProviderStats" ("JobName");
CREATE INDEX IF NOT EXISTS "IX_NzbProviderStats_JobName_LastUsed" ON "NzbProviderStats" ("JobName", "LastUsed");

-- Verify the table was created
SELECT 'Table created successfully!' as status;
