using Npgsql;
using NpgsqlTypes;
using PaymentGateway.Models;

namespace PaymentGateway.Services;

public interface IPaymentDataStore
{
    Task<Guid> SaveDecryptedPayloadAsync(PaymentModel payment, string decryptedJson, string traceId, CancellationToken cancellationToken);
    Task<PaymentSessionRecord?> GetByIndexGuidAsync(Guid indexGuid, CancellationToken cancellationToken);
    Task<List<PaymentSessionRecord>> GetRecentPaymentSessionsAsync(int limit, CancellationToken cancellationToken);
    Task<PaymentReportQueryResult> SearchPaymentSessionsAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? status,
        string? referenceNo,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken);
    Task<List<PaymentChannelOptionViewModel>> GetChannelOptionsAsync(decimal amount, CancellationToken cancellationToken);
    Task UpdatePaymentAttemptAsync(Guid indexGuid, string channelCode, string? paymentUrl, CancellationToken cancellationToken);
    Task UpdateStatusAsync(Guid indexGuid, string status, CancellationToken cancellationToken);
    Task<UserAccountRecord?> GetUserByEmailAsync(string email, CancellationToken cancellationToken);
    Task<UserAccountRecord?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken);
    Task<UserAccountRecord?> CreateFirstUserIfNoUsersAsync(
        string email,
        string? fullName,
        byte[] passwordHash,
        byte[] passwordSalt,
        CancellationToken cancellationToken);
    Task<Guid> CreatePasswordResetTokenAsync(Guid userId, byte[] tokenHash, DateTime expiresAtUtc, CancellationToken cancellationToken);
    Task<bool> IsPasswordResetTokenValidAsync(Guid tokenId, byte[] tokenHash, CancellationToken cancellationToken);
    Task<bool> TryResetPasswordWithTokenAsync(Guid tokenId, byte[] tokenHash, byte[] newPasswordHash, byte[] newPasswordSalt, CancellationToken cancellationToken);
    Task UpdateUserPasswordAsync(Guid userId, byte[] newPasswordHash, byte[] newPasswordSalt, CancellationToken cancellationToken);
}

