using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Clodlogs.Desktop.Models;

namespace Clodlogs.Desktop.Services;

public sealed class AnthropicPricingService
{
    public const string PricingSourceUrl = "https://platform.claude.com/docs/en/about-claude/pricing";
    private const string PricingMarkdownUrl = PricingSourceUrl + ".md";
    private static readonly Regex TableRowRegex = new(@"^\s*\|(?<row>.+)\|\s*$", RegexOptions.Multiline);
    private static readonly Regex HtmlTagRegex = new("<.*?>", RegexOptions.Singleline);
    private static readonly Regex PriceRegex = new(@"\$(?<price>\d+(?:\.\d+)?)", RegexOptions.CultureInvariant);

    public static AnthropicPricing DefaultPricing()
        => new(PricingSourceUrl, null,
        [
            new AnthropicModelPrice("Claude Fable 5", 10m, 12.50m, 20m, 1m, 50m),
            new AnthropicModelPrice("Claude Mythos 5", 10m, 12.50m, 20m, 1m, 50m),
            new AnthropicModelPrice("Claude Opus 5", 5m, 6.25m, 10m, 0.50m, 25m),
            new AnthropicModelPrice("Claude Opus 4.8", 5m, 6.25m, 10m, 0.50m, 25m),
            new AnthropicModelPrice("Claude Opus 4.7", 5m, 6.25m, 10m, 0.50m, 25m),
            new AnthropicModelPrice("Claude Opus 4.6", 5m, 6.25m, 10m, 0.50m, 25m),
            new AnthropicModelPrice("Claude Opus 4.5", 5m, 6.25m, 10m, 0.50m, 25m),
            new AnthropicModelPrice("Claude Opus 4.1", 15m, 18.75m, 30m, 1.50m, 75m),
            new AnthropicModelPrice("Claude Opus 4", 15m, 18.75m, 30m, 1.50m, 75m),
            new AnthropicModelPrice("Claude Sonnet 5", 2m, 2.50m, 4m, 0.20m, 10m),
            new AnthropicModelPrice("Claude Sonnet 4.6", 3m, 3.75m, 6m, 0.30m, 15m),
            new AnthropicModelPrice("Claude Sonnet 4.5", 3m, 3.75m, 6m, 0.30m, 15m),
            new AnthropicModelPrice("Claude Sonnet 4", 3m, 3.75m, 6m, 0.30m, 15m),
            new AnthropicModelPrice("Claude Haiku 4.5", 1m, 1.25m, 2m, 0.10m, 5m),
            new AnthropicModelPrice("Claude Haiku 3.5", 0.80m, 1m, 1.60m, 0.08m, 4m)
        ]);

