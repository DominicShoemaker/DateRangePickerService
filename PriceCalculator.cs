using System;
using System.Collections.Generic;

namespace DateRangePickerService
{
    // C# representations of the JSON structure in price_rules.json
    public class PriceRules
    {
        public decimal Default { get; set; } = 380m;
        public Dictionary<string, decimal> Days { get; set; } = new Dictionary<string, decimal>();
        public Dictionary<string, decimal> Dates { get; set; } = new Dictionary<string, decimal>();
        [System.Text.Json.Serialization.JsonPropertyName("discount_week")]
        public decimal DiscountWeek { get; set; } = 0.1m;
        [System.Text.Json.Serialization.JsonPropertyName("discount_month")]
        public decimal DiscountMonth { get; set; } = 0.3m;

        public static PriceRules CreateFromJson(string jsonFilePath)
        {
            if (!System.IO.File.Exists(jsonFilePath))
            {
                throw new System.IO.FileNotFoundException("Could not find price rules file.", jsonFilePath);
            }

            string jsonContent = System.IO.File.ReadAllText(jsonFilePath);
            var options = new System.Text.Json.JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };

            return System.Text.Json.JsonSerializer.Deserialize<PriceRules>(jsonContent, options) 
                   ?? new PriceRules();
        }
    }

    public class PriceTotals
    {
        public int Nights { get; set; }
        public decimal FullPrice { get; set; }
        public decimal DiscountedPrice { get; set; }
    }

    public class PriceCalculator
    {
        private readonly PriceRules _priceRules;

        public PriceCalculator(PriceRules priceRules)
        {
            _priceRules = priceRules ?? throw new ArgumentNullException(nameof(priceRules));
        }

        /// <summary>
        /// 1. Takes input as YYYY-MM-DD date and returns the price for this date
        /// </summary>
        public decimal GetPriceForDate(string dateStr)
        {
            if (!DateTime.TryParseExact(dateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime date))
            {
                throw new ArgumentException("Invalid date format. Expected YYYY-MM-DD", nameof(dateStr));
            }

            // Check specific overriding dates (Matches exactly YYYY-MM-DD keys from JSON)
            if (_priceRules.Dates != null && _priceRules.Dates.TryGetValue(dateStr, out decimal specificPrice))
            {
                return specificPrice;
            }

            // Check day of week (0 = Sunday, 1 = Monday, ..., 6 = Saturday to match JS logic)
            int dayOfWeek = (int)date.DayOfWeek;
            if (_priceRules.Days != null && _priceRules.Days.TryGetValue(dayOfWeek.ToString(), out decimal dayPrice))
            {
                return dayPrice;
            }

            // Fallback to default
            return _priceRules.Default;
        }

        /// <summary>
        /// 2. Takes input as start and end dates in YYYY-MM-DD and returns array of prices for this date range
        /// </summary>
        public List<decimal> GetPricesForDateRange(string startDateStr, string endDateStr)
        {
            if (!DateTime.TryParseExact(startDateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime startDate) ||
                !DateTime.TryParseExact(endDateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime endDate))
            {
                throw new ArgumentException("Invalid date format. Expected YYYY-MM-DD");
            }

            var prices = new List<decimal>();
            DateTime current = startDate;

            // Iterate strictly while current < endDate (checkout day is usually not charged)
            while (current < endDate)
            {
                prices.Add(GetPriceForDate(current.ToString("yyyy-MM-dd")));
                current = current.AddDays(1);
            }

            return prices;
        }

        /// <summary>
        /// 3. Takes input as start and end dates in YYYY-MM-DD and returns total price and discounted price
        /// </summary>
        public PriceTotals GetTotalAndDiscountedPrice(string startDateStr, string endDateStr)
        {
            if (!DateTime.TryParseExact(startDateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime startDate) ||
                !DateTime.TryParseExact(endDateStr, "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out DateTime endDate))
            {
                throw new ArgumentException("Invalid date format. Expected YYYY-MM-DD");
            }

            decimal fullPrice = 0m;
            DateTime current = startDate;
            
            while (current < endDate)
            {
                fullPrice += GetPriceForDate(current.ToString("yyyy-MM-dd"));
                current = current.AddDays(1);
            }

            int nights = (int)(endDate - startDate).TotalDays;
            decimal discountedPrice = fullPrice;

            // Apply discounts based on JS rules: >= 28 days for month discount, >= 7 days for week discount
            if (nights >= 28 && _priceRules.DiscountMonth > 0)
            {
                discountedPrice = fullPrice * (1m - _priceRules.DiscountMonth);
            }
            else if (nights >= 7 && _priceRules.DiscountWeek > 0)
            {
                discountedPrice = fullPrice * (1m - _priceRules.DiscountWeek);
            }

            return new PriceTotals
            {
                Nights = nights,
                FullPrice = fullPrice,
                // Round equivalent to JS Math.round()
                DiscountedPrice = Math.Round(discountedPrice, MidpointRounding.AwayFromZero)
            };
        }
    }
}
