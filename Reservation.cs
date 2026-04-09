using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.ClientModel.Primitives;
using Ical.Net;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;
using System.IO;

namespace DateRangePickerService;

public class Reservation
{
    private static readonly System.Net.Http.HttpClient _httpClient = new System.Net.Http.HttpClient();
    private readonly ILogger<Reservation> _logger;

    public Reservation(ILogger<Reservation> logger)
    {
        _logger = logger;
    }

    private CalendarService GetCalendarService()
    {
        string? jsonKey = Environment.GetEnvironmentVariable("GCP_SERVICE_ACCOUNT_KEY");
        if (string.IsNullOrWhiteSpace(jsonKey))
        {
            var ex = new InvalidOperationException("GCP_SERVICE_ACCOUNT_KEY environment variable is not set.");
            _logger.LogError(ex, "Google service account key is required via environment variable.");
            throw ex;
        }

        var credential = GoogleCredential.FromJson(jsonKey).CreateScoped(CalendarService.Scope.Calendar);

        return new CalendarService(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
            ApplicationName = "Casa de Pedra - Google Calendar",
        });
    }

    private string GetCalendarId()
    {
        var id = Environment.GetEnvironmentVariable("GoogleCalendarId");
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new InvalidOperationException("GoogleCalendarId environment variable is not set.");
        }
        return id!;
    }

    [Function("Reservation")]
    public async Task<IActionResult> Run([HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", "delete", Route = "Reservation/{reservationid?}/{from?}/{to?}")] HttpRequest req, string? reservationid = null, string? from = null, string? to = null)
    {
        _logger.LogInformation("Processing reservation request with Google Calendar.");

        // POST - create new reservation
        if (string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                using var reader = new StreamReader(req.Body);
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

                var service = GetCalendarService();

                // Verify the requested date range is still available before creating a pending reservation
                if (!await IsRangeAvailable(service, fromDate, toDate))
                {
                    return new BadRequestObjectResult("Requested date range is not available.");
                }

                if (service == null)
                {
                    // Cannot create calendar event without Google Calendar service
                    _logger.LogError("Google Calendar service is not available; cannot create reservation.");
                    return new ObjectResult("Calendar service unavailable.") { StatusCode = 503 };
                }
                var newEvent = new Event()
                {
                    Summary = $"Pending - {fullName ?? "Guest"}",
                    Description = $"Email: {email}\nPhone: {phone ?? ""}\nSource: {source ?? ""}",
                    Start = new EventDateTime() { Date = fromDate.ToString("yyyy-MM-dd") },
                    End = new EventDateTime() { Date = toDate.ToString("yyyy-MM-dd") },
                    ExtendedProperties = new Event.ExtendedPropertiesData()
                    {
                        Private__ = new Dictionary<string, string>
                        {
                            { "Email", email },
                            { "FullName", fullName ?? "" },
                            { "Phone", phone ?? "" },
                            { "Source", source ?? "" },
                            { "Status", "Pending" }
                        }
                    }
                };

                var createdEvent = await service.Events.Insert(newEvent, GetCalendarId()).ExecuteAsync();

                return new CreatedResult(string.Empty, new { ReservationID = createdEvent.Id });
            }
            catch (JsonException jex)
            {
                _logger.LogError(jex, "Invalid JSON in request body.");
                return new BadRequestObjectResult("Invalid JSON payload.");
            }
            catch (FileNotFoundException fex)
            {
                _logger.LogError(fex, "Required file missing: {Message}", fex.Message);
                return new ObjectResult(fex.Message) { StatusCode = 500 };
            }
            catch (InvalidDataException idex)
            {
                _logger.LogError(idex, "Invalid configuration: {Message}", idex.Message);
                return new ObjectResult(idex.Message) { StatusCode = 500 };
            }
            catch (InvalidOperationException ioex)
            {
                _logger.LogError(ioex, "Configuration error: {Message}", ioex.Message);
                return new ObjectResult(ioex.Message) { StatusCode = 500 };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error inserting reservation in Calendar");
                return new ObjectResult("Error writing to calendar") { StatusCode = 500 };
            }
        }

        // DELETE - remove a reservation
        if (string.Equals(req.Method, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (string.IsNullOrWhiteSpace(reservationid))
                {
                    return new BadRequestObjectResult("reservationID is required and must be a valid string.");
                }

                if (string.IsNullOrWhiteSpace(from) || !DateTime.TryParse(from, out var fromDate))
                {
                    return new BadRequestObjectResult("From date is required and must be a valid date.");
                }

                if (string.IsNullOrWhiteSpace(to) || !DateTime.TryParse(to, out var toDate))
                {
                    return new BadRequestObjectResult("To date is required and must be a valid date.");
                }

                var service = GetCalendarService();
                var ev = await service.Events.Get(GetCalendarId(), reservationid).ExecuteAsync();
                
                string evStatus = "";
                string evSource = "";
                if (ev.ExtendedProperties?.Private__ != null)
                {
                    ev.ExtendedProperties.Private__.TryGetValue("Status", out evStatus);
                    ev.ExtendedProperties.Private__.TryGetValue("Source", out evSource);
                }

                if (evStatus == "Paid" || (evSource != "self" && !string.IsNullOrEmpty(evSource)))
                {
                    return new BadRequestObjectResult("Cannot delete a paid or external reservation.");
                }

                await service.Events.Delete(GetCalendarId(), reservationid).ExecuteAsync();

                return new OkObjectResult(new { Message = "Reservation deleted successfully." });
            }
            catch (Google.GoogleApiException)
            {
                return new NotFoundObjectResult("Reservation not found with the given parameters.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting reservation");
                return new ObjectResult("Error deleting from database") { StatusCode = 500 };
            }
        }

        // GET - fetch existing reservations
        var results = new List<Dictionary<string, object?>>();
        try
        {
            var today = DateTime.UtcNow.Date;
            var twoYearsFromToday = today.AddYears(2);

            var service = GetCalendarService();

            if (!string.IsNullOrWhiteSpace(reservationid) && reservationid != "0" && !string.IsNullOrWhiteSpace(from) && !string.IsNullOrWhiteSpace(to))
            {
                try
                {
                    var ev = await service.Events.Get(GetCalendarId(), reservationid).ExecuteAsync();
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    row["ReservationID"] = ev.Id;

                    string evSource = null, evFullName = null, evEmail = null, evPhone = null, evStatus = null;
                    if (ev.ExtendedProperties?.Private__ != null)
                    {
                        ev.ExtendedProperties.Private__.TryGetValue("Source", out evSource);
                        ev.ExtendedProperties.Private__.TryGetValue("FullName", out evFullName);
                        ev.ExtendedProperties.Private__.TryGetValue("Email", out evEmail);
                        ev.ExtendedProperties.Private__.TryGetValue("Phone", out evPhone);
                        ev.ExtendedProperties.Private__.TryGetValue("Status", out evStatus);
                    }

                    row["Source"] = string.IsNullOrEmpty(evSource) ? null : evSource;
                    row["FullName"] = string.IsNullOrEmpty(evFullName) ? null : evFullName;
                    row["Email"] = string.IsNullOrEmpty(evEmail) ? null : evEmail;
                    row["Phone"] = string.IsNullOrEmpty(evPhone) ? null : evPhone;
                    row["Status"] = string.IsNullOrEmpty(evStatus) ? null : evStatus;
                    row["From"] = DateTime.Parse(ev.Start.Date ?? ev.Start.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
                    row["To"] = DateTime.Parse(ev.End.Date ?? ev.End.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);

                    // If it is "Pending" and somebody else has booked the same dates, delete the "Pending" reservation
                    bool overlapWithReserved = false;
                    if (ev.Summary != null && ev.Summary.StartsWith("Pending", StringComparison.OrdinalIgnoreCase))
                    {
                        var reqStart = ((DateTime)row["From"]!).Date;
                        var reqEnd = ((DateTime)row["To"]!).Date;

                        // Check ICAL feeds
                        var calendarUrls = GetCalendarUrls();
                        foreach (var url in calendarUrls)
                        {
                            try
                            {
                                var response = await _httpClient.GetStringAsync(url);
                                var calendar = Ical.Net.Calendar.Load(response);
                                foreach (var calEv in calendar.Events)
                                {
                                    if (calEv.Summary != null && calEv.Summary.StartsWith("Reserved", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var evStart = calEv.DtStart.Value.Date;
                                        var evEnd = calEv.DtEnd.Value.Date;
                                        if (!(evEnd <= reqStart || evStart >= reqEnd))
                                        {
                                            overlapWithReserved = true;
                                            break;
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error reading calendar from {Url}", url);
                            }
                            if (overlapWithReserved) break;
                        }

                        // Check native calendar
                        if (!overlapWithReserved)
                        {
                            try
                            {
                                var request = service.Events.List(GetCalendarId());
                                request.TimeMinDateTimeOffset = reqStart;
                                request.TimeMaxDateTimeOffset = reqEnd;
                                request.SingleEvents = true;

                                var existingEvents = await request.ExecuteAsync();
                                if (existingEvents.Items != null)
                                {
                                    foreach (var existEv in existingEvents.Items)
                                    {
                                        if (existEv.Id == ev.Id) continue;
                                        
                                        if (existEv.Summary != null && existEv.Summary.StartsWith("Reserved", StringComparison.OrdinalIgnoreCase))
                                        {
                                            var existStart = DateTime.Parse(existEv.Start.Date ?? existEv.Start.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!).Date;
                                            var existEnd = DateTime.Parse(existEv.End.Date ?? existEv.End.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!).Date;
                                            if (!(existEnd <= reqStart || existStart >= reqEnd))
                                            {
                                                overlapWithReserved = true;
                                                break;
                                            }
                                        }
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error checking Google Native calendar for overlap.");
                            }
                        }

                        if (overlapWithReserved)
                        {
                            _logger.LogInformation("Pending event {EventId} overlaps with Reserved, deleting.", ev.Id);
                            try
                            {
                                await service.Events.Delete(GetCalendarId(), ev.Id).ExecuteAsync();
                            }
                            catch (Exception delEx)
                            {
                                _logger.LogError(delEx, "Failed to delete Pending event");
                            }
                        }
                    }

                    if (!overlapWithReserved)
                    {
                        results.Add(row);
                    }
                }
                catch (Google.GoogleApiException)
                {
                    // Not found, do nothing
                }
                catch (FileNotFoundException fex)
                {
                    _logger.LogError(fex, "Required file missing: {Message}", fex.Message);
                    return new ObjectResult(fex.Message) { StatusCode = 500 };
                }
                catch (InvalidOperationException ioex)
                {
                    _logger.LogError(ioex, "Configuration error: {Message}", ioex.Message);
                    return new ObjectResult(ioex.Message) { StatusCode = 500 };
                }
            }
            else
            {
                var calendarUrls = GetCalendarUrls();

                // Add existing ICAL fetching
                foreach (var url in calendarUrls)
                {
                    try
                    {
                        var response = await _httpClient.GetStringAsync(url);
                        var calendar = Ical.Net.Calendar.Load(response);
                        foreach (var ev in calendar.Events)
                        {
                            if (ev.Summary != null && ev.Summary.StartsWith("Reserved", StringComparison.OrdinalIgnoreCase))
                            {
                                var startDate = ev.DtStart.Value.Date;
                                var endDate = ev.DtEnd.Value.Date;
                                
                                if (endDate > today && startDate < twoYearsFromToday)
                                {
                                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                                    row["From"] = startDate;
                                    row["To"] = endDate;
                                    results.Add(row);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error reading calendar from {Url}", url);
                    }
                }

                // Add items from the new Google Account native calendar
                try {
                    var request = service.Events.List(GetCalendarId());
                    request.TimeMinDateTimeOffset = today;
                    request.TimeMaxDateTimeOffset = twoYearsFromToday;
                    request.SingleEvents = true;
                    request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

                    var newEvents = await request.ExecuteAsync();
                    foreach (var ev in newEvents.Items)
                    {
                        if (ev.Summary != null && (ev.Summary.StartsWith("Reserved", StringComparison.OrdinalIgnoreCase) || ev.Summary.StartsWith("Pending", StringComparison.OrdinalIgnoreCase)))
                        {
                            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                            row["From"] = DateTime.Parse(ev.Start.Date ?? ev.Start.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
                            row["To"] = DateTime.Parse(ev.End.Date ?? ev.End.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
                            results.Add(row);
                        }
                    }
                }
                catch (FileNotFoundException fex)
                {
                    _logger.LogError(fex, "Required file missing: {Message}", fex.Message);
                    return new ObjectResult(fex.Message) { StatusCode = 500 };
                }
                catch (InvalidOperationException ioex)
                {
                    _logger.LogError(ioex, "Configuration error: {Message}", ioex.Message);
                    return new ObjectResult(ioex.Message) { StatusCode = 500 };
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Failed aggregating from native google calendar");
                }
                
                results.Sort((a, b) => ((DateTime)a["From"]!).CompareTo((DateTime)b["From"]!));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error querying Calendar");
            return new ObjectResult("Error querying database") { StatusCode = 500 };
        }

        return new OkObjectResult(results);
    }

    [Function("Payment")]
    public async Task<IActionResult> Payment([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "Payment")] HttpRequest req)
    {
        _logger.LogInformation("Processing payment request via Calendar.");

        try
        {
            using var reader = new StreamReader(req.Body);
            var body = await reader.ReadToEndAsync();
            _logger.LogInformation("Payment request body: {Body}", body);
            
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
            
            string reservationId = resIdEl.ValueKind == JsonValueKind.Number ? resIdEl.GetInt32().ToString() : resIdEl.GetString()!;
            _logger.LogInformation("Payment for reservation: {ReservationId}", reservationId);

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
            _logger.LogInformation("Payment amount: {Amount}", amount);

            var stripeKey = Environment.GetEnvironmentVariable("StripeSecretKey");
            if (string.IsNullOrWhiteSpace(stripeKey))
            {
                _logger.LogError("StripeSecretKey not set in environment.");
                return new ObjectResult("Payment service not configured.") { StatusCode = 500 };
            }

            Stripe.StripeConfiguration.ApiKey = stripeKey;
            var confirmationUrl = Environment.GetEnvironmentVariable("ConfirmationUrl") ?? $"{req.Scheme}://{req.Host}";

            var service = GetCalendarService();
            Event ev;
            try
            {
                _logger.LogInformation("Fetching event {ReservationId} from calendar", reservationId);
                ev = await service.Events.Get(GetCalendarId(), reservationId).ExecuteAsync();
                _logger.LogInformation("Event found: {EventTitle}", ev.Summary);
            }
            catch (Exception calEx)
            {
                _logger.LogError(calEx, "Failed to fetch reservation {ReservationId}", reservationId);
                return new NotFoundObjectResult($"Reservation {reservationId} not found.");
            }

            DateTime fromDate = DateTime.Parse(ev.Start.Date ?? ev.Start.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
            DateTime toDate = DateTime.Parse(ev.End.Date ?? ev.End.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
            _logger.LogInformation("Event dates: {FromDate} to {ToDate}", fromDate, toDate);

            var calc = GetCalculator();
            var price = calc.GetTotalAndDiscountedPrice(fromDate.ToString("yyyy-MM-dd"), toDate.ToString("yyyy-MM-dd"));
            _logger.LogInformation("Calculated price: {DiscountedPrice}, Amount provided: {Amount}", price.DiscountedPrice, amount);
            
            if (amount != price.DiscountedPrice)
            {
                _logger.LogError("Amount {amount} does not match the price rules {price}", amount, price.DiscountedPrice);
                return new BadRequestObjectResult("amount does not match the price rules.");
            }

            var options = new Stripe.Checkout.SessionCreateOptions
            {
                ExpiresAt = DateTime.UtcNow.AddHours(1),
                PaymentMethodTypes = new List<string> { "card" },
                LineItems = new List<Stripe.Checkout.SessionLineItemOptions>
                {
                    new Stripe.Checkout.SessionLineItemOptions
                    {
                        PriceData = new Stripe.Checkout.SessionLineItemPriceDataOptions
                        {
                            UnitAmountDecimal = amount * 100,
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

            _logger.LogInformation("Creating Stripe session for reservation {ReservationId}", reservationId);
            var stripeService = new Stripe.Checkout.SessionService();
            Stripe.Checkout.Session session = await stripeService.CreateAsync(options);
            _logger.LogInformation("Stripe session created: {SessionId}", session.Id);

            if (ev.ExtendedProperties == null) ev.ExtendedProperties = new Event.ExtendedPropertiesData();
            if (ev.ExtendedProperties.Private__ == null) ev.ExtendedProperties.Private__ = new Dictionary<string, string>();
            ev.ExtendedProperties.Private__["SessionId"] = session.Id;
            
            await service.Events.Update(ev, GetCalendarId(), ev.Id).ExecuteAsync();
            _logger.LogInformation("Updated event with session ID: {SessionId}", session.Id);

            return new OkObjectResult(new { url = session.Url });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing payment - Exception type: {ExceptionType}, Message: {Message}, StackTrace: {StackTrace}", ex.GetType().Name, ex.Message, ex.StackTrace);
            return new ObjectResult("Error processing payment") { StatusCode = 500 };
        }
    }

    [Function("StripeWebhook")]
    public async Task<IActionResult> StripeWebhook([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "Webhook")] HttpRequest req)
    {
        _logger.LogInformation("Processing Stripe webhook.");

        var json = await new StreamReader(req.Body).ReadToEndAsync();
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

                    var service = GetCalendarService();
                    var request = service.Events.List(GetCalendarId());
                    request.PrivateExtendedProperty = new Google.Apis.Util.Repeatable<string>(new[] { $"SessionId={session.Id}" });
                    var events = await request.ExecuteAsync();

                    if (events.Items != null && events.Items.Count > 0)
                    {
                        var ev = events.Items[0];
                        ev.Summary = ev.Summary.Replace("Pending -", "Reserved -");
                        ev.ExtendedProperties.Private__["Status"] = "Paid";
                        await service.Events.Update(ev, GetCalendarId(), ev.Id).ExecuteAsync();
                        
                        string reservationId = ev.Id;
                        string fullName = ev.ExtendedProperties.Private__.ContainsKey("FullName") ? ev.ExtendedProperties.Private__["FullName"] : "Guest";
                        string email = ev.ExtendedProperties.Private__.ContainsKey("Email") ? ev.ExtendedProperties.Private__["Email"] : "";
                        DateTime fromDate = DateTime.Parse(ev.Start.Date ?? ev.Start.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
                        DateTime toDate = DateTime.Parse(ev.End.Date ?? ev.End.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
                        
                        int nights = (int)(toDate - fromDate).TotalDays;
                        decimal amountPaid = (session.AmountTotal ?? 0) / 100m;
                        
                        var connectionString = Environment.GetEnvironmentVariable("COMMUNICATION_SERVICES_CONNECTION_STRING");
                        if (!string.IsNullOrEmpty(connectionString) && !string.IsNullOrEmpty(email))
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

                                await emailClient.SendAsync(Azure.WaitUntil.Started, emailMessage);
                                _logger.LogInformation("Confirmation email is sent to {Email}.", email);
                            }
                            catch (Exception emailEx)
                            {
                                _logger.LogError(emailEx, "Failed to send confirmation email.");
                            }
                        }
                    }
                }
            }
            else if (stripeEvent.Type == "checkout.session.expired")
            {
                var session = stripeEvent.Data.Object as Stripe.Checkout.Session;
                if (session != null)
                {
                    _logger.LogInformation("Checkout session {SessionId} expired.", session.Id);

                    var service = GetCalendarService();
                    var request = service.Events.List(GetCalendarId());
                    request.PrivateExtendedProperty = new Google.Apis.Util.Repeatable<string>(new[] { $"SessionId={session.Id}" });
                    var events = await request.ExecuteAsync();

                    if (events.Items != null && events.Items.Count > 0)
                    {
                        var ev = events.Items[0];
                        await service.Events.Delete(GetCalendarId(), ev.Id).ExecuteAsync();
                        _logger.LogInformation("Deleted expired session {SessionId}.", session.Id);
                    }
                }
            }

            return new OkResult();
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

    private IEnumerable<string> GetCalendarUrls()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "calendars.json");
        if (!File.Exists(path))
        {
            var ex = new FileNotFoundException("calendars.json not found.", path);
            _logger.LogError(ex, "Required calendars.json is missing.");
            throw ex;
        }

        var txt = File.ReadAllText(path);
        var urls = JsonSerializer.Deserialize<List<string>>(txt);
        if (urls == null || urls.Count == 0)
        {
            var ex = new InvalidDataException("calendars.json is empty or invalid.");
            _logger.LogError(ex, "Required calendars.json is invalid.");
            throw ex;
        }

        return urls;
    }

    private async Task<bool> IsRangeAvailable(CalendarService service, DateTime fromDate, DateTime toDate)
    {
        // Normalize to date-only
        var reqStart = fromDate.Date;
        var reqEnd = toDate.Date;

        // Check ICAL feeds
        var calendarUrls = GetCalendarUrls();
        foreach (var url in calendarUrls)
        {
            try
            {
                var response = await _httpClient.GetStringAsync(url);
                var calendar = Ical.Net.Calendar.Load(response);
                foreach (var ev in calendar.Events)
                {
                    var summary = ev.Summary ?? string.Empty;
                    if (!summary.StartsWith("Reserved", StringComparison.OrdinalIgnoreCase) && !summary.StartsWith("Pending", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var evStart = ev.DtStart.Value.Date;
                    var evEnd = ev.DtEnd.Value.Date;

                    // Overlap if not (evEnd <= reqStart || evStart >= reqEnd)
                    if (!(evEnd <= reqStart || evStart >= reqEnd))
                    {
                        return false;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading calendar from {Url}", url);
            }
        }

        // Check Google native calendar
        try
        {
            var request = service.Events.List(GetCalendarId());
            request.TimeMinDateTimeOffset = reqStart;
            request.TimeMaxDateTimeOffset = reqEnd;
            request.SingleEvents = true;
            request.OrderBy = EventsResource.ListRequest.OrderByEnum.StartTime;

            var newEvents = await request.ExecuteAsync();
            if (newEvents.Items != null)
            {
                foreach (var ev in newEvents.Items)
                {
                    if (ev.Summary == null) continue;
                    if (!(ev.Summary.StartsWith("Reserved", StringComparison.OrdinalIgnoreCase) || ev.Summary.StartsWith("Pending", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var evStart = DateTime.Parse(ev.Start.Date ?? ev.Start.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);
                    var evEnd = DateTime.Parse(ev.End.Date ?? ev.End.DateTimeDateTimeOffset?.ToString("yyyy-MM-dd")!);

                    if (!(evEnd <= reqStart || evStart >= reqEnd))
                    {
                        return false;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed aggregating from native google calendar when checking availability");
        }

        return true;
    }
}