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

                string? fullName = null;
                if (root.TryGetProperty("fullName", out var fullNameEl) && fullNameEl.ValueKind != JsonValueKind.Null)
                {
                    fullName = fullNameEl.GetString();
                }

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
                cmd.CommandText = @$"INSERT INTO Reservation ({(source == null?"":"Source,")} {(fullName == null?"":"FullName,")} Email, {(phone == null?"":"Phone,")} [From], [To])
VALUES ({(source == null?"":"@source,")} {(fullName == null?"":"@fullName,")} @email, {(phone == null?"":"@phone,")} @from, @to);
SELECT CAST(SCOPE_IDENTITY() AS INT);";

                if(source != null) cmd.Parameters.AddWithValue("@source", source);
                cmd.Parameters.AddWithValue("@email", email);
                if(fullName != null) cmd.Parameters.AddWithValue("@fullName", fullName);
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

            var confirmationUrl = Environment.GetEnvironmentVariable("ConfirmationUrl") ?? $"{req.Scheme}://{req.Host}";

            DateTime fromDate;
            DateTime toDate;

            await using var connection = new SqlConnection(conn);
            await connection.OpenAsync();

            await using var selectCmd = connection.CreateCommand();
            selectCmd.CommandText = "SELECT [From], [To] FROM Reservation WHERE ReservationID = @reservationId";
            selectCmd.Parameters.AddWithValue("@reservationId", reservationId);
            await using (var dbReader = await selectCmd.ExecuteReaderAsync())
            {
                if (await dbReader.ReadAsync())
                {
                    fromDate = dbReader.GetDateTime(0);
                    toDate = dbReader.GetDateTime(1);
                }
                else
                {
                    return new NotFoundObjectResult($"Reservation {reservationId} not found.");
                }
            }

            // Compare amount with the price rules
            var calc = GetCalculator();
            var price = calc.GetTotalAndDiscountedPrice(fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"));
            if (amount != price.DiscountedPrice)
            {
                _logger.LogError("Amount {amount} does not match the price rules {price}", amount, price.DiscountedPrice);
                return new BadRequestObjectResult("amount does not match the price rules.");
            }

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
                SuccessUrl = $"{confirmationUrl}?status=success&reservationid={reservationId}&amount={amount}&from={fromDate:yyyy-MM-dd}&to={toDate:yyyy-MM-dd}",
                CancelUrl = $"{confirmationUrl}",
            };

            var service = new Stripe.Checkout.SessionService();
            Stripe.Checkout.Session session = await service.CreateAsync(options);

            await using var updateCmd = connection.CreateCommand();
            updateCmd.CommandText = "UPDATE Reservation SET Session = @sessionId WHERE ReservationID = @reservationId";
            updateCmd.Parameters.AddWithValue("@sessionId", session.Id);
            updateCmd.Parameters.AddWithValue("@reservationId", reservationId);

            int rowsUpdated = await updateCmd.ExecuteNonQueryAsync();
            if (rowsUpdated == 0)
            {
                _logger.LogWarning("ReservationID {ReservationId} not found in database.", reservationId);
                return new NotFoundObjectResult($"Reservation {reservationId} not found during update.");
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

                    // Update database - mark reservation as paid and fetch details for email
                    var conn = Environment.GetEnvironmentVariable("SqlConnectionString");
                    if (!string.IsNullOrWhiteSpace(conn))
                    {
                        await using var connection = new SqlConnection(conn);
                        await connection.OpenAsync();

                        // 1. Fetch Reservation details first
                        await using var selectCmd = connection.CreateCommand();
                        selectCmd.CommandText = @"SELECT ReservationID, FullName, Email, [From], [To] 
                                                  FROM Reservation 
                                                  WHERE Session = @sessionId";
                        selectCmd.Parameters.AddWithValue("@sessionId", session.Id);

                        int reservationId = 0;
                        string fullName = "Guest";
                        string email = "";
                        DateTime fromDate = default;
                        DateTime toDate = default;
                        bool found = false;

                        await using (var reader = await selectCmd.ExecuteReaderAsync())
                        {
                            if (await reader.ReadAsync())
                            {
                                reservationId = reader.GetInt32(0);
                                fullName = reader.IsDBNull(1) ? "Guest" : reader.GetString(1);
                                email = reader.GetString(2);
                                fromDate = reader.GetDateTime(3);
                                toDate = reader.GetDateTime(4);
                                found = true;
                            }
                        }

                        if (found)
                        {
                            // 2. Update status to 'Paid'
                            await using var updateCmd = connection.CreateCommand();
                            updateCmd.CommandText = "UPDATE Reservation SET Status = 'Paid' WHERE Session = @sessionId";
                            updateCmd.Parameters.AddWithValue("@sessionId", session.Id);
                            await updateCmd.ExecuteNonQueryAsync();
                            
                            int nights = (int)(toDate - fromDate).TotalDays;
                            decimal amountPaid = (session.AmountTotal ?? 0) / 100m;
                            
                            // Send Email via Azure Communication Services
                            var connectionString = Environment.GetEnvironmentVariable("COMMUNICATION_SERVICES_CONNECTION_STRING");

                            if (!string.IsNullOrEmpty(connectionString))
                            {
                                try
                                {
                                    var emailClient = new Azure.Communication.Email.EmailClient(connectionString);

                                    var subject = $"Booking Confirmation - Reservation #{reservationId}";
                                    var formattedAmount = amountPaid.ToString("C", System.Globalization.CultureInfo.CreateSpecificCulture("en-US"));
                                    var plainTextContent = $"Dear {fullName},\n\nThank you for your payment. Your booking has been confirmed.\n\nReservation Details:\nReservation ID: {reservationId}\nCheck-in: {fromDate:yyyy-MM-dd} after 3PM\nCheck-out: {toDate:yyyy-MM-dd} before 11AM\nTotal Nights: {nights}\nAmount Paid: {formattedAmount}\n\nWe look forward to hosting you!\n\nCasaDePedra.rio";
                                    var sender = "DoNotReply@casadepedra.rio";
                                    
                                    var emailMessage = new Azure.Communication.Email.EmailMessage(
                                        senderAddress: sender,
                                        recipientAddress: email,
                                        content: new Azure.Communication.Email.EmailContent(subject)
                                        {
                                            PlainText = plainTextContent
                                        });

                                    Azure.Communication.Email.EmailSendOperation emailSendOperation = await emailClient.SendAsync(
                                        Azure.WaitUntil.Started,
                                        emailMessage);

                                    _logger.LogInformation("Confirmation email is sent to {Email}. Operation Id: {OperationId}", email, emailSendOperation.Id);
                                }
                                catch (Exception emailEx)
                                {
                                    _logger.LogError(emailEx, "Failed to send confirmation email.");
                                }
                            }
                            else
                            {
                                _logger.LogWarning("COMMUNICATION_SERVICES_CONNECTION_STRING is missing. Cannot send confirmation email.");
                            }
                        }
                    }
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

    private PriceCalculator GetCalculator()
    {
        string rulesPath = Path.Combine(AppContext.BaseDirectory, "price_rules.json");
        var rules = PriceRules.CreateFromJson(rulesPath);
        return new PriceCalculator(rules);
    }
}