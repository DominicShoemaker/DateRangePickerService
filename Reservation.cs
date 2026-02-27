using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;
using System.ClientModel.Primitives;

namespace DateRangePickerService;

public class Reservation
{
    private readonly ILogger<Reservation> _logger;

    public Reservation(ILogger<Reservation> logger)
    {
        _logger = logger;
    }

    [Function("Reservation")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = "Reservation/{user?}")] HttpRequest req, string user)
    {
        _logger.LogInformation("Processing reservation request and querying database.");

        var conn = Environment.GetEnvironmentVariable("SqlConnectionString");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogError("SqlConnectionString not set in environment.");
            return new BadRequestObjectResult("Database connection not configured.");
        }

        // POST - create new reservation
        if (string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = new System.IO.StreamReader(req.Body);
                var body = await reader.ReadToEndAsync();
                if (string.IsNullOrWhiteSpace(body))
                {
                    return new BadRequestObjectResult("Request body is empty.");
                }

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                string? source = null;
                if (root.TryGetProperty("Source", out var sourceEl) && sourceEl.ValueKind != JsonValueKind.Null)
                {
                    source = sourceEl.GetString();
                }

                if (!root.TryGetProperty("Email", out var emailEl) || emailEl.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(emailEl.GetString()))
                {
                    return new BadRequestObjectResult("Email is required.");
                }
                string email = emailEl.GetString()!;

                string? phone = null;
                if (root.TryGetProperty("Phone", out var phoneEl) && phoneEl.ValueKind != JsonValueKind.Null)
                {
                    phone = phoneEl.GetString();
                }

                if (!root.TryGetProperty("From", out var fromEl) || fromEl.ValueKind == JsonValueKind.Null || !fromEl.TryGetDateTime(out var fromDate))
                {
                    return new BadRequestObjectResult("From date is required and must be a valid date.");
                }

                if (!root.TryGetProperty("To", out var toEl) || toEl.ValueKind == JsonValueKind.Null || !toEl.TryGetDateTime(out var toDate))
                {
                    return new BadRequestObjectResult("To date is required and must be a valid date.");
                }

                await using var connection = new SqlConnection(conn);
                await connection.OpenAsync();

                await using var cmd = connection.CreateCommand();
                cmd.CommandText = @$"INSERT INTO Reservation ({(source == null?"":"Source,")} Email, {(phone == null?"":"Phone,")} [From], [To])
VALUES ({(source == null?"":"@source,")} @email, {(phone == null?"":"@phone,")} @from, @to);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                if(source != null) cmd.Parameters.AddWithValue("@source", source);
                cmd.Parameters.AddWithValue("@email", email);
                if(phone != null) cmd.Parameters.AddWithValue("@phone", phone);
                cmd.Parameters.AddWithValue("@from", fromDate.Date);
                cmd.Parameters.AddWithValue("@to", toDate.Date);

                var result = await cmd.ExecuteScalarAsync();
                int reservationId = result is int i ? i : Convert.ToInt32(result);

                // return 201 Created; location could point to a GET endpoint if available
                var created = new CreatedResult(string.Empty, new { ReservationID = reservationId });
                // If you have a route for getting a single reservation, set created.Location accordingly.
                return created;
            }
            catch (JsonException jex)
            {
                _logger.LogError(jex, "Invalid JSON in request body.");
                return new BadRequestObjectResult("Invalid JSON payload.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting reservation");
                return new ObjectResult("Error writing to database") { StatusCode = 500 };
            }
        }

        // GET - fetch existing reservations
        var results = new List<Dictionary<string, object?>>();
        try
        {
            await using var connection = new SqlConnection(conn);
            await connection.OpenAsync();

            // compute filter window: start tomorrow (so To > today) and end one year from today
            var today = DateTime.UtcNow.Date;
            var oneYearFromToday = today.AddYears(1);

            await using var cmd = connection.CreateCommand();
            if(user == "admin")
                cmd.CommandText = "SELECT * FROM Reservation";
            else
                cmd.CommandText = "SELECT [From], [To] FROM Reservation WHERE [To] > @today AND [From] < @oneYear ORDER BY [From] ASC";

            cmd.Parameters.AddWithValue("@today", today);
            cmd.Parameters.AddWithValue("@oneYear", oneYearFromToday);

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    var value = await reader.IsDBNullAsync(i) ? null : reader.GetValue(i);
                    row[reader.GetName(i)] = value;
                }
                results.Add(row);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Reservation table");
            _logger.LogError("Connection string used: {Conn}\nexception: {Exception}", conn, ex.ToString());
            return new ObjectResult("Error querying database") { StatusCode = 500 };
        }

        return new OkObjectResult(results);
    }

    
}