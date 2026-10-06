-- Waveform envelope for the player: JSON array of numbers in [0, 1] (see Integrations/AudioFiles.fs).
ALTER TABLE Recordings ADD COLUMN Peaks TEXT NULL;
