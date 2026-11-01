using System.Text.Json;
using GroceryScout.Core;
using Microsoft.Playwright;

namespace GroceryScout.Providers;

/// <summary>Raw text read from one product card; all fields are null when the card has not rendered yet.</summary>
public sealed record CardData(
    string? Key,
    string? Href,
    string? Title,
    string? Brand,
    string? Price,
    string? Size,
    string? Mrp,
    string? ImageAlt,
    string? Purchased,
    bool HasAdd,
    bool OutOfStock,
    // True when the card is currently on screen; some sites only fill a card in once it has been scrolled into view.
    bool InView = false);

/// <summary>
/// Reads every product card on the page in a single browser round-trip. Doing the work inside the page
/// (instead of several calls per card) is what makes searching fast.
/// </summary>
public static class CardExtractor
{
    private const string Script = """
        (els, c) => JSON.stringify(els.map(el => {
          // textContent needs no layout pass (innerText forces one, which is very slow on busy pages).
          const text = e => e ? (e.textContent ?? '').replace(/\s+/g, ' ').trim() : '';
          const find = (sel, pat) => {
            if (!sel) return null;
            const re = pat ? new RegExp(pat, 'i') : null;
            for (const e of el.querySelectorAll(sel)) {
              const t = text(e);
              if (t && (!re || re.test(t))) return t;
            }
            return null;
          };
          const has = (sel, pat) => {
            if (!sel) return false;
            return pat ? find(sel, pat) !== null : !!el.querySelector(sel);
          };
          const link = c.link ? el.querySelector(c.link) : null;
          const img = el.querySelector('img[alt]');
          return {
            key: c.idAttr ? (el.getAttribute(c.idAttr) || null) : null,
            href: link ? link.getAttribute('href') : null,
            title: find(c.title),
            brand: find(c.brand),
            price: find(c.price, c.pricePattern),
            size: find(c.size),
            mrp: find(c.mrp),
            imageAlt: img ? img.getAttribute('alt') : null,
            purchased: find(c.purchased),
            hasAdd: c.add ? (has(c.add, c.addPattern) || has(c.inCart)) : true,
            outOfStock: c.oos ? new RegExp(c.oos, 'i').test(el.innerText ?? '') : false,
            inView: (() => { const r = el.getBoundingClientRect(); return r.bottom > 0 && r.top < window.innerHeight; })(),
          };
        }))
        """;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<IReadOnlyList<CardData>> ExtractAsync(IPage page, SelectorConfig config)
    {
        var args = new
        {
            idAttr = config.ProductIdAttribute,
            link = config.CardLink,
            title = config.CardTitle,
            brand = config.CardBrand,
            price = config.CardPrice,
            pricePattern = config.CardPricePattern,
            size = config.CardSize,
            mrp = config.CardMrp,
            purchased = config.CardPurchasedBadge,
            add = config.CardAddButton,
            addPattern = config.CardAddButtonPattern,
            inCart = config.CardInCartSelector,
            oos = config.CardOutOfStockPattern,
        };
        var json = await page.Locator(config.ProductCard).EvaluateAllAsync<string>(Script, args);
        return JsonSerializer.Deserialize<List<CardData>>(json, Json) ?? [];
    }
}

/// <summary>Turns extracted card text into a listing. Pure logic, so it is unit tested without a browser.</summary>
public static class ListingBuilder
{
    /// <summary>
    /// Rendered=false means the card has no usable content yet (virtualized or still loading) and should be retried;
    /// Rendered=true with a null listing means it was skipped for good (irrelevant to the query, or no size).
    /// </summary>
    public readonly record struct Result(bool Rendered, ProductListing? Listing);

    public static Result Build(CardData card, SelectorConfig config, string provider, string query)
    {
        if (card.Title is null) return new Result(false, null);

        var title = card.Brand is not null && !card.Title.StartsWith(card.Brand, StringComparison.OrdinalIgnoreCase)
            ? $"{card.Brand} {card.Title}"
            : card.Title;

        // Most cards on a results page are unrelated products; skip them early.
        if (!QueryMatcher.IsRelevant(query, title)) return new Result(true, null);

        if (SelectorDrivenAdapter.ParsePrice(card.Price) is not { } price)
            // An unavailable product legitimately has no price: nothing to compare, nothing to wait for.
            return new Result(card.OutOfStock, null);

        // The pack size can render a moment after the title, so a missing size is retried later.
        var sizeText = card.Size ?? title;
        if (!SizeParser.TryParse(sizeText, out var size, out var unit) &&
            !SizeParser.TryParse(title, out size, out unit) &&
            !SizeParser.TryParse(card.ImageAlt ?? "", out size, out unit))
            return new Result(false, null);

        string url, productId;
        if (config.ProductIdAttribute.Length > 0)
        {
            if (string.IsNullOrWhiteSpace(card.Key)) return new Result(false, null);
            productId = card.Key;
            url = string.Format(config.ProductUrlTemplate, productId);
        }
        else
        {
            url = card.Href is null ? config.BaseUrl
                : Uri.TryCreate(card.Href, UriKind.Absolute, out var abs) ? abs.ToString()
                : new Uri(new Uri(config.BaseUrl), card.Href).ToString();
            productId = SelectorDrivenAdapter.ProductIdFromUrl(url);
        }

        return new Result(true, new ProductListing(
            provider, title, card.Brand,
            size, unit, price, SelectorDrivenAdapter.ParsePrice(card.Mrp),
            InStock: !card.OutOfStock && card.HasAdd, url, productId,
            Purchased: card.Purchased is not null,
            PurchasedNote: SelectorDrivenAdapter.PurchasedNote(card.Purchased)));
    }

    /// <summary>The id used to recognise a card across scroll rounds; null while the card is empty.</summary>
    public static string? KeyOf(CardData card, SelectorConfig config)
    {
        if (config.ProductIdAttribute.Length > 0)
            return string.IsNullOrWhiteSpace(card.Key) ? null : card.Key;
        return card.Href is null ? null : SelectorDrivenAdapter.ProductIdFromUrl(card.Href);
    }
}
