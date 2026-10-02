-- Loopback logs in to Plaud itself (PLAUD_EMAIL / PLAUD_PASSWORD) instead of borrowing the
-- browser's pasted user token. The user session is kept here and renewed with the `pld_urt`
-- refresh cookie; the password is only used when that fails.
-- AccountHash = SHA-256 of the lower-cased PLAUD_EMAIL. Existing rows hold a hash of the old
-- PLAUD_TOKEN, which never matches, so the first sync after the upgrade logs in.
ALTER TABLE PlaudConnection RENAME COLUMN BootstrapTokenHash TO AccountHash;
ALTER TABLE PlaudConnection ADD COLUMN UserToken TEXT NOT NULL DEFAULT '';
ALTER TABLE PlaudConnection ADD COLUMN UserTokenExpiresAt INTEGER NOT NULL DEFAULT 0;
ALTER TABLE PlaudConnection ADD COLUMN UserRefreshToken TEXT NOT NULL DEFAULT '';
ALTER TABLE PlaudConnection ADD COLUMN UserRefreshExpiresAt INTEGER NOT NULL DEFAULT 0;
