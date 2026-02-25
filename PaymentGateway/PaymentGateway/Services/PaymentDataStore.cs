using Npgsql;
using PaymentGateway.Models;

namespace PaymentGateway.Services;

public interface IPaymentDataStore
{
    Task<Guid> SaveDecryptedPayloadAsync(PaymentModel payment, string decryptedJson, string traceId, CancellationToken cancellationToken);
    Task<PaymentSessionRecord?> GetByIndexGuidAsync(Guid indexGuid, CancellationToken cancellationToken);
    Task<List<PaymentChannelOptionViewModel>> GetChannelOptionsAsync(decimal amount, CancellationToken cancellationToken);
    Task UpdatePaymentAttemptAsync(Guid indexGuid, string channelCode, string? paymentUrl, CancellationToken cancellationToken);
    Task UpdateStatusAsync(Guid indexGuid, string status, CancellationToken cancellationToken);
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
}
