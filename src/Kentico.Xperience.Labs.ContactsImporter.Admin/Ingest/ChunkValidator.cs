using System.Buffers;
using System.Text.Json;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// What a chunk record is validated against: the job's key field and mapped targets, with the value length
/// limits of the contact class.
/// </summary>
internal sealed class ChunkValidationContext
{
    private ChunkValidationContext(string keyField, string[] targets, int?[] maxValueLengths, int keyIndex, int emailIndex)
    {
        KeyField = keyField;
        Targets = targets;
        MaxValueLengths = maxValueLengths;
        KeyIndex = keyIndex;
        EmailIndex = emailIndex;
    }

    public string KeyField { get; }

    /// <summary>Mapped target field names; the only names allowed in <c>v</c>.</summary>
    public string[] Targets { get; }

    /// <summary>Longest accepted value per target (same order as <see cref="Targets"/>); <c>null</c> = no field limit.</summary>
    public int?[] MaxValueLengths { get; }

    public int KeyIndex { get; }

    /// <summary>Index of the email field in <see cref="Targets"/>, or -1 when it is not mapped.</summary>
    public int EmailIndex { get; }

    /// <summary>The <c>invalidEmail</c> flag only makes sense when email is mapped and is not the key.</summary>
    public bool InvalidEmailFlagAllowed => EmailIndex >= 0 && EmailIndex != KeyIndex;

    /// <summary>
    /// Builds the context. Fails when the job maps a field that is not (or no longer) a mappable contact field,
    /// maps a field twice, or the key field is not mapped.
    /// </summary>
    public static bool TryCreate(ImportJob job, IReadOnlyList<ContactFieldDefinition> fields, out ChunkValidationContext? context, out string? error)
    {
        context = null;
        var fieldsByName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        string[] targets = [.. job.Mapping.Select(m => m.Target)];
        int?[] maxValueLengths = new int?[targets.Length];

        for (int i = 0; i < targets.Length; i++)
        {
            if (!fieldsByName.TryGetValue(targets[i], out var field))
            {
                error = $"Mapped field '{targets[i]}' is not an importable contact field.";
                return false;
            }

            if (Array.IndexOf(targets, targets[i]) != i)
            {
                error = $"Field '{targets[i]}' is mapped more than once.";
                return false;
            }

            maxValueLengths[i] = field.MaxValueLength;
        }

        int keyIndex = Array.IndexOf(targets, job.KeyField);
        if (keyIndex < 0)
        {
            error = $"Key field '{job.KeyField}' is not mapped.";
            return false;
        }

        error = null;
        context = new ChunkValidationContext(job.KeyField, targets, maxValueLengths, keyIndex, Array.IndexOf(targets, KeyNormalizer.EmailFieldName));

        return true;
    }
}

/// <param name="IsValid">Whether every record passed.</param>
/// <param name="Rows">Number of records read (all of them when valid).</param>
/// <param name="FirstRow">Row number of the first record.</param>
/// <param name="LastRow">Row number of the last record.</param>
/// <param name="Reason">Why validation failed; <c>null</c> when valid.</param>
/// <param name="Message">Description of the failure without contact data; <c>null</c> when valid.</param>
/// <param name="RowNumber">Row number (<c>r</c>) of the offending record, when it could be read.</param>
internal sealed record ChunkValidationResult(bool IsValid, int Rows, long FirstRow, long LastRow, ChunkRejectReason? Reason = null, string? Message = null, long? RowNumber = null);

/// <summary>
/// Validates the decompressed content of a chunk: NDJSON, one record per line, per the wire contract
/// (<c>{"r":12,"k":"key","v":{"Field":"value"},"f":["invalidEmail"]}</c>). The server never trusts the browser:
/// any invalid record rejects the whole chunk.
/// </summary>
internal static class ChunkValidator
{
    /// <summary>Longest accepted record (one NDJSON line), in bytes.</summary>
    public const int MaxRecordBytes = 1024 * 1024;

    public const string InvalidEmailFlag = "invalidEmail";

    private const int InitialBufferSize = 64 * 1024;

