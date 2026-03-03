using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using CasaDePedra.Pricing;

namespace DateRangePickerService
{
    public class DateRangePrice
    {
        private readonly ILogger<DateRangePrice> _logger;

        public DateRangePrice(ILogger<DateRangePrice> logger)
        {
            _logger = logger;
        }

        private PriceCalculator GetCalculator()
        {
            string rulesPath = Path.Combine(AppContext.BaseDirectory, "price_rules.json");
            var rules = PriceRules.CreateFromJson(rulesPath);
            return new PriceCalculator(rules);
        }

        [Function("GetPriceRules")]
        public async Task<IActionResult> GetPriceRules([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "price-rules")] HttpRequest req)
        {
            _logger.LogInformation("Getting price rules.");
            string rulesPath = Path.Combine(AppContext.BaseDirectory, "price_rules.json");
            
            if (!File.Exists(rulesPath))
            {
                return new NotFoundObjectResult("price_rules.json not found.");
            }

            var json = await File.ReadAllTextAsync(rulesPath);
            return new ContentResult 
            { 
                Content = json, 
                ContentType = "application/json",
                StatusCode = 200
            };
        }

        [Function("GetPriceForDate")]
        public IActionResult GetPriceForDate([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "price/{date}")] HttpRequest req, string date)
        {
            _logger.LogInformation("Getting price for date: {Date}", date);
            
            try
            {
                var calc = GetCalculator();
                var price = calc.GetPriceForDate(date);
                return new OkObjectResult(price);
            }
            catch (ArgumentException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting price for date.");
                return new ObjectResult("Internal server error") { StatusCode = 500 };
            }
        }

        [Function("GetPricesForDateRange")]
        public IActionResult GetPricesForDateRange([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "price/{from}/{to}")] HttpRequest req, string from, string to)
        {
            _logger.LogInformation("Getting prices for date range: {From} to {To}", from, to);
            
            try
            {
                var calc = GetCalculator();
                var prices = calc.GetPricesForDateRange(from, to);
                return new OkObjectResult(prices);
            }
            catch (ArgumentException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting prices for date range.");
                return new ObjectResult("Internal server error") { StatusCode = 500 };
            }
        }

        [Function("GetTotalAndDiscountedPrice")]
        public IActionResult GetTotalAndDiscountedPrice([HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "price/total/{from}/{to}")] HttpRequest req, string from, string to)
        {
            _logger.LogInformation("Getting total price for date range: {From} to {To}", from, to);
            
            try
            {
                var calc = GetCalculator();
                var totals = calc.GetTotalAndDiscountedPrice(from, to);
                return new OkObjectResult(totals);
            }
            catch (ArgumentException ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting total price.");
                return new ObjectResult("Internal server error") { StatusCode = 500 };
            }
        }
    }
}
