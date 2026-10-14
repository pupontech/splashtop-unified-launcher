using System.Globalization;
using System.Text;
using SplashtopUnified.Core;

namespace SplashtopUnified.Prototype;

/// <summary>Reads a CSV inventory using prototype-only header mappings.</summary>
public static class CsvInventoryReader
{
    private static readonly string[] NameAliases = ["Name", "Computer Name"];
    private static readonly string[] IdAliases = ["ID", "Computer ID"];
    private static readonly string[] MacAliases = ["MAC", "MAC Address"];

    /// <summary>
    /// Reads a user-provided CSV inventory. Header aliases are documented prototype
    /// assumptions and have not been verified against current Splashtop exports.
    /// </summary>
    public static IReadOnlyList<InventoryItem> Read(string csv, string accountId)
    {
        ArgumentNullException.ThrowIfNull(csv);
        if (string.IsNullOrWhiteSpace(accountId))
        {
            throw new ArgumentException("A non-blank account ID is required.", nameof(accountId));
        }

        var records = CsvRecords.Parse(csv);
        if (records.Count == 0)
        {
            throw MissingNameHeader();
        }

        var headers = records[0];
        var nameColumn = FindColumn(headers, NameAliases, required: true, "Name") ?? throw MissingNameHeader();
        var idColumn = FindColumn(headers, IdAliases, required: false, "ID");
        var macColumn = FindColumn(headers, MacAliases, required: false, "MAC");
        var groupColumn = FindColumn(headers, ["Group"], required: false, "Group");
        var statusColumn = FindColumn(headers, ["Status"], required: false, "Status");
        var items = new List<InventoryItem>(Math.Max(0, records.Count - 1));

        for (var recordIndex = 1; recordIndex < records.Count; recordIndex++)
        {
            var row = records[recordIndex];
            var recordNumber = recordIndex + 1;
            if (row.Length != headers.Length)
            {
                throw new FormatException(
                    $"Malformed CSV at record {recordNumber}: expected {headers.Length} columns from the header, but found {row.Length}.");
            }

            var name = row[nameColumn];
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new FormatException($"Malformed CSV at record {recordNumber}: the Name value must not be blank.");
            }

            var id = ParseOptionalId(idColumn is { } index ? row[index] : null, recordNumber);
            var macAddress = OptionalCell(macColumn is { } macIndex ? row[macIndex] : null);
            var groupName = OptionalCell(groupColumn is { } groupIndex ? row[groupIndex] : null);
            var status = ParseStatus(statusColumn is { } statusIndex ? row[statusIndex] : null);

            items.Add(new InventoryItem(new Computer(
                AccountId: accountId,
                SplashtopComputerId: id,
                Name: name,
                GroupName: groupName,
                MacAddress: macAddress,
                Status: status)));
        }

        return items.AsReadOnly();
    }

    private static int? FindColumn(string[] headers, string[] aliases, bool required, string fieldName)
    {
        var matches = headers
            .Select((header, index) => (Header: header.Trim(), Index: index))
            .Where(column => aliases.Contains(column.Header, StringComparer.OrdinalIgnoreCase))
            .Select(column => column.Index)
            .ToArray();

        if (matches.Length > 1)
        {
            throw new FormatException(
                $"The CSV header contains more than one column mapped to {fieldName}; keep only one supported alias.");
        }

        if (matches.Length == 0 && required)
        {
            throw MissingNameHeader();
        }

        return matches.Length == 0 ? null : matches[0];
    }

    private static FormatException MissingNameHeader() =>
        new("The CSV header must include a Name or Computer Name column.");

    private static long? ParseOptionalId(string? value, int recordNumber)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!long.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            throw new FormatException(
                $"Invalid ID at CSV record {recordNumber}: '{value}' must be a non-negative whole number.");
        }

        return id;
    }

    private static string? OptionalCell(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static ComputerStatus? ParseStatus(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return Enum.TryParse<ComputerStatus>(trimmed, ignoreCase: true, out var status)
            && Enum.IsDefined(status)
                ? status
                : ComputerStatus.Unknown;
    }

    private static class CsvRecords
    {
        public static List<string[]> Parse(string input)
        {
            if (input.Length > 0 && input[0] == '\uFEFF')
            {
                input = input[1..];
            }

            var records = new List<string[]>();
            var fields = new List<string>();
            var field = new StringBuilder();
            var inQuotedField = false;
            var afterClosingQuote = false;
            var recordPending = false;

            for (var index = 0; index < input.Length; index++)
            {
                var character = input[index];
                if (inQuotedField)
                {
                    if (character == '"')
                    {
                        if (index + 1 < input.Length && input[index + 1] == '"')
                        {
                            field.Append('"');
                            index++;
                        }
                        else
                        {
                            inQuotedField = false;
                            afterClosingQuote = true;
                        }
                    }
                    else
                    {
                        field.Append(character);
                    }

                    recordPending = true;
                    continue;
                }

                if (afterClosingQuote)
                {
                    if (character == ',')
                    {
                        FinishField(fields, field);
                        afterClosingQuote = false;
                        recordPending = true;
                        continue;
                    }

                    if (IsNewline(character))
                    {
                        FinishRecord(records, fields, field);
                        afterClosingQuote = false;
                        recordPending = false;
                        index = SkipLfAfterCr(input, index, character);
                        continue;
                    }

                    throw MalformedCsv(records.Count + 1, "unexpected character after a closing quote");
                }

                if (character == '"')
                {
                    if (field.Length != 0)
                    {
                        throw MalformedCsv(records.Count + 1, "a quote appeared inside an unquoted field");
                    }

                    inQuotedField = true;
                    recordPending = true;
                    continue;
                }

                if (character == ',')
                {
                    FinishField(fields, field);
                    recordPending = true;
                    continue;
                }

                if (IsNewline(character))
                {
                    FinishRecord(records, fields, field);
                    recordPending = false;
                    index = SkipLfAfterCr(input, index, character);
                    continue;
                }

                field.Append(character);
                recordPending = true;
            }

            if (inQuotedField)
            {
                throw MalformedCsv(records.Count + 1, "a quoted field was not closed before end of input");
            }

            if (recordPending)
            {
                FinishRecord(records, fields, field);
            }

            return records;
        }

        private static bool IsNewline(char character) => character is '\r' or '\n';

        private static int SkipLfAfterCr(string input, int index, char character) =>
            character == '\r' && index + 1 < input.Length && input[index + 1] == '\n'
                ? index + 1
                : index;

        private static void FinishField(List<string> fields, StringBuilder field)
        {
            fields.Add(field.ToString());
            field.Clear();
        }

        private static void FinishRecord(List<string[]> records, List<string> fields, StringBuilder field)
        {
            FinishField(fields, field);
            records.Add(fields.ToArray());
            fields.Clear();
        }

        private static FormatException MalformedCsv(int recordNumber, string detail) =>
            new($"Malformed CSV at record {recordNumber}: {detail}.");
    }
}
