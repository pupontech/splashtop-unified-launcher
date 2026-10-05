# Prototype CSV inventory mapping

`CsvInventoryReader.Read(csv, accountId)` converts a user-supplied CSV string into Core `InventoryItem` values without network access or fabricated identifiers.

Supported header aliases (case-insensitive, surrounding heading whitespace ignored):

- Required: `Name` or `Computer Name`
- Optional numeric ID: `ID` or `Computer ID`
- Optional MAC address: `MAC` or `MAC Address`
- Optional: `Group`, `Status`

These header mappings are prototype assumptions; they have not been verified against the current Splashtop export format. If the ID column or cell is absent, the computer ID remains null. Invalid non-empty IDs and malformed CSV rows fail with a record-specific error. CSV quoting supports commas, embedded CR/LF newlines, and doubled quote escapes. A UTF-8 BOM at the start of the supplied text is ignored. Known Core statuses map case-insensitively; blank status stays null and unrecognized status values map to `ComputerStatus.Unknown`.

No sample inventory is bundled or implicitly loaded.
