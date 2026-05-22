import channelsByArtcc from "../validation-channels.json";

/** ARTCC code → Discord channel snowflake */
export const VALIDATION_CHANNELS_BY_ARTCC = channelsByArtcc;

/** Discord channel snowflake → ARTCC code (for interaction routing) */
export const VALIDATION_CHANNELS = Object.fromEntries(
  Object.entries(channelsByArtcc).map(([artcc, channelId]) => [channelId, artcc]),
);

export const VALIDATION_ARTCCS = Object.keys(channelsByArtcc).sort();