public sealed class PaymentDataStore : IPaymentDataStore
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PaymentDataStore> _logger;
    private readonly SemaphoreSlim _databaseLock = new(1, 1);
    private readonly SemaphoreSlim _schemaLock = new(1, 1);
    private volatile bool _databaseReady;
    private volatile bool _schemaReady;

    public PaymentDataStore(IConfiguration configuration, ILogger<PaymentDataStore> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task<Guid> SaveDecryptedPayloadAsync(PaymentModel payment, string decryptedJson, string traceId, CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "DB insert start for callback payload. TraceId: {TraceId}, InvoiceReference: {InvoiceReference}, InvoiceUuid: {InvoiceUuid}, Amount: {Amount}",
            traceId, payment.Invoice?.Reference, payment.Invoice?.Uuid, payment.Invoice?.PriceWithDiscountWithTaxes);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);
        Guid indexGuid = Guid.NewGuid();

        string insertSql = $"""
            INSERT INTO {GetQualifiedTableName("payment_checkout_session")}
            (
                index_guid,
                trace_id,
                invoice_uuid,
                invoice_reference,
                billed_entity_name,
                amount,
                space_uuid,
                space_name,
                decrypted_json
            )
            VALUES
            (
                @index_guid,
                @trace_id,
                @invoice_uuid,
                @invoice_reference,
                @billed_entity_name,
                @amount,
                @space_uuid,
                @space_name,
                @decrypted_json
            );
            """;

        await using var command = new NpgsqlCommand(insertSql, connection);
        command.Parameters.AddWithValue("@index_guid", indexGuid);
        command.Parameters.AddWithValue("@trace_id", (object?)traceId ?? DBNull.Value);
        command.Parameters.AddWithValue("@invoice_uuid", (object?)payment.Invoice?.Uuid ?? DBNull.Value);
        command.Parameters.AddWithValue("@invoice_reference", (object?)payment.Invoice?.Reference ?? DBNull.Value);
        command.Parameters.AddWithValue("@billed_entity_name", (object?)payment.Invoice?.BilledEntityName ?? DBNull.Value);
        command.Parameters.AddWithValue("@amount", payment.Invoice?.PriceWithDiscountWithTaxes ?? 0m);
        command.Parameters.AddWithValue("@space_uuid", (object?)payment.Space?.Uuid ?? DBNull.Value);
        command.Parameters.AddWithValue("@space_name", (object?)payment.Space?.Name ?? DBNull.Value);
        command.Parameters.AddWithValue("@decrypted_json", decryptedJson);

        int rows = await command.ExecuteNonQueryAsync(cancellationToken);
        _logger.LogDebug("DB insert rows affected: {Rows}. IndexGuid: {IndexGuid}", rows, indexGuid);
        _logger.LogInformation("Decrypted callback payload inserted into database for TraceId: {TraceId}, IndexGuid: {IndexGuid}", traceId, indexGuid);
        return indexGuid;
    }

    public async Task<PaymentSessionRecord?> GetByIndexGuidAsync(Guid indexGuid, CancellationToken cancellationToken)
    {
        _logger.LogDebug("DB get session by IndexGuid started. IndexGuid: {IndexGuid}", indexGuid);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string querySql = $"""
            SELECT
                index_guid,
                trace_id,
                invoice_uuid,
                invoice_reference,
                billed_entity_name,
                amount,
                space_uuid,
                space_name,
                decrypted_json,
                status,
                selected_channel_code,
                payment_url,
                created_at_utc,
                updated_at_utc
            FROM {GetQualifiedTableName("payment_checkout_session")}
            WHERE index_guid = @index_guid
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(querySql, connection);
        command.Parameters.AddWithValue("@index_guid", indexGuid);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            _logger.LogWarning("DB get session returned no rows. IndexGuid: {IndexGuid}", indexGuid);
            return null;
        }

        var result = new PaymentSessionRecord
        {
            IndexGuid = reader.GetGuid(reader.GetOrdinal("index_guid")),
            TraceId = reader["trace_id"] as string,
            InvoiceUuid = reader["invoice_uuid"] as string,
            InvoiceReference = reader["invoice_reference"] as string,
            BilledEntityName = reader["billed_entity_name"] as string,
            Amount = reader.GetDecimal(reader.GetOrdinal("amount")),
            SpaceUuid = reader["space_uuid"] as string,
            SpaceName = reader["space_name"] as string,
            DecryptedJson = reader["decrypted_json"] as string ?? string.Empty,
            Status = reader["status"] as string ?? "Pending",
            SelectedChannelCode = reader["selected_channel_code"] as string,
            PaymentUrl = reader["payment_url"] as string,
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc"))
        };
        _logger.LogDebug(
            "DB get session success. IndexGuid: {IndexGuid}, Status: {Status}, Amount: {Amount}, SelectedChannelCode: {SelectedChannelCode}",
            result.IndexGuid, result.Status, result.Amount, result.SelectedChannelCode);
        return result;
    }

    public async Task<List<PaymentChannelOptionViewModel>> GetChannelOptionsAsync(decimal amount, CancellationToken cancellationToken)
    {
        _logger.LogDebug("DB get channel options started. Amount: {Amount}", amount);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string querySql = $"""
            SELECT
                id,
                channel_code,
                display_name,
                country,
                currency,
                settlement_time,
                type,
                is_refundable,
                supports_save,
                supports_reusable_payment_code,
                supports_merchant_initiated_txn
            FROM {GetQualifiedTableName("payment_channels")}
            WHERE @amount >= min_amount
              AND (max_amount IS NULL OR @amount <= max_amount)
            ORDER BY type, display_name;
            """;

        await using var command = new NpgsqlCommand(querySql, connection);
        command.Parameters.AddWithValue("@amount", amount);

        var options = new List<PaymentChannelOptionViewModel>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            string settlement = reader["settlement_time"] as string ?? "N/A";
            string type = reader["type"] as string ?? "Unknown";
            bool isRefundable = reader.GetBoolean(reader.GetOrdinal("is_refundable"));
            bool supportsSave = reader.GetBoolean(reader.GetOrdinal("supports_save"));
            bool supportsReusable = reader.GetBoolean(reader.GetOrdinal("supports_reusable_payment_code"));
            bool supportsMit = reader.GetBoolean(reader.GetOrdinal("supports_merchant_initiated_txn"));

            options.Add(new PaymentChannelOptionViewModel
            {
                Id = reader.GetInt64(reader.GetOrdinal("id")),
                Type = type,
                Code = reader["channel_code"] as string ?? string.Empty,
                Name = reader["display_name"] as string ?? string.Empty,
                Country = reader["country"] as string ?? string.Empty,
                Currency = reader["currency"] as string ?? string.Empty,
                Description = $"Settlement: {settlement} | Refundable: {(isRefundable ? "Yes" : "No")} | Save: {(supportsSave ? "Yes" : "No")} | Reusable: {(supportsReusable ? "Yes" : "No")} | MIT: {(supportsMit ? "Yes" : "No")}"
            });
        }

        _logger.LogInformation("DB get channel options completed. Amount: {Amount}, ChannelCount: {ChannelCount}", amount, options.Count);
        return options;
    }

    public async Task<List<PaymentSessionRecord>> GetRecentPaymentSessionsAsync(int limit, CancellationToken cancellationToken)
    {
        _logger.LogDebug("DB get recent payment sessions started. Limit: {Limit}", limit);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string querySql = $"""
            SELECT
                index_guid,
                trace_id,
                invoice_uuid,
                invoice_reference,
                billed_entity_name,
                amount,
                space_uuid,
                space_name,
                decrypted_json,
                status,
                selected_channel_code,
                payment_url,
                created_at_utc,
                updated_at_utc
            FROM {GetQualifiedTableName("payment_checkout_session")}
            ORDER BY created_at_utc DESC
            LIMIT @limit;
            """;

        await using var command = new NpgsqlCommand(querySql, connection);
        command.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 500));

        var sessions = new List<PaymentSessionRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sessions.Add(new PaymentSessionRecord
            {
                IndexGuid = reader.GetGuid(reader.GetOrdinal("index_guid")),
                TraceId = reader["trace_id"] as string,
                InvoiceUuid = reader["invoice_uuid"] as string,
                InvoiceReference = reader["invoice_reference"] as string,
                BilledEntityName = reader["billed_entity_name"] as string,
                Amount = reader.GetDecimal(reader.GetOrdinal("amount")),
                SpaceUuid = reader["space_uuid"] as string,
                SpaceName = reader["space_name"] as string,
                DecryptedJson = reader["decrypted_json"] as string ?? string.Empty,
                Status = reader["status"] as string ?? "Pending",
                SelectedChannelCode = reader["selected_channel_code"] as string,
                PaymentUrl = reader["payment_url"] as string,
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
                UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc"))
            });
        }

        _logger.LogInformation("DB get recent payment sessions completed. Count: {Count}", sessions.Count);
        return sessions;
    }

    public async Task<PaymentReportQueryResult> SearchPaymentSessionsAsync(
        DateTime? fromUtc,
        DateTime? toUtc,
        string? status,
        string? referenceNo,
        int pageNumber,
        int pageSize,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);
        string? normalizedStatus = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        string? normalizedReference = string.IsNullOrWhiteSpace(referenceNo) ? null : referenceNo.Trim();
        int safePageNumber = Math.Max(1, pageNumber);
        bool fetchAll = pageSize <= 0;
        int safePageSize = fetchAll ? 0 : Math.Clamp(pageSize, 1, 500);
        int offset = 0;
        if (!fetchAll)
        {
            long computedOffset = (long)(safePageNumber - 1) * safePageSize;
            offset = computedOffset > int.MaxValue ? int.MaxValue : (int)computedOffset;
        }

        string whereClause = """
            WHERE (@from_utc IS NULL OR created_at_utc >= @from_utc)
              AND (@to_utc IS NULL OR created_at_utc <= @to_utc)
              AND (@status IS NULL OR LOWER(status) = LOWER(@status))
              AND (@reference_no IS NULL OR invoice_reference ILIKE '%' || @reference_no || '%')
            """;

        string paginationSql = fetchAll
            ? string.Empty
            : "LIMIT @limit OFFSET @offset";

        string listSql = $"""
            SELECT
                index_guid,
                trace_id,
                invoice_uuid,
                invoice_reference,
                billed_entity_name,
                amount,
                space_uuid,
                space_name,
                decrypted_json,
                status,
                selected_channel_code,
                payment_url,
                created_at_utc,
                updated_at_utc
            FROM {GetQualifiedTableName("payment_checkout_session")}
            {whereClause}
            ORDER BY created_at_utc DESC
            {paginationSql};
            """;

        var sessions = new List<PaymentSessionRecord>();
        await using (var listCommand = new NpgsqlCommand(listSql, connection))
        {
            AddReportFilterParameters(listCommand, fromUtc, toUtc, normalizedStatus, normalizedReference);
            if (!fetchAll)
            {
                listCommand.Parameters.AddWithValue("@limit", safePageSize);
                listCommand.Parameters.AddWithValue("@offset", offset);
            }

            await using var reader = await listCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                sessions.Add(MapPaymentSession(reader));
            }
        }

        string countSql = $"""
            SELECT COUNT(1)
            FROM {GetQualifiedTableName("payment_checkout_session")}
            {whereClause};
            """;

        int totalCount;
        await using (var countCommand = new NpgsqlCommand(countSql, connection))
        {
            AddReportFilterParameters(countCommand, fromUtc, toUtc, normalizedStatus, normalizedReference);
            object? countResult = await countCommand.ExecuteScalarAsync(cancellationToken);
            totalCount = Convert.ToInt32(countResult ?? 0);
        }

        return new PaymentReportQueryResult
        {
            Sessions = sessions,
            TotalCount = totalCount
        };
    }

    public async Task UpdatePaymentAttemptAsync(Guid indexGuid, string channelCode, string? paymentUrl, CancellationToken cancellationToken)
    {
        _logger.LogDebug("DB update payment attempt started. IndexGuid: {IndexGuid}, ChannelCode: {ChannelCode}", indexGuid, channelCode);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string updateSql = $"""
            UPDATE {GetQualifiedTableName("payment_checkout_session")}
            SET
                selected_channel_code = @selected_channel_code,
                payment_url = @payment_url,
                updated_at_utc = NOW()
            WHERE index_guid = @index_guid;
            """;

        await using var command = new NpgsqlCommand(updateSql, connection);
        command.Parameters.AddWithValue("@selected_channel_code", channelCode);
        command.Parameters.AddWithValue("@payment_url", (object?)paymentUrl ?? DBNull.Value);
        command.Parameters.AddWithValue("@index_guid", indexGuid);
        int rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            _logger.LogWarning("DB update payment attempt affected 0 rows. IndexGuid: {IndexGuid}", indexGuid);
        }
        else
        {
            _logger.LogInformation("DB update payment attempt succeeded. IndexGuid: {IndexGuid}, Rows: {Rows}", indexGuid, rows);
        }
    }

    public async Task UpdateStatusAsync(Guid indexGuid, string status, CancellationToken cancellationToken)
    {
        _logger.LogDebug("DB update status started. IndexGuid: {IndexGuid}, Status: {Status}", indexGuid, status);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string updateSql = $"""
            UPDATE {GetQualifiedTableName("payment_checkout_session")}
            SET
                status = @status,
                updated_at_utc = NOW()
            WHERE index_guid = @index_guid;
            """;

        await using var command = new NpgsqlCommand(updateSql, connection);
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@index_guid", indexGuid);
        int rows = await command.ExecuteNonQueryAsync(cancellationToken);
        if (rows == 0)
        {
            _logger.LogWarning("DB update status affected 0 rows. IndexGuid: {IndexGuid}, Status: {Status}", indexGuid, status);
        }
        else
        {
            _logger.LogInformation("DB update status succeeded. IndexGuid: {IndexGuid}, Status: {Status}, Rows: {Rows}", indexGuid, status, rows);
        }
    }

    public async Task<UserAccountRecord?> GetUserByEmailAsync(string email, CancellationToken cancellationToken)
    {
        string normalizedEmail = NormalizeEmail(email);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string querySql = $"""
            SELECT
                user_id,
                email,
                full_name,
                password_hash,
                password_salt,
                created_at_utc,
                updated_at_utc
            FROM {GetQualifiedTableName("app_users")}
            WHERE email = @email
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(querySql, connection);
        command.Parameters.AddWithValue("@email", normalizedEmail);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserAccountRecord
        {
            UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
            Email = reader["email"] as string ?? string.Empty,
            FullName = reader["full_name"] as string,
            PasswordHash = reader["password_hash"] as byte[] ?? [],
            PasswordSalt = reader["password_salt"] as byte[] ?? [],
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc"))
        };
    }

    public async Task<UserAccountRecord?> GetUserByIdAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string querySql = $"""
            SELECT
                user_id,
                email,
                full_name,
                password_hash,
                password_salt,
                created_at_utc,
                updated_at_utc
            FROM {GetQualifiedTableName("app_users")}
            WHERE user_id = @user_id
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(querySql, connection);
        command.Parameters.AddWithValue("@user_id", userId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserAccountRecord
        {
            UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
            Email = reader["email"] as string ?? string.Empty,
            FullName = reader["full_name"] as string,
            PasswordHash = reader["password_hash"] as byte[] ?? [],
            PasswordSalt = reader["password_salt"] as byte[] ?? [],
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc"))
        };
    }

    public async Task<UserAccountRecord?> CreateFirstUserIfNoUsersAsync(
        string email,
        string? fullName,
        byte[] passwordHash,
        byte[] passwordSalt,
        CancellationToken cancellationToken)
    {
        string normalizedEmail = NormalizeEmail(email);
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        const long firstUserLockKey = 25022501;
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@lock_key);", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("@lock_key", firstUserLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        string countSql = $"""
            SELECT COUNT(1)
            FROM {GetQualifiedTableName("app_users")};
            """;
        await using (var countCommand = new NpgsqlCommand(countSql, connection, transaction))
        {
            object? countResult = await countCommand.ExecuteScalarAsync(cancellationToken);
            long totalUsers = Convert.ToInt64(countResult ?? 0);
            if (totalUsers > 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }
        }

        Guid userId = Guid.NewGuid();
        string insertSql = $"""
            INSERT INTO {GetQualifiedTableName("app_users")}
            (
                user_id,
                email,
                full_name,
                password_hash,
                password_salt
            )
            VALUES
            (
                @user_id,
                @email,
                @full_name,
                @password_hash,
                @password_salt
            )
            RETURNING
                user_id,
                email,
                full_name,
                password_hash,
                password_salt,
                created_at_utc,
                updated_at_utc;
            """;

        await using var insertCommand = new NpgsqlCommand(insertSql, connection, transaction);
        insertCommand.Parameters.AddWithValue("@user_id", userId);
        insertCommand.Parameters.AddWithValue("@email", normalizedEmail);
        insertCommand.Parameters.AddWithValue("@full_name", (object?)fullName ?? DBNull.Value);
        insertCommand.Parameters.AddWithValue("@password_hash", passwordHash);
        insertCommand.Parameters.AddWithValue("@password_salt", passwordSalt);

        NpgsqlDataReader reader;
        try
        {
            reader = await insertCommand.ExecuteReaderAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }

        await using (reader)
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var createdUser = new UserAccountRecord
            {
                UserId = reader.GetGuid(reader.GetOrdinal("user_id")),
                Email = reader["email"] as string ?? normalizedEmail,
                FullName = reader["full_name"] as string,
                PasswordHash = reader["password_hash"] as byte[] ?? [],
                PasswordSalt = reader["password_salt"] as byte[] ?? [],
                CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
                UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc"))
            };

            await reader.CloseAsync();
            await transaction.CommitAsync(cancellationToken);
            _logger.LogInformation("First application user created successfully. Email: {Email}", createdUser.Email);
            return createdUser;
        }
    }

    public async Task<Guid> CreatePasswordResetTokenAsync(Guid userId, byte[] tokenHash, DateTime expiresAtUtc, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);
        Guid tokenId = Guid.NewGuid();

        string insertSql = $"""
            INSERT INTO {GetQualifiedTableName("password_reset_tokens")}
            (
                token_id,
                user_id,
                token_hash,
                expires_at_utc
            )
            VALUES
            (
                @token_id,
                @user_id,
                @token_hash,
                @expires_at_utc
            );
            """;

        await using var command = new NpgsqlCommand(insertSql, connection);
        command.Parameters.AddWithValue("@token_id", tokenId);
        command.Parameters.AddWithValue("@user_id", userId);
        command.Parameters.AddWithValue("@token_hash", tokenHash);
        command.Parameters.AddWithValue("@expires_at_utc", expiresAtUtc);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return tokenId;
    }

    public async Task<bool> IsPasswordResetTokenValidAsync(Guid tokenId, byte[] tokenHash, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);
        string querySql = $"""
            SELECT 1
            FROM {GetQualifiedTableName("password_reset_tokens")}
            WHERE token_id = @token_id
              AND token_hash = @token_hash
              AND used_at_utc IS NULL
              AND expires_at_utc >= NOW()
            LIMIT 1;
            """;

        await using var command = new NpgsqlCommand(querySql, connection);
        command.Parameters.AddWithValue("@token_id", tokenId);
        command.Parameters.AddWithValue("@token_hash", tokenHash);
        object? exists = await command.ExecuteScalarAsync(cancellationToken);
        return exists is not null;
    }

    public async Task<bool> TryResetPasswordWithTokenAsync(Guid tokenId, byte[] tokenHash, byte[] newPasswordHash, byte[] newPasswordSalt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        string consumeTokenSql = $"""
            UPDATE {GetQualifiedTableName("password_reset_tokens")}
            SET used_at_utc = NOW()
            WHERE token_id = @token_id
              AND token_hash = @token_hash
              AND used_at_utc IS NULL
              AND expires_at_utc >= NOW()
            RETURNING user_id;
            """;

        Guid userId;
        await using (var consumeCommand = new NpgsqlCommand(consumeTokenSql, connection, transaction))
        {
            consumeCommand.Parameters.AddWithValue("@token_id", tokenId);
            consumeCommand.Parameters.AddWithValue("@token_hash", tokenHash);
            object? userIdResult = await consumeCommand.ExecuteScalarAsync(cancellationToken);
            if (userIdResult is null || userIdResult == DBNull.Value)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            userId = (Guid)userIdResult;
        }

        string updateUserSql = $"""
            UPDATE {GetQualifiedTableName("app_users")}
            SET
                password_hash = @password_hash,
                password_salt = @password_salt,
                updated_at_utc = NOW()
            WHERE user_id = @user_id;
            """;

        await using (var updateUserCommand = new NpgsqlCommand(updateUserSql, connection, transaction))
        {
            updateUserCommand.Parameters.AddWithValue("@password_hash", newPasswordHash);
            updateUserCommand.Parameters.AddWithValue("@password_salt", newPasswordSalt);
            updateUserCommand.Parameters.AddWithValue("@user_id", userId);
            int updated = await updateUserCommand.ExecuteNonQueryAsync(cancellationToken);
            if (updated == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }
        }

        string invalidateOthersSql = $"""
            UPDATE {GetQualifiedTableName("password_reset_tokens")}
            SET used_at_utc = NOW()
            WHERE user_id = @user_id
              AND used_at_utc IS NULL
              AND token_id <> @token_id;
            """;

        await using (var invalidateCommand = new NpgsqlCommand(invalidateOthersSql, connection, transaction))
        {
            invalidateCommand.Parameters.AddWithValue("@user_id", userId);
            invalidateCommand.Parameters.AddWithValue("@token_id", tokenId);
            await invalidateCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task UpdateUserPasswordAsync(Guid userId, byte[] newPasswordHash, byte[] newPasswordSalt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenReadyConnectionAsync(cancellationToken);

        string updateSql = $"""
            UPDATE {GetQualifiedTableName("app_users")}
            SET
                password_hash = @password_hash,
                password_salt = @password_salt,
                updated_at_utc = NOW()
            WHERE user_id = @user_id;
            """;

        await using var command = new NpgsqlCommand(updateSql, connection);
        command.Parameters.AddWithValue("@password_hash", newPasswordHash);
        command.Parameters.AddWithValue("@password_salt", newPasswordSalt);
        command.Parameters.AddWithValue("@user_id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenReadyConnectionAsync(CancellationToken cancellationToken)
    {
        string connectionString = _configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        _logger.LogDebug("Opening ready DB connection for schema {Schema}.", GetSchemaName());

        await EnsureDatabaseExistsAsync(connectionString, cancellationToken);

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        _logger.LogDebug("Ready DB connection opened successfully.");
        return connection;
    }

    private async Task EnsureDatabaseExistsAsync(string connectionString, CancellationToken cancellationToken)
    {
        if (_databaseReady)
        {
            return;
        }

        await _databaseLock.WaitAsync(cancellationToken);
        try
        {
            if (_databaseReady)
            {
                return;
            }

            var targetBuilder = new NpgsqlConnectionStringBuilder(connectionString);
            if (string.IsNullOrWhiteSpace(targetBuilder.Database))
            {
                throw new InvalidOperationException("Database name is required in connection string.");
            }

            string targetDatabase = targetBuilder.Database;
            _logger.LogInformation("Ensuring PostgreSQL database exists. Database: {DatabaseName}", targetDatabase);
            var adminBuilder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Database = "postgres",
                Pooling = false
            };

            await using var adminConnection = new NpgsqlConnection(adminBuilder.ConnectionString);
            await adminConnection.OpenAsync(cancellationToken);

            const string existsSql = "SELECT 1 FROM pg_database WHERE datname = @database_name;";
            await using var existsCommand = new NpgsqlCommand(existsSql, adminConnection);
            existsCommand.Parameters.AddWithValue("@database_name", targetDatabase);
            object? exists = await existsCommand.ExecuteScalarAsync(cancellationToken);
            if (exists is null)
            {
                string createSql = $"CREATE DATABASE {QuoteIdentifier(targetDatabase)};";
                await using var createCommand = new NpgsqlCommand(createSql, adminConnection);
                await createCommand.ExecuteNonQueryAsync(cancellationToken);
                _logger.LogInformation("Created PostgreSQL database: {DatabaseName}", targetDatabase);
            }
            else
            {
                _logger.LogDebug("PostgreSQL database already exists: {DatabaseName}", targetDatabase);
            }

            _databaseReady = true;
        }
        finally
        {
            _databaseLock.Release();
        }
    }

    private async Task EnsureSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (_schemaReady)
        {
            return;
        }

        await _schemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaReady)
            {
                return;
            }

            string schema = GetSchemaName();
            _logger.LogInformation("Ensuring PostgreSQL schema and tables exist. Schema: {Schema}", schema);
            string createSql = $"""
                CREATE SCHEMA IF NOT EXISTS {QuoteIdentifier(schema)};

                CREATE TABLE IF NOT EXISTS {GetQualifiedTableName("payment_checkout_session")}
                (
                    index_guid UUID PRIMARY KEY,
                    trace_id VARCHAR(128) NULL,
                    invoice_uuid VARCHAR(100) NULL,
                    invoice_reference VARCHAR(100) NULL,
                    billed_entity_name VARCHAR(255) NULL,
                    amount NUMERIC(18,2) NOT NULL,
                    space_uuid VARCHAR(100) NULL,
                    space_name VARCHAR(255) NULL,
                    decrypted_json TEXT NOT NULL,
                    status VARCHAR(20) NOT NULL DEFAULT 'Pending',
                    selected_channel_code VARCHAR(100) NULL,
                    payment_url TEXT NULL,
                    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS {GetQualifiedTableName("payment_channels")}
                (
                    id BIGSERIAL PRIMARY KEY,
                    channel_code VARCHAR(100) NOT NULL,
                    display_name VARCHAR(200) NOT NULL,
                    country CHAR(2) NOT NULL,
                    currency CHAR(3) NOT NULL,
                    min_amount NUMERIC(18,2) NOT NULL,
                    max_amount NUMERIC(18,2) NULL,
                    settlement_time VARCHAR(100) NOT NULL,
                    is_refundable BOOLEAN DEFAULT FALSE,
                    supports_save BOOLEAN DEFAULT FALSE,
                    supports_reusable_payment_code BOOLEAN DEFAULT FALSE,
                    supports_merchant_initiated_txn BOOLEAN DEFAULT FALSE,
                    type VARCHAR(100) NOT NULL,
                    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP,
                    updated_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ux_payment_channels_code_country_currency
                ON {GetQualifiedTableName("payment_channels")} (channel_code, country, currency);

                CREATE TABLE IF NOT EXISTS {GetQualifiedTableName("app_users")}
                (
                    user_id UUID PRIMARY KEY,
                    email VARCHAR(255) NOT NULL UNIQUE,
                    full_name VARCHAR(255) NULL,
                    password_hash BYTEA NOT NULL,
                    password_salt BYTEA NOT NULL,
                    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    updated_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE TABLE IF NOT EXISTS {GetQualifiedTableName("password_reset_tokens")}
                (
                    token_id UUID PRIMARY KEY,
                    user_id UUID NOT NULL REFERENCES {GetQualifiedTableName("app_users")} (user_id) ON DELETE CASCADE,
                    token_hash BYTEA NOT NULL,
                    expires_at_utc TIMESTAMPTZ NOT NULL,
                    used_at_utc TIMESTAMPTZ NULL,
                    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW()
                );

                CREATE INDEX IF NOT EXISTS ix_password_reset_tokens_user_id
                ON {GetQualifiedTableName("password_reset_tokens")} (user_id);
                """;

            await using var command = new NpgsqlCommand(createSql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);

            string seedSql = $"""
                INSERT INTO {GetQualifiedTableName("payment_channels")}
                (
                    channel_code,
                    display_name,
                    country,
                    currency,
                    min_amount,
                    max_amount,
                    settlement_time,
                    is_refundable,
                    supports_save,
                    supports_reusable_payment_code,
                    supports_merchant_initiated_txn,
                    type
                )
                VALUES
                    ('KBANK_CARD_INSTALLMENT', 'KBank Card Installment', 'TH', 'THB', 100.00, NULL, 'T+2', FALSE, TRUE, FALSE, FALSE, 'CARD_INSTALLMENT'),
                    ('BAY_CARD', 'Krungsri Credit Card', 'TH', 'THB', 50.00, NULL, 'T+2', TRUE, TRUE, FALSE, FALSE, 'CARD'),
                    ('SCB_CARD', 'SCB Credit Card', 'TH', 'THB', 50.00, NULL, 'T+2', TRUE, TRUE, FALSE, FALSE, 'CARD'),
                    ('BBL_CARD', 'Bangkok Bank Card', 'TH', 'THB', 50.00, NULL, 'T+2', TRUE, TRUE, FALSE, FALSE, 'CARD'),
                    ('KTC_CARD', 'KTC Credit Card', 'TH', 'THB', 50.00, NULL, 'T+2', TRUE, TRUE, FALSE, FALSE, 'CARD'),
                    ('UOB_CARD', 'UOB Credit Card', 'TH', 'THB', 50.00, NULL, 'T+2', TRUE, TRUE, FALSE, FALSE, 'CARD'),
                    ('BAY_CARD_INSTALLMENT', 'Krungsri Card Installment', 'TH', 'THB', 500.00, NULL, 'T+2', FALSE, TRUE, FALSE, FALSE, 'CARD_INSTALLMENT'),
                    ('SCB_CARD_INSTALLMENT', 'SCB Card Installment', 'TH', 'THB', 500.00, NULL, 'T+2', FALSE, TRUE, FALSE, FALSE, 'CARD_INSTALLMENT'),
                    ('THAI_QR', 'Thai QR Payment', 'TH', 'THB', 1.00, NULL, 'T+0', FALSE, FALSE, TRUE, FALSE, 'QR'),
                    ('PROMPTPAY', 'PromptPay', 'TH', 'THB', 1.00, NULL, 'T+0', FALSE, FALSE, TRUE, FALSE, 'QR')
                ON CONFLICT (channel_code, country, currency)
                DO NOTHING;
                """;

            await using var seedCommand = new NpgsqlCommand(seedSql, connection);
            int seeded = await seedCommand.ExecuteNonQueryAsync(cancellationToken);
            _logger.LogInformation("Payment channels seed completed. RowsInserted: {RowsInserted}", seeded);

            _schemaReady = true;
        }
        finally
        {
            _schemaLock.Release();
        }
    }

    private string GetQualifiedTableName(string tableName)
    {
        return $"{QuoteIdentifier(GetSchemaName())}.{QuoteIdentifier(tableName)}";
    }

    private string GetSchemaName()
    {
        string schema = _configuration["Database:Schema"] ?? "public";
        if (string.IsNullOrWhiteSpace(schema))
        {
            schema = "public";
        }

        return schema.Trim();
    }

    private static string QuoteIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            throw new ArgumentException("Identifier cannot be empty.", nameof(identifier));
        }

        return "\"" + identifier.Replace("\"", "\"\"") + "\"";
    }

    private static string NormalizeEmail(string email)
    {
        return email.Trim().ToLowerInvariant();
    }

    private static PaymentSessionRecord MapPaymentSession(NpgsqlDataReader reader)
    {
        return new PaymentSessionRecord
        {
            IndexGuid = reader.GetGuid(reader.GetOrdinal("index_guid")),
            TraceId = reader["trace_id"] as string,
            InvoiceUuid = reader["invoice_uuid"] as string,
            InvoiceReference = reader["invoice_reference"] as string,
            BilledEntityName = reader["billed_entity_name"] as string,
            Amount = reader.GetDecimal(reader.GetOrdinal("amount")),
            SpaceUuid = reader["space_uuid"] as string,
            SpaceName = reader["space_name"] as string,
            DecryptedJson = reader["decrypted_json"] as string ?? string.Empty,
            Status = reader["status"] as string ?? "Pending",
            SelectedChannelCode = reader["selected_channel_code"] as string,
            PaymentUrl = reader["payment_url"] as string,
            CreatedAtUtc = reader.GetDateTime(reader.GetOrdinal("created_at_utc")),
            UpdatedAtUtc = reader.GetDateTime(reader.GetOrdinal("updated_at_utc"))
        };
    }

    private static void AddReportFilterParameters(
        NpgsqlCommand command,
        DateTime? fromUtc,
        DateTime? toUtc,
        string? status,
        string? referenceNo)
    {
        AddNullableTimestamp(command, "@from_utc", fromUtc);
        AddNullableTimestamp(command, "@to_utc", toUtc);
        AddNullableText(command, "@status", status);
        AddNullableText(command, "@reference_no", referenceNo);
    }

    private static void AddNullableTimestamp(NpgsqlCommand command, string parameterName, DateTime? value)
    {
        DateTime? utcValue = value.HasValue ? EnsureUtc(value.Value) : null;
        var parameter = new NpgsqlParameter(parameterName, NpgsqlDbType.TimestampTz)
        {
            Value = utcValue.HasValue ? utcValue.Value : DBNull.Value
        };
        command.Parameters.Add(parameter);
    }

    private static void AddNullableText(NpgsqlCommand command, string parameterName, string? value)
    {
        var parameter = new NpgsqlParameter(parameterName, NpgsqlDbType.Text)
        {
            Value = string.IsNullOrWhiteSpace(value) ? DBNull.Value : value.Trim()
        };
        command.Parameters.Add(parameter);
    }

    private static DateTime EnsureUtc(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return value;
        }

        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Local);
        }

        return value.ToUniversalTime();
    }
}
