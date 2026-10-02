-- Recording's local UTC offset from Plaud (`timezone` hours + `zonemins`), for local dates in titles/output.
ALTER TABLE Recordings ADD COLUMN UtcOffsetMinutes INTEGER NULL;