    /// <summary>
    /// Reads and validates all records from <paramref name="content"/> (decompressed NDJSON).
    /// </summary>
    /// <param name="content">Decompressed chunk content. Read to the end unless validation fails.</param>
    /// <param name="context">Job mapping and field limits.</param>
    /// <param name="maxBytes">Maximum decompressed size; guards against decompression bombs.</param>
    /// <param name="expectedRows">Row count declared by the browser.</param>
    /// <exception cref="InvalidDataException">The underlying gzip data is corrupt.</exception>
    public static ChunkValidationResult Validate(Stream content, ChunkValidationContext context, long maxBytes, int expectedRows)
    {
        var state = new ValidationState(context);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
        try
        {
            int start = 0;
            int end = 0;
            long totalBytes = 0;
            while (true)
            {
                int newLine = buffer.AsSpan(start, end - start).IndexOf((byte)'\n');
                if (newLine >= 0)
                {
                    if (!state.AddLine(buffer.AsSpan(start, newLine), expectedRows))
                    {
                        return state.Failure!;
                    }

                    start += newLine + 1;
                    continue;
                }

                if (end - start > MaxRecordBytes)
                {
                    return state.Fail(ChunkRejectReason.InvalidRecord, $"Record {state.Rows + 1} of the chunk is longer than {MaxRecordBytes} bytes.");
                }

                buffer = MakeRoom(buffer, ref start, ref end);
                int read = content.Read(buffer, end, buffer.Length - end);
                if (read == 0)
                {
                    break;
                }

                end += read;
                totalBytes += read;
                if (totalBytes > maxBytes)
                {
                    return state.Fail(ChunkRejectReason.DecompressedTooLarge, $"The chunk decompresses to more than {maxBytes} bytes.");
                }
            }

            // Last line without a trailing line feed.
            if (end > start && !state.AddLine(buffer.AsSpan(start, end - start), expectedRows))
            {
                return state.Failure!;
            }

            return state.Rows == expectedRows
                ? new ChunkValidationResult(true, state.Rows, state.FirstRow, state.LastRow)
                : state.Fail(ChunkRejectReason.RowCountMismatch, $"The chunk has {state.Rows} records but declares {expectedRows}.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Moves unread bytes to the start of the buffer and grows it when it is full.
    /// </summary>
    private static byte[] MakeRoom(byte[] buffer, ref int start, ref int end)
    {
        int length = end - start;
        byte[] target = buffer;
        if (length == buffer.Length)
        {
            target = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
        }

        if (start > 0 || !ReferenceEquals(target, buffer))
        {
            buffer.AsSpan(start, length).CopyTo(target);
        }

        if (!ReferenceEquals(target, buffer))
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        start = 0;
        end = length;

        return target;
    }

    private sealed class ValidationState(ChunkValidationContext context)
    {
        private readonly bool[] seenTargets = new bool[context.Targets.Length];

        public int Rows { get; private set; }

        public long FirstRow { get; private set; }

        public long LastRow { get; private set; }

        public ChunkValidationResult? Failure { get; private set; }

        public ChunkValidationResult Fail(ChunkRejectReason reason, string message, long? rowNumber = null) =>
            Failure = new ChunkValidationResult(false, Rows, FirstRow, LastRow, reason, message, rowNumber);

        public bool AddLine(ReadOnlySpan<byte> line, int expectedRows)
        {
            int position = Rows + 1;
            if (position > expectedRows)
            {
                Fail(ChunkRejectReason.RowCountMismatch, $"The chunk has more records than the declared {expectedRows}.");
                return false;
            }

            long rowNumber = 0;
            string? error;
            try
            {
                error = ValidateRecord(line, ref rowNumber);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                error = "is not a valid JSON record";
            }

            long? knownRow = rowNumber > 0 ? rowNumber : null;
            if (error is not null)
            {
                Fail(ChunkRejectReason.InvalidRecord, $"Record {position} of the chunk{FormatRow(knownRow)} {error}.", knownRow);
                return false;
            }

            if (Rows > 0 && rowNumber <= LastRow)
            {
                Fail(ChunkRejectReason.RowOrder, $"Record {position} of the chunk{FormatRow(knownRow)} does not have a higher row number than the record before it.", knownRow);
                return false;
            }

            if (Rows == 0)
            {
                FirstRow = rowNumber;
            }

            LastRow = rowNumber;
            Rows = position;

            return true;
        }

        private static string FormatRow(long? rowNumber) => rowNumber is null ? "" : $" (row {rowNumber})";

        /// <summary>
        /// Returns <c>null</c> for a valid record, otherwise the end of the sentence "Record n of the chunk ...".
        /// Messages never contain values from the record.
        /// </summary>
        private string? ValidateRecord(ReadOnlySpan<byte> line, ref long rowNumber)
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return "is not a JSON object";
            }

            string? key = null;
            string? keyValue = null;
            bool hasRow = false;
            bool hasValues = false;
            bool hasFlags = false;
            bool flaggedInvalidEmail = false;

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("r"u8))
                {
                    if (hasRow)
                    {
                        return "has a duplicate 'r' property";
                    }

                    hasRow = true;
                    reader.Read();
                    if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt64(out long row) || row < 1)
                    {
                        return "has an invalid row number 'r'";
                    }

                    rowNumber = row;
                }
                else if (reader.ValueTextEquals("k"u8))
                {
                    if (key is not null)
                    {
                        return "has a duplicate 'k' property";
                    }

                    reader.Read();
                    if (reader.TokenType != JsonTokenType.String)
                    {
                        return "has a key 'k' that is not a string";
                    }

                    key = reader.GetString()!;
                }
                else if (reader.ValueTextEquals("v"u8))
                {
                    if (hasValues)
                    {
                        return "has a duplicate 'v' property";
                    }

                    hasValues = true;
                    string? valuesError = ReadValues(ref reader, out keyValue);
                    if (valuesError is not null)
                    {
                        return valuesError;
                    }
                }
                else if (reader.ValueTextEquals("f"u8))
                {
                    if (hasFlags)
                    {
                        return "has a duplicate 'f' property";
                    }

                    hasFlags = true;
                    string? flagsError = ReadFlags(ref reader, out flaggedInvalidEmail);
                    if (flagsError is not null)
                    {
                        return flagsError;
                    }
                }
                else
                {
                    return "has an unknown property";
                }
            }

