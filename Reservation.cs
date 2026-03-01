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

    [Function("Payment")]
    public async Task<IActionResult> Payment([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "Payment")] HttpRequest req)
    {
        _logger.LogInformation("Processing payment request.");

        var conn = Environment.GetEnvironmentVariable("SqlConnectionString");
        if (string.IsNullOrWhiteSpace(conn))
        {
            _logger.LogError("SqlConnectionString not set in environment.");
            return new ObjectResult("Database connection not configured.") { StatusCode = 500 };
        }

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

            if (!root.TryGetProperty("reservationID", out var resIdEl) && !root.TryGetProperty("ReservationID", out resIdEl))
            {
                return new BadRequestObjectResult("reservationID is required.");
            }
            
            int reservationId;
            if (resIdEl.ValueKind == JsonValueKind.Number)
            {
                reservationId = resIdEl.GetInt32();
            }
            else if (!int.TryParse(resIdEl.GetString(), out reservationId))
            {
                return new BadRequestObjectResult("reservationID must be a valid integer.");
            }

            if (!root.TryGetProperty("amount", out var amountEl) && !root.TryGetProperty("Amount", out amountEl))
            {
                return new BadRequestObjectResult("amount is required.");
            }
            
            decimal amount;
            if (amountEl.ValueKind == JsonValueKind.Number)
            {
                amount = amountEl.GetDecimal();
            }
            else if (!decimal.TryParse(amountEl.GetString(), out amount))
            {
                return new BadRequestObjectResult("amount must be a valid number.");
            }

            var stripeKey = Environment.GetEnvironmentVariable("StripeSecretKey");
            if (string.IsNullOrWhiteSpace(stripeKey))
            {
                _logger.LogError("StripeSecretKey not set in environment.");
                return new ObjectResult("Payment service not configured.") { StatusCode = 500 };
            }

            Stripe.StripeConfiguration.ApiKey = stripeKey;

            var domain = Environment.GetEnvironmentVariable("DomainUrl") ?? $"{req.Scheme}://{req.Host}";

            var options = new Stripe.Checkout.SessionCreateOptions
            {
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
                {
                    new Stripe.Checkout.SessionLineItemOptions
                    {
                        PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                        {
                            UnitAmountDecimal = amount * 100, // Stripe expects amount in cents
                            Currency = "usd", 
                            ProductData = new Stripe.Checkout.SessionLineItemPriceDataProductDataOptions
                            {
                                Name = $"Reservation #{reservationId}",
                            },
                        },
                        Quantity = 1,
                    },
                },
                Mode = "payment",
                SuccessUrl = $"{domain}?session_id={{CHECKOUT_SESSION_ID}}",
                CancelUrl = $"{domain}",
            };

            var service = new Stripe.Checkout.SessionService();
            Stripe.Checkout.Session session = await service.CreateAsync(options);

            await using var connection = new SqlConnection(conn);
            await connection.OpenAsync();

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE Reservation SET Session = @sessionId WHERE ReservationID = @reservationId";
            cmd.Parameters.AddWithValue("@sessionId", session.Id);
            cmd.Parameters.AddWithValue("@reservationId", reservationId);

            int rowsUpdated = await cmd.ExecuteNonQueryAsync();
            if (rowsUpdated == 0)
            {
                _logger.LogWarning("ReservationID {ReservationId} not found in database.", reservationId);
                return new NotFoundObjectResult($"Reservation {reservationId} not found.");
            }

            return new OkObjectResult(new { url = session.Url });
        }
        catch (JsonException jex)
        {
            _logger.LogError(jex, "Invalid JSON in payment request body.");
            return new BadRequestObjectResult("Invalid JSON payload.");
        }
        catch (Stripe.StripeException stripeEx)
        {
            _logger.LogError(stripeEx, "Stripe API error.");
            return new ObjectResult("Payment service error.") { StatusCode = 500 };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing payment");
            return new ObjectResult("Error processing payment") { StatusCode = 500 };
        }
    }

    [Function("StripeWebhook")]
    public async Task<IActionResult> StripeWebhook([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "Webhook")] HttpRequest req)
    {
        _logger.LogInformation("Processing Stripe webhook.");

        var json = await new System.IO.StreamReader(req.Body).ReadToEndAsync();
        string signatureHeader = req.Headers["Stripe-Signature"].ToString();
        var endpointSecret = Environment.GetEnvironmentVariable("StripeWebhookSecret");

        if (string.IsNullOrEmpty(endpointSecret))
        {
            _logger.LogError("StripeWebhookSecret not set in environment.");
            return new ObjectResult("Webhook secret not configured.") { StatusCode = 500 };
        }

        try
        {
            var stripeEvent = Stripe.EventUtility.ConstructEvent(
                json,
                signatureHeader,
                endpointSecret
            );

            if (stripeEvent.Type == "checkout.session.completed")
            {
                var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
                if (session != null)
                {
                    _logger.LogInformation("Checkout session {SessionId} completed successfully.", session.Id);

                    // TODO: Update your database according to your schema (e.g. mark reservation as paid)
                    // var conn = Environment.GetEnvironmentVariable("SqlConnectionString");
                    // if (!string.IsNullOrWhiteSpace(conn))
                    // {
                    //     await using var connection = new SqlConnection(conn);
                    //     await connection.OpenAsync();
                    //     await using var cmd = connection.CreateCommand();
                    //     cmd.CommandText = "UPDATE Reservation SET Status = 'Paid' WHERE Session = @sessionId";
                    //     cmd.Parameters.AddWithValue("@sessionId", session.Id);
                    //     await cmd.ExecuteNonQueryAsync();
                    // }
                }
            }
            else
            {
                _logger.LogInformation("Unhandled event type: {EventType}", stripeEvent.Type);
            }

            return new OkResult();
        }
        catch (Stripe.StripeException e)
        {
            _logger.LogError(e, "Invalid Stripe signature.");
            return new BadRequestObjectResult("Invalid payload or signature.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing webhook");
            return new ObjectResult("Error processing webhook") { StatusCode = 500 };
        }
    }
}