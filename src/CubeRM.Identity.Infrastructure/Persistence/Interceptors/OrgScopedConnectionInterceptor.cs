using System.Data.Common;
using CubeRM.SharedKernel.Auth.Abstractions;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace CubeRM.Identity.Infrastructure.Persistence.Interceptors;

/// <summary>
/// L4 tenant isolation: sets the PostgreSQL session variable that row-level security
/// policies filter on.
/// </summary>
/// <remarks>
/// <para>
/// The application connects as <c>cube_app</c>, a role with neither <c>BYPASSRLS</c> nor
/// table ownership. Every tenant-owned table has <c>FORCE ROW LEVEL SECURITY</c> and a
/// policy of the form <c>org_id = current_setting('app.current_org_id')::uuid</c>.
/// </para>
/// <para>
/// <b>The failure mode this class exists to avoid:</b> <c>set_config(..., is_local =&gt; false)</c>
/// is session-scoped, and Npgsql pools connections. A connection returned to the pool
/// carrying the previous request's organisation will serve the next request under it —
/// silently, and with RLS correctly enforcing the <i>wrong</i> organisation. That is the
/// single highest-severity bug available in this design (risk register R7), which is why
/// <see cref="ConnectionClosingAsync"/> is not optional and why
/// <c>ConnectionPoolLeakageTests</c> runs on every build rather than nightly.
/// </para>
/// <para>
/// <b>Both</b> the closing and disposing hooks clear, because they are not the same event:
/// Npgsql returns a connection to the pool on <c>Close()</c>, not on <c>Dispose()</c>.
/// Hooking only disposal leaves the pooled path — the common one — uncleared.
/// </para>
/// </remarks>
public sealed class OrgScopedConnectionInterceptor(
    ICubeTenantContext tenant,
    ILogger<OrgScopedConnectionInterceptor> logger)
    : DbConnectionInterceptor
{
    private const string SessionVariable = "app.current_org_id";

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (!tenant.IsResolved)
        {
            // No organisation: leave the variable unset. RLS then returns zero rows, which
            // is the correct fail-closed outcome. Do NOT default to anything.
            await ClearAsync(connection, cancellationToken);
            return;
        }

        await SetAsync(connection, tenant.OrgId.ToString(), cancellationToken);
    }

    /// <summary>
    /// Fires just before EF calls <see cref="DbConnection.Close"/>. For Npgsql this is the
    /// moment the connection goes back to the pool, so this is the hook that actually
    /// prevents R7.
    /// </summary>
    public override async ValueTask<InterceptionResult> ConnectionClosingAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        await ClearOnReturnAsync(connection);
        return result;
    }

    /// <summary>
    /// Fires just before EF disposes the connection object. Covers the case where the
    /// connection is owned and disposed without an explicit close.
    /// </summary>
    /// <remarks>
    /// Overlapping with <see cref="ConnectionClosingAsync"/> is intentional and harmless:
    /// <c>ClearAsync</c> is idempotent and no-ops on an already-closed connection. Clearing
    /// twice costs a round trip that only happens on connection teardown; clearing never
    /// costs a cross-tenant read.
    /// </remarks>
    public override async ValueTask<InterceptionResult> ConnectionDisposingAsync(
        DbConnection connection,
        ConnectionEventData eventData,
        InterceptionResult result)
    {
        await ClearOnReturnAsync(connection);
        return result;
    }

    /// <summary>
    /// Clears the session variable, and destroys the connection rather than let it return
    /// to the pool still carrying an organisation.
    /// </summary>
    /// <remarks>
    /// Neither interception point is given a <see cref="CancellationToken"/> by EF, which is
    /// correct for this work: a cancelled clear would return a poisoned connection to the
    /// pool. This must run to completion or take the connection down with it.
    /// </remarks>
    private async Task ClearOnReturnAsync(DbConnection connection)
    {
        try
        {
            await ClearAsync(connection, CancellationToken.None);
        }
        catch (Exception ex)
        {
            // If we cannot clear it we must not let the connection be reused. Breaking it
            // costs one connection; leaving it risks a cross-tenant read.
            logger.LogCritical(ex,
                "Failed to clear {SessionVariable} on connection return. Discarding the connection.",
                SessionVariable);

            if (connection is NpgsqlConnection npgsql)
                NpgsqlConnection.ClearPool(npgsql);
        }
    }

    private static async Task SetAsync(DbConnection connection, string orgId, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        // Parameterised. Never string-concatenated: this value originates in a token.
        command.CommandText = "SELECT set_config(@name, @value, false)";
        AddParameter(command, "@name", SessionVariable);
        AddParameter(command, "@value", orgId);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task ClearAsync(DbConnection connection, CancellationToken ct)
    {
        if (connection.State != System.Data.ConnectionState.Open) return;

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT set_config(@name, '', false)";
        AddParameter(command, "@name", SessionVariable);
        await command.ExecuteNonQueryAsync(ct);
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
