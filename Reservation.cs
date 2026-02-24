using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Data.SqlClient;

namespace DateRangePickerService;

public class Reservation
{
    private readonly ILogger<Reservation> _logger;

    public Reservation(ILogger<Reservation> logger)
    {
        _logger = logger;
    }

    [Function("Reservation")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Function, "get", "post")] HttpRequest req)
    {
        _logger.LogInformation("Processing reservation request and querying database.");

        var conn = Environment.GetEnvironmentVariable("SqlConnectionString");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogError("SqlConnectionString not set in environment.");
            return new BadRequestObjectResult("Database connection not configured.");
        }

        var results = new List<Dictionary<string, object?>>();

        try
        {
            await using var connection = new SqlConnection(conn);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT * FROM Reservation";

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
            return new ObjectResult("Error querying database") { StatusCode = 500 };
        }

        return new OkObjectResult(results);
    }
}