    public async Task<AnthropicPricing> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var response = await client.GetAsync(PricingMarkdownUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        var document = await response.Content.ReadAsStringAsync(cancellationToken);
        var parsed = ParsePricingDocument(document);
        if (parsed.Models.Count == 0)
        {
            throw new InvalidOperationException("The Anthropic pricing page did not contain a recognizable model pricing table.");
        }

        return parsed with { RefreshedAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) };
    }

    public static AnthropicPricing ParsePricingDocument(string document)
    {
        var models = new List<AnthropicModelPrice>();
        foreach (Match match in TableRowRegex.Matches(document))
        {
            var cells = match.Groups["row"].Value
                .Split('|')
                .Select(CleanCell)
                .Where(cell => cell.Length > 0)
                .ToArray();
            if (cells.Length < 5 || !cells[0].Contains("Claude", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var model = CanonicalizeModelName(cells[0]);
            if (model is null || !IsCurrentlyApplicablePrice(cells[0]))
            {
                continue;
            }

            var prices = cells
                .Skip(1)
                .Select(ExtractPrice)
                .Where(price => price.HasValue)
                .Select(price => price!.Value)
                .ToArray();
            if (prices.Length < 5)
            {
                continue;
            }

            var parsed = new AnthropicModelPrice(model, prices[0], prices[1], prices[2], prices[3], prices[4]);
            var existingIndex = models.FindIndex(price => string.Equals(price.Model, model, StringComparison.OrdinalIgnoreCase));
            if (existingIndex >= 0)
            {
                models[existingIndex] = parsed;
            }
            else
            {
                models.Add(parsed);
            }
        }

        return new AnthropicPricing(PricingSourceUrl, null, models);
    }

    public static AnthropicModelPrice? FindPrice(AnthropicPricing pricing, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return null;
        }

        var normalizedModel = NormalizeModelName(model);
        var orderedPrices = pricing.Models
            .OrderByDescending(price => NormalizeModelName(price.Model).Length)
            .ToArray();
        foreach (var price in orderedPrices)
        {
            var normalizedPrice = NormalizeModelName(price.Model);
            if (normalizedModel.Contains(normalizedPrice, StringComparison.Ordinal))
            {
                return price;
            }
        }

        return orderedPrices.FirstOrDefault(price => MatchesFamilyAndVersion(model, price.Model));
    }

    public static decimal CalculateCost(SessionTokenUsage usage, AnthropicModelPrice price)
    {
        const decimal oneMillion = 1_000_000m;
        return (usage.InputTokens / oneMillion * price.InputPerMillionTokens)
            + (usage.CacheCreation5MinuteInputTokens / oneMillion * price.CacheWrite5MinutePerMillionTokens)
            + (usage.CacheCreation1HourInputTokens / oneMillion * price.CacheWrite1HourPerMillionTokens)
            + (usage.CacheReadInputTokens / oneMillion * price.CacheReadPerMillionTokens)
            + (usage.OutputTokens / oneMillion * price.OutputPerMillionTokens);
    }

    private static string CleanCell(string value)
    {
        var decoded = WebUtility.HtmlDecode(value);
        decoded = HtmlTagRegex.Replace(decoded, " ");
        decoded = decoded.Replace("*", "", StringComparison.Ordinal);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static decimal? ExtractPrice(string cell)
    {
        var match = PriceRegex.Match(cell);
        if (!match.Success)
        {
            return null;
        }

        return decimal.TryParse(match.Groups["price"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static string? CanonicalizeModelName(string value)
    {
        var match = Regex.Match(
            value,
            @"Claude\s+(?:Fable|Mythos|Opus|Sonnet|Haiku)\s+\d+(?:\.\d+)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    private static bool IsCurrentlyApplicablePrice(string modelCell)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (modelCell.Contains("through August 31, 2026", StringComparison.OrdinalIgnoreCase))
        {
            return today <= new DateOnly(2026, 8, 31);
        }
        if (modelCell.Contains("starting September 1, 2026", StringComparison.OrdinalIgnoreCase))
        {
            return today >= new DateOnly(2026, 9, 1);
        }

        return true;
    }

    private static string NormalizeModelName(string value)
        => Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]", "");

    private static bool MatchesFamilyAndVersion(string model, string price)
    {
        var modelIdentity = ParseModelIdentity(model);
        var priceIdentity = ParseModelIdentity(price);
        return modelIdentity is not null
            && priceIdentity is not null
            && string.Equals(modelIdentity.Value.Family, priceIdentity.Value.Family, StringComparison.Ordinal)
            && modelIdentity.Value.Major == priceIdentity.Value.Major
            && modelIdentity.Value.Minor == priceIdentity.Value.Minor;
    }

    private static (string Family, int Major, int Minor)? ParseModelIdentity(string value)
    {
        var lower = value.ToLowerInvariant();
        foreach (var family in new[] { "opus", "sonnet", "haiku", "fable", "mythos" })
        {
            if (!lower.Contains(family, StringComparison.Ordinal))
            {
                continue;
            }

            var familyFirst = Regex.Match(
                lower,
                $@"{family}[^0-9]*(?<major>\d+)(?:[-.](?<minor>\d+))?",
                RegexOptions.CultureInvariant);
            var versionFirst = Regex.Match(
                lower,
                $@"claude[^0-9]*(?<major>\d+)(?:[-.](?<minor>\d+))?[^a-z]*{family}",
                RegexOptions.CultureInvariant);
            var match = familyFirst.Success ? familyFirst : versionFirst;
            if (match.Success
                && int.TryParse(match.Groups["major"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major))
            {
                var minor = int.TryParse(match.Groups["minor"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedMinor)
                    ? parsedMinor
                    : 0;
                return (family, major, minor);
            }
        }

        return null;
    }
}
