-- Sharing flag: true when the point was captured with live location sharing on (visible history),
-- false when it was captured only for AEC or Location Based Alerts ("not shared").
-- Rows stored before this migration were all shared history. The default is dropped afterwards so
-- every insert has to state the flag explicitly.
ALTER TABLE location_history ADD COLUMN shared boolean NOT NULL DEFAULT true;
ALTER TABLE location_history ALTER COLUMN shared DROP DEFAULT;