            if (reader.TokenType != JsonTokenType.EndObject || reader.Read())
            {
                return "is not a single JSON object";
            }

            if (!hasRow)
            {
                return "has no row number 'r'";
            }

            if (key is null)
            {
                return "has no key 'k'";
            }

            if (!hasValues)
            {
                return "has no values 'v'";
            }

            return ValidateKey(key, keyValue) ?? ValidateInvalidEmailFlag(flaggedInvalidEmail);
        }

        private string? ReadValues(ref Utf8JsonReader reader, out string? keyValue)
        {
            keyValue = null;
            Array.Clear(seenTargets);

            reader.Read();
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return "has values 'v' that are not an object";
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                int targetIndex = FindTarget(ref reader);
                if (targetIndex < 0)
                {
                    return "has a value for a field that is not mapped";
                }

                string target = context.Targets[targetIndex];
                if (seenTargets[targetIndex])
                {
                    return $"has more than one value for field '{target}'";
                }

                seenTargets[targetIndex] = true;
                reader.Read();
                if (reader.TokenType != JsonTokenType.String)
                {
                    return $"has a value for field '{target}' that is not a string";
                }

                string value = reader.GetString()!;
                if (context.MaxValueLengths[targetIndex] is { } maxLength && value.Length > maxLength)
                {
                    return $"has a value for field '{target}' longer than {maxLength} characters";
                }

                if (value.Length > 0 && (KeyNormalizer.IsJsWhiteSpace(value[0]) || KeyNormalizer.IsJsWhiteSpace(value[^1])))
                {
                    return $"has a value for field '{target}' that is not trimmed";
                }

                if (targetIndex == context.KeyIndex)
                {
                    keyValue = value;
                }
            }

            return reader.TokenType == JsonTokenType.EndObject ? null : "has malformed values 'v'";
        }

        private int FindTarget(ref Utf8JsonReader reader)
        {
            for (int i = 0; i < context.Targets.Length; i++)
            {
                if (reader.ValueTextEquals(context.Targets[i]))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string? ReadFlags(ref Utf8JsonReader reader, out bool flaggedInvalidEmail)
        {
            flaggedInvalidEmail = false;
            reader.Read();
            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return "has flags 'f' that are not an array";
            }

            while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            {
                if (reader.TokenType != JsonTokenType.String || !reader.ValueTextEquals(InvalidEmailFlag))
                {
                    return "has an unknown flag in 'f'";
                }

                if (flaggedInvalidEmail)
                {
                    return "has a duplicate flag in 'f'";
                }

                flaggedInvalidEmail = true;
            }

            return reader.TokenType == JsonTokenType.EndArray ? null : "has malformed flags 'f'";
        }

        private string? ValidateKey(string key, string? keyValue)
        {
            if (key.Length is 0 or > KeyNormalizer.MaxKeyLength)
            {
                return $"has a key 'k' that is empty or longer than {KeyNormalizer.MaxKeyLength} characters";
            }

            if (keyValue is null)
            {
                return $"has no value for the key field '{context.KeyField}'";
            }

            // Also covers email validity when the key is the email field: an invalid email normalizes to null.
            return string.Equals(KeyNormalizer.NormalizeKey(context.KeyField, keyValue), key, StringComparison.Ordinal)
                ? null
                : $"has a key 'k' that is not the normalized value of the key field '{context.KeyField}'";
        }

        private string? ValidateInvalidEmailFlag(bool flaggedInvalidEmail) =>
            flaggedInvalidEmail && !context.InvalidEmailFlagAllowed
                ? $"has the '{InvalidEmailFlag}' flag, which is only allowed when the email field is mapped and is not the key"
                : null;
    }
}